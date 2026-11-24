using System.Text.Json.Nodes;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Handoff;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Session;

/// <summary>
/// A mapping being worked on: the mapping, the old and new schemas, the scan used as evidence, the undo history, the
/// last profile, dry run and migration, and every operation on them. The window and AI agents both work through one
/// (the window's), so what an agent does is what a person could do, with the same checks and the same undo.
/// Operations say what happened through <see cref="Said"/> and tell whoever shows the mapping through <see cref="Changed"/>.
/// </summary>
public sealed class MapSession(SavedConnections connections)
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    public SavedConnections Connections { get; } = connections;
    public MapFile Map { get; private set; } = new();
    public string? MapPath { get; private set; }
    public DatabaseSchema? Old { get; private set; }
    public DatabaseSchema? New { get; private set; }
    public Evidence? Evidence { get; private set; }
    public bool Dirty { get; private set; }
    public string? LastChange { get; private set; }
    public TableProfile? Profile { get; private set; }
    public List<TableRun>? LastRun { get; private set; }

    /// <summary>"dry run" or "migration": what <see cref="LastRun"/> was.</summary>
    public string LastRunKind { get; private set; } = "dry run";
    public MigrationResult? LastMigration { get; private set; }
    public List<RunRecord> Runs { get; private set; } = [];
    public string? LastSaid { get; private set; }
    public int UndoSteps => _undo.Count;

    /// <summary>What happened, for the person (the status line) or an agent.</summary>
    public event Action<string>? Said;

    /// <summary>The mapping, a schema or a run changed: refresh what shows it.</summary>
    public event Action? Changed;

    private void Say(string text)
    {
        LastSaid = text;
        Said?.Invoke(text);
    }

    private void Notify() => Changed?.Invoke();

    public List<MapFinding> Findings => Old != null && New != null ? MapChecks.Check(Map, Old, New) : [];
    public List<CoverageItem> Coverage => Mapping.Coverage.Of(Map, Old, New, Evidence);

    // ───────────────────────────── opening and saving ─────────────────────────────

    public void NewMap()
    {
        Map = new MapFile();
        MapPath = null;
        Old = New = null;
        Evidence = null;
        Profile = null;
        LastRun = null;
        LastMigration = null;
        Runs = [];
        _undo.Clear();
        _redo.Clear();
        Dirty = false;
        Notify();
        Say("A new mapping: open the old and new schemas (File menu).");
    }

    /// <summary>Opens a mapping and the schemas it names (files beside it, or saved connections called old and new).</summary>
    public bool OpenMap(string path)
    {
        try
        {
            Map = MapFile.Load(path);
            MapPath = Path.GetFullPath(path);
            _undo.Clear();
            _redo.Clear();
            Dirty = false;
            Profile = null;
            LastRun = null;
            LastMigration = null;
            var said = new List<string>();
            LoadEvidence();
            Old = LoadSide(Map.Source, said);
            New = LoadSide(Map.Target, said);
            LoadRuns();
            Notify();
            Say($"Opened {Path.GetFileName(path)}: {Map.Tables.Count} tables mapped." + (said.Count > 0 ? " " + string.Join(" ", said) : ""));
            return true;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Say($"Couldn't open {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    private DatabaseSchema? LoadSide(Side side, List<string> said)
    {
        try
        {
            if (MapFile.FullPath(side, MapPath) is { } file) return SchemaFiles.Load(file, side.Dialect == Dialect.Sqlite && SchemaFiles.IsSqlite(file) ? null : side.Dialect);
            if (Connections.Get(side.Name) is { } saved)
            {
                using var c = Catalogs.Open(saved.Dialect, saved.Connection);
                return Catalogs.Read(c, saved.Dialect, Catalogs.Describe(saved.Dialect, saved.Connection));
            }
            said.Add($"The {side.Name} database ({side.Described ?? side.Dialect.ToString()}) isn't connected: File › Connect to the {(side == Map.Source ? "Old" : "New")} Database.");
        }
        catch (Exception e) when (e is IOException or FormatException or System.Data.Common.DbException or InvalidOperationException or ArgumentException)
        {
            said.Add($"The {side.Name} schema couldn't be read: {e.Message}");
        }
        return null;
    }

    /// <summary>Opens a schema from a file (a SQLite database or a DDL script) as the old or the new side.</summary>
    public bool OpenSchema(bool old, string path, Dialect? dialect = null)
    {
        try
        {
            var schema = SchemaFiles.Load(path, dialect);
            var side = old ? Map.Source : Map.Target;
            if (old) Old = schema;
            else New = schema;
            Change($"opened the {(old ? "old" : "new")} schema", () =>
            {
                side.Dialect = schema.Dialect;
                side.File = Relative(Path.GetFullPath(path));
                side.Described = schema.Source;
            });
            if (!old) LoadRuns();
            Say($"The {(old ? "old" : "new")} schema: {schema.Tables.Count} tables from {schema.Source} ({schema.Dialect}).");
            return true;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            Say($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    /// <summary>Reads a live database's schema as the old or new side, and keeps the connection (by name, outside the mapping).</summary>
    public bool ConnectSchema(bool old, Dialect dialect, string connection)
    {
        try
        {
            var described = Catalogs.Describe(dialect, connection);
            DatabaseSchema schema;
            using (var c = Catalogs.Open(dialect, connection)) schema = Catalogs.Read(c, dialect, described);
            var side = old ? Map.Source : Map.Target;
            Connections.Set(side.Name, dialect, connection);
            if (old) Old = schema;
            else New = schema;
            Change($"connected the {(old ? "old" : "new")} database", () =>
            {
                side.Dialect = dialect;
                side.File = null;
                side.Described = described;
            });
            if (!old) LoadRuns();
            Say($"The {(old ? "old" : "new")} database {described}: {schema.Tables.Count} tables. The connection is kept in your settings, not in the mapping.");
            return true;
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException or ArgumentException or IOException)
        {
            Say($"Couldn't connect: {e.Message}");
            return false;
        }
    }

    /// <summary>A file as the mapping keeps it: relative to the mapping file when it's saved.</summary>
    public string Relative(string full) => MapPath == null ? full : Path.GetRelativePath(Path.GetDirectoryName(MapPath)!, full).Replace('\\', '/');

    public bool SaveMap(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // Files the sides read stay found: relative to where the mapping is now.
            foreach (var side in new[] { Map.Source, Map.Target })
                if (MapFile.FullPath(side, MapPath) is { } f) side.File = Path.GetRelativePath(Path.GetDirectoryName(full)!, f).Replace('\\', '/');
            if (Map.Evidence != null && MapFile.FullPath(new Side { File = Map.Evidence }, MapPath) is { } ev) Map.Evidence = Path.GetRelativePath(Path.GetDirectoryName(full)!, ev).Replace('\\', '/');
            Map.Save(full);
            MapPath = full;
            Dirty = false;
            Notify();
            Say($"Saved {Path.GetFileName(full)}.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save: {e.Message}");
            return false;
        }
    }

    private void LoadEvidence()
    {
        Evidence = null;
        if (Map.Evidence == null) return;
        var path = MapFile.FullPath(new Side { File = Map.Evidence }, MapPath);
        try
        {
            if (path != null) Evidence = Evidence.Load(path);
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Say($"The scan {Map.Evidence} couldn't be read: {e.Message}");
        }
    }

    /// <summary>Uses an AVALegacyLens scan of the old system as evidence (kept in the mapping, as a path).</summary>
    public bool OpenEvidence(string path)
    {
        try
        {
            var ev = Evidence.Load(path);
            Evidence = ev;
            Change("opened a scan of the old system", () => Map.Evidence = Relative(Path.GetFullPath(path)));
            Say($"The old system's code ({ev.Source}) is evidence now: suggestions use its rules, and coverage says what it reads and writes.");
            return true;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Say($"Couldn't read {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    // ───────────────────────────── changing the mapping ─────────────────────────────

    /// <summary>Makes a change to the mapping as one undo step.</summary>
    public void Change(string what, Action change)
    {
        _undo.Push(Map.ToJson());
        _redo.Clear();
        change();
        Dirty = true;
        LastChange = what;
        Notify();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) { Say("Nothing to undo."); return false; }
        _redo.Push(Map.ToJson());
        Map = MapFile.Read(_undo.Pop());
        Dirty = true;
        Notify();
        Say("Undone.");
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) { Say("Nothing to redo."); return false; }
        _undo.Push(Map.ToJson());
        Map = MapFile.Read(_redo.Pop());
        Dirty = true;
        Notify();
        Say("Redone.");
        return true;
    }

    /// <summary>The table map for a new table, made when it's first given a source.</summary>
    private TableMap Ensure(string target)
    {
        if (Map.Table(target) is { } tm) return tm;
        var table = New?.Table(target);
        tm = new TableMap { Target = table?.Name ?? target };
        Map.Tables.Add(tm);
        return tm;
    }

    /// <summary>Says which old table a new one's rows come from (null: rows given, for a code table), and its filter.</summary>
    public void SetSource(string target, string? from, string? where = null, bool setWhere = false, Certainty certainty = Certainty.Confirmed, string? reason = null) =>
        Change($"set {target}'s source to {from ?? "rows given"}", () =>
        {
            var tm = Ensure(target);
            tm.From = from == null ? null : Old?.Table(from)?.Name ?? from;
            if (setWhere) tm.Where = string.IsNullOrWhiteSpace(where) ? null : where.Trim();
            tm.Certainty = certainty;
            if (reason != null) tm.Reason = reason;
            if (New?.Table(target) is { } t)
                foreach (var c in t.Columns.Where(c => c.Identity && tm.Column(c.Name) == null))
                    tm.Columns.Add(new ColumnMap { Target = c.Name, Kind = ColumnKind.Generated });
            Order(tm);
        });

    public void SetWhere(string target, string? where)
    {
        if (Map.Table(target) is not { } tm) return;
        var w = string.IsNullOrWhiteSpace(where) ? null : where.Trim();
        if (w != tm.Where) Change($"filtered {target}", () => tm.Where = w);
    }

    /// <summary>Says how a new column is filled: its kind and the old column it reads (a key or lookup's table from the new foreign key).</summary>
    public void SetColumn(string target, string column, ColumnKind kind, string? from = null) =>
        Change($"mapped {target}.{column}", () =>
        {
            var tm = Ensure(target);
            var cm = tm.Column(column);
            if (cm == null)
            {
                cm = new ColumnMap { Target = New?.Table(target)?.Column(column)?.Name ?? column };
                tm.Columns.Add(cm);
                Order(tm);
            }
            cm.Kind = kind;
            cm.From = from ?? (kind is ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated ? null : cm.From);
            cm.Certainty = Certainty.Confirmed;
            if (kind is ColumnKind.Key or ColumnKind.Lookup && cm.Table == null && New?.Table(target) is { } t
                && t.ForeignKeys.FirstOrDefault(f => f.Columns.Count == 1 && f.Columns[0].Equals(column, StringComparison.OrdinalIgnoreCase)) is { } fk)
                cm.Table = New.Table(fk.RefTable)?.Name ?? fk.RefTable;
        });

    /// <summary>Replaces a column's mapping with this one (from JSON, the editor or an agent).</summary>
    public void PutColumn(string target, ColumnMap cm, string? what = null) =>
        Change(what ?? $"edited {target}.{cm.Target}", () =>
        {
            var tm = Ensure(target);
            tm.Columns.RemoveAll(c => c.Target.Equals(cm.Target, StringComparison.OrdinalIgnoreCase));
            tm.Columns.Add(cm);
            Order(tm);
        });

    /// <summary>Takes a new column out of the mapping (it's filled by nothing).</summary>
    public void Unmap(string target, string column) =>
        Change($"unmapped {target}.{column}", () => Map.Table(target)?.Columns.RemoveAll(c => c.Target.Equals(column, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Sets a code table's given rows.</summary>
    public void SetRows(string target, List<Dictionary<string, JsonNode?>> rows) =>
        Change($"gave {target}'s rows", () =>
        {
            var tm = Ensure(target);
            tm.From = null;
            tm.Rows = rows;
            if (New?.Table(target) is { } t)
                foreach (var c in t.Columns.Where(c => !c.Identity && tm.Column(c.Name) == null))
                    tm.Columns.Add(new ColumnMap { Target = c.Name, Kind = ColumnKind.Direct, From = c.Name, Reason = "From the rows given." });
            Order(tm);
        });

    private void Order(TableMap tm)
    {
        if (New?.Table(tm.Target) is not { } t) return;
        tm.Columns = [.. tm.Columns.OrderBy(c => t.Column(c.Target)?.Ordinal ?? int.MaxValue)];
    }

    /// <summary>Suggests mappings for everything not yet mapped (one undo step); nothing already mapped changes.</summary>
    public Suggester.Result? Suggest()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        Suggester.Result? r = null;
        Change("suggested mappings", () => r = Suggester.Suggest(Map, Old, New, Evidence));
        Say(r!.Tables + r.Columns == 0 ? "Nothing to suggest: everything is mapped." : $"Suggested {r.Tables} table{(r.Tables == 1 ? "" : "s")} and {r.Columns} column{(r.Columns == 1 ? "" : "s")}: check each (amber) and confirm it. See Coverage for what's left." + (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : ""));
        return r;
    }

    /// <summary>Confirms suggestions: one column, a whole table, or every suggestion marked sure.</summary>
    public int Confirm(string? table = null, string? column = null, bool sureOnly = false)
    {
        var n = 0;
        Change(column != null ? $"confirmed {table}.{column}" : table != null ? $"confirmed {table}" : "confirmed the sure suggestions", () =>
        {
            foreach (var tm in Map.Tables.Where(t => table == null || t.Target.Equals(table, StringComparison.OrdinalIgnoreCase)))
            {
                if (column == null && tm.Certainty != Certainty.Confirmed && (!sureOnly || tm.Certainty == Certainty.Sure)) { tm.Certainty = Certainty.Confirmed; n++; }
                foreach (var c in tm.Columns.Where(c => column == null || c.Target.Equals(column, StringComparison.OrdinalIgnoreCase)))
                    if (c.Certainty != Certainty.Confirmed && (!sureOnly || c.Certainty == Certainty.Sure)) { c.Certainty = Certainty.Confirmed; n++; }
            }
        });
        Say($"Confirmed {n}.");
        return n;
    }

    /// <summary>Records that an old table or column isn't migrated, and why (by whom: the person, or an agent's proposal).</summary>
    public void Drop(string about, string why, string by = "person") =>
        Change($"dropped {about}", () =>
        {
            Map.Decisions.RemoveAll(d => d.About.Equals(about, StringComparison.OrdinalIgnoreCase));
            Map.Decisions.Add(new Decision { About = about, Said = why, By = by, At = DateTime.Now });
        });

    // ───────────────────────────── profiles and runs ─────────────────────────────

    /// <summary>Profiles an old table's rows.</summary>
    public TableProfile? ProfileOld(string table)
    {
        if (Old?.Table(table) is not { } t) { Say($"The old database has no table {table}."); return null; }
        try
        {
            var (c, d) = Rows.OpenSide(Map.Source, MapPath, Connections);
            using (c) Profile = Profiler.Profile(c, d, t);
            Notify();
            Say($"Profiled {t.Name}: {Profile.Rows:N0} rows.");
            return Profile;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"Couldn't profile {table}: {e.Message}");
            return null;
        }
    }

    /// <summary>Sets a date conversion's formats to the ones the profile found (one undo step).</summary>
    public bool UseFormats(string table, string column)
    {
        if (Map.Table(table)?.Column(column) is not { From: { } from } cm) { Say($"{table}.{column} isn't mapped from an old column."); return false; }
        if (Profile == null || Old?.Table(Map.Table(table)!.From ?? "")?.Name != Profile.Table) ProfileOld(Map.Table(table)!.From ?? "");
        if (Profile?.Column(from) is not { } cp || cp.DateFormats.Count == 0) { Say($"The profile of {from} found no date formats."); return false; }
        Change($"used the profile's date formats for {table}.{column}", () =>
        {
            cm.Formats = [.. (cm.Formats ?? []).Concat(cp.DateFormats).Distinct(StringComparer.Ordinal)];
            cm.BlankIsNull ??= cp.Blanks > 0 ? true : null;
        });
        Say($"{table}.{column} reads {string.Join(", ", cm.Formats!)}." + (cp.NotDates.Count > 0 ? $" Still not dates: {cp.NotDates.Count} value{(cp.NotDates.Count == 1 ? "" : "s")}." : ""));
        return true;
    }

    /// <summary>
    /// Fills a lookup's code table from the codes the profile found (meanings start as the codes: change them), and maps
    /// each code to itself (trimmed, in capitals) where the lookup doesn't say yet. One undo step.
    /// </summary>
    public bool UseCodes(string table, string column)
    {
        if (Map.Table(table)?.Column(column) is not { From: { } from, Table: { } codeTable } cm || Map.Table(codeTable) is not { From: null } code) { Say($"{table}.{column} isn't a lookup into a code table with given rows."); return false; }
        if (Profile == null || Old?.Table(Map.Table(table)!.From ?? "")?.Name != Profile.Table) ProfileOld(Map.Table(table)!.From ?? "");
        if (Profile?.Column(from) is not { } cp) return false;
        var target = New?.Table(codeTable);
        var key = target?.PrimaryKey.FirstOrDefault() ?? "code";
        var other = target?.Columns.FirstOrDefault(c => !c.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && !c.Identity && !c.Nullable)?.Name;
        Change($"made {codeTable}'s rows from {from}'s codes", () =>
        {
            code.Rows ??= [];
            cm.Map ??= [];
            foreach (var v in cp.Top.Select(t => t.Value.Trim().ToUpperInvariant()).Distinct())
            {
                if (!code.Rows.Any(r => string.Equals(Expression.Str(Transforms.Clr(r.GetValueOrDefault(key))), v, StringComparison.OrdinalIgnoreCase)))
                {
                    var r = new Dictionary<string, JsonNode?> { [key] = v };
                    if (other != null) r[other] = v;
                    code.Rows.Add(r);
                }
                if (!cm.Map.Keys.Contains(v, StringComparer.OrdinalIgnoreCase)) cm.Map[v] = v;
            }
            cm.Trim ??= true;
        });
        Say($"{codeTable} has {code.Rows!.Count} rows now. Their meanings are the codes for now: say what each means.");
        return true;
    }

    /// <summary>Runs the whole mapping on the old rows in memory. Writes nothing.</summary>
    public List<TableRun>? DryRun()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        var errors = Findings.Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count > 0) { Say($"The mapping has {errors.Count} errors (the Checks tab): fix them first."); return null; }
        try
        {
            var (c, d) = Rows.OpenSide(Map.Source, MapPath, Connections);
            using (c) LastRun = Data.DryRun.Run(Map, Old, New, c, d);
            LastRunKind = "dry run";
            Notify();
            var inserted = LastRun.Sum(r => r.Inserted);
            var rejected = LastRun.Sum(r => r.Rejected);
            Say($"Dry run: {inserted:N0} rows would go in, {rejected:N0} would be rejected ({rejected - LastRun.Sum(r => r.Followed):N0} for their own values). Nothing was written.");
            return LastRun;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or FormatException)
        {
            Say($"The dry run couldn't run: {e.Message}");
            return null;
        }
    }

    /// <summary>Migrates into the new database (after the checks), keeping a journal; a rerun picks up where the last stopped.</summary>
    public MigrationResult? Migrate()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        try
        {
            var (oc, od) = Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc)
            {
                var (nc, nd) = Rows.OpenSide(Map.Target, MapPath, Connections);
                using (nc)
                {
                    LastMigration = Migrator.Run(Map, Old, New, oc, od, nc, nd);
                    Runs = Journal.List(nc, nd, Map.Id);
                }
            }
            LastRun = LastMigration.Tables;
            LastRunKind = "migration";
            Notify();
            Say($"Migrated: {LastMigration.Inserted:N0} rows in, {LastMigration.Rejected:N0} rejected" + (LastMigration.Skipped > 0 ? $", {LastMigration.Skipped:N0} already in from an earlier run" : "") + ". Write the parity suite to prove it.");
            return LastMigration;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"The migration didn't run: {e.Message}");
            return null;
        }
    }

    /// <summary>Takes out exactly the rows one run put in; nothing else is touched.</summary>
    public long RollBack(string runId)
    {
        if (New == null) return 0;
        try
        {
            var (nc, nd) = Rows.OpenSide(Map.Target, MapPath, Connections);
            using (nc)
            {
                var n = Migrator.Rollback(New, nc, nd, runId);
                Runs = Journal.List(nc, nd, Map.Id);
                Notify();
                Say($"Rolled back: {n:N0} rows the run put in are out again. Nothing else was touched.");
                return n;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"Couldn't roll back: {e.Message}");
            return 0;
        }
    }

    public void LoadRuns()
    {
        Runs = [];
        if (New == null) return;
        try
        {
            var (nc, nd) = Rows.OpenSide(Map.Target, MapPath, Connections);
            using (nc) Runs = Journal.List(nc, nd, Map.Id);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException) { }
    }

    // ───────────────────────────── hand-offs ─────────────────────────────

    /// <summary>Writes the migration as a SQL script for an empty new database (in the new database's dialect, unless given).</summary>
    public List<TableRun>? WriteScript(string path, Dialect? dialect = null)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        try
        {
            var (oc, od) = Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc)
            {
                using var w = new StreamWriter(path);
                var runs = ScriptWriter.Write(Map, Old, New, oc, od, dialect ?? Map.Target.Dialect, w);
                Say($"Wrote {Path.GetFileName(path)}: {runs.Sum(r => r.Inserted):N0} rows as INSERTs for an empty {dialect ?? Map.Target.Dialect} database, the {runs.Sum(r => r.Rejected):N0} rejected listed at its end.");
                return runs;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the script: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes the AVAParityCheck suite that proves the migration: expected rows (CSV) against the new tables, by old key.</summary>
    public bool WriteParitySuite(string path)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return false; }
        try
        {
            var (oc, od) = Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc) ParityPlan.Write(Map, MapPath, Old, New, oc, od, Map.Target.Dialect, path);
            Say($"Wrote {Path.GetFileName(path)}: open it in AVAParityCheck (or AVAParityCheck --run) after the migration. Every new table's rows are checked against what the mapping makes, by old key." +
                (Map.Target.File == null ? " Name the new database \"new\" in AVAParityCheck's connections." : ""));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the parity suite: {e.Message}");
            return false;
        }
    }

    /// <summary>Writes the sign-off report (.html, or .xlsx for a workbook) with the last dry run or migration; values in reasons masked when given a mask.</summary>
    public bool ExportReport(string path, Func<string, string>? mask = null, Func<string, string>? maskKey = null)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return false; }
        try
        {
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) MigrationReport.Excel(path, Map, Old, New, LastRun, Evidence, mask, maskKey);
            else File.WriteAllText(path, MigrationReport.Html(Map, Old, New, LastRun, LastRunKind, Evidence, DateTime.Now, mask, maskKey));
            Say($"Wrote {Path.GetFileName(path)}." + (LastRun == null ? " Run the dry run first to include what would go in and what wouldn't." : ""));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the report: {e.Message}");
            return false;
        }
    }
}
