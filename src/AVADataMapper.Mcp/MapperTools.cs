using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;
using AVADataMapper.Core.Session;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVADataMapper.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with a mapping, through the same <see cref="MapSession"/> the window uses (the
/// window's own, when attached), so every change is one undo step the person can take back. Agents see schemas, the
/// mapping, checks, coverage and the old code's evidence in full; the old database's values (in profiles and rejects)
/// are masked to their shape unless the person allows values. What an agent maps is marked "likely", a proposal, until
/// a person confirms it. Writing files that hold the data (the script, the parity suite) needs values allowed; a
/// migration or a rollback needs the open window and the person's yes. Failures come back as MCP tool errors.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed partial class MapperTools(IExplorerHost host)
{
    private readonly Workspace _files = new(() => host.Root);

    public const int MaxResults = 300;
    private const string TableHelp = "A new table (the new database's), as list_tables new names it: patient.";
    private const string ColumnHelp = "A column of that new table: birth_date.";

    // ───────────────────────────── files and the mapping ─────────────────────────────

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the files in the working folder (and its subfolders) a mapper works with: mappings (.dmap), SQLite databases, DDL scripts (.sql), AVALegacyLens scans (.lens), parity suites, reports.")]
    public Task<string> ListFiles(
        [Description("A folder inside the working folder to list instead. Optional.")] string? folder = null,
        CancellationToken cancel = default) =>
        Session("listed the files", s =>
        {
            var (list, more) = _files.List(folder);
            var o = new JsonObject
            {
                ["folder"] = _files.Root,
                ["files"] = new JsonArray([.. list.Select(f => (JsonNode)new JsonObject { ["file"] = f, ["kind"] = Kind(_files.Resolve(f)) })]),
            };
            if (more) o["note"] = $"Only the first {Workspace.MaxListed} are listed. Name a folder to see the rest.";
            return o;
        }, cancel);

    private static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dmap" => "mapping",
        ".db" or ".sqlite" or ".sqlite3" => SchemaFiles.IsSqlite(path) ? "SQLite database" : "file",
        ".sql" => "DDL script",
        ".lens" => "AVALegacyLens scan",
        ".parity" => "AVAParityCheck suite",
        ".csv" => "CSV",
        ".html" or ".xlsx" => "report",
        _ => "steps",
    };

    [McpServerTool(Name = "open_map", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Opens a mapping (.dmap) from the working folder, with the schemas it names (files beside it, or the person's saved connections), and the scan it uses as evidence. Refused when the mapping open now has unsaved changes.")]
    public Task<string> OpenMap(
        [Description("The .dmap file, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("opened " + file, s =>
        {
            NotDirty(s);
            if (!s.Work.OpenMap(_files.Existing(file))) throw new InvalidOperationException(s.Work.LastSaid);
            return Overview(s);
        }, cancel);

    [McpServerTool(Name = "new_map", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Starts a new, empty mapping (open_schema then gives it its old and new schemas). Refused when the mapping open now has unsaved changes.")]
    public Task<string> NewMap(CancellationToken cancel = default) =>
        Session("started a new mapping", s =>
        {
            NotDirty(s);
            s.Work.NewMap();
            return Overview(s);
        }, cancel);

    private static void NotDirty(IExplorerSession s)
    {
        if (s.Work.Dirty) throw new InvalidOperationException("The mapping open now has unsaved changes: ask the person to save it (or save_map_as a copy) first.");
    }

    [McpServerTool(Name = "open_schema", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Reads the old or the new database's schema from a file in the working folder: a SQLite database, or a DDL script (T-SQL, PostgreSQL or SQLite; the dialect is worked out from its words). Live databases are connected by the person (File menu): agents never see a connection string.")]
    public Task<string> OpenSchema(
        [Description("old or new.")] string side,
        [Description("The SQLite database or DDL script, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("read a schema", s =>
        {
            var old = Side(side);
            if (!s.Work.OpenSchema(old, _files.Existing(file))) throw new InvalidOperationException(s.Work.LastSaid);
            var schema = old ? s.Work.Old! : s.Work.New!;
            return new JsonObject { ["side"] = old ? "old" : "new", ["dialect"] = schema.Dialect.ToString(), ["tables"] = new JsonArray([.. schema.Tables.Select(t => (JsonNode)t.FullName)]) };
        }, cancel);

    [McpServerTool(Name = "open_evidence", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Uses an AVALegacyLens scan (.lens) of the old system as evidence: which code writes and reads each old column, the business rules in the original's own words, and what nothing uses. Suggestions and coverage use it.")]
    public Task<string> OpenEvidence(
        [Description("The .lens file, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("opened a scan of the old system", s =>
        {
            if (!s.Work.OpenEvidence(_files.Existing(file))) throw new InvalidOperationException(s.Work.LastSaid);
            return new JsonObject { ["evidence"] = s.Work.Evidence!.Source, ["dead_tables"] = new JsonArray([.. s.Work.Evidence.DeadTables.Keys.Select(k => (JsonNode)k)]) };
        }, cancel);

    [McpServerTool(Name = "save_map_as", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Saves the mapping as a new .dmap file in the working folder (never over one that's there). The person saves their own mapping file.")]
    public Task<string> SaveMapAs(
        [Description("The new .dmap file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("saved the mapping as a new file", output, "A mapping", [MapFile.Extension], (s, full) =>
        {
            var copy = MapFile.Read(s.Work.Map.ToJson());
            foreach (var side in new[] { copy.Source, copy.Target })
                if (MapFile.FullPath(side, s.Work.MapPath) is { } f) side.File = Path.GetRelativePath(Path.GetDirectoryName(full)!, f).Replace('\\', '/');
            if (copy.Evidence != null && MapFile.FullPath(new Side { File = copy.Evidence }, s.Work.MapPath) is { } ev) copy.Evidence = Path.GetRelativePath(Path.GetDirectoryName(full)!, ev).Replace('\\', '/');
            copy.Save(full);
            return new JsonObject { ["tables"] = copy.Tables.Count };
        }, cancel);

    [McpServerTool(Name = "get_overview", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The mapping at a glance: the old and new databases, each new table's source and how many of its columns are filled, the coverage and checks in counts, the last dry run or migration, and whether there are unsaved changes.")]
    public Task<string> GetOverview(CancellationToken cancel = default) => Session("looked at the mapping", Overview, cancel);

    private static JsonObject Overview(IExplorerSession s)
    {
        var w = s.Work;
        var findings = w.Findings;
        var cov = w.Coverage;
        return new JsonObject
        {
            ["name"] = w.Map.Name,
            ["file"] = w.MapPath == null ? null : Path.GetFileName(w.MapPath),
            ["old"] = w.Old == null ? "not open" : $"{w.Old.Source} ({w.Old.Dialect}, {w.Old.Tables.Count} tables)",
            ["new"] = w.New == null ? "not open" : $"{w.New.Source} ({w.New.Dialect}, {w.New.Tables.Count} tables)",
            ["evidence"] = w.Evidence?.Source,
            ["tables"] = new JsonArray([.. (w.New?.LoadOrder() ?? []).Select(t =>
            {
                var tm = w.Map.Table(t.Name);
                var done = t.Columns.Count(c => c.Identity || tm?.Column(c.Name) != null);
                return (JsonNode)new JsonObject { ["table"] = t.Name, ["from"] = tm == null ? null : tm.From ?? "rows given", ["filled"] = $"{done}/{t.Columns.Count}", ["certainty"] = tm?.Certainty.ToString().ToLowerInvariant() };
            })]),
            ["coverage"] = new JsonObject(cov.GroupBy(i => i.Section).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["checks"] = new JsonObject { ["errors"] = findings.Count(f => f.Severity == Severity.Error), ["warnings"] = findings.Count(f => f.Severity == Severity.Warning) },
            ["last_run"] = w.LastRun == null ? null : $"{w.LastRunKind}: {w.LastRun.Sum(r => r.Inserted):N0} in, {w.LastRun.Sum(r => r.Rejected):N0} rejected",
            ["unsaved_changes"] = w.Dirty,
            ["undo_steps"] = w.UndoSteps,
        };
    }

    // ───────────────────────────── schemas ─────────────────────────────

    [McpServerTool(Name = "list_tables", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The old or new database's tables: each with its columns' count, primary key and the tables its foreign keys point at; the new side in the order a migration loads them.")]
    public Task<string> ListTables(
        [Description("old or new.")] string side,
        CancellationToken cancel = default) =>
        Session("listed tables", s =>
        {
            var schema = Schema(s, side);
            var tables = Side(side) ? schema.Tables : schema.LoadOrder();
            return new JsonObject
            {
                ["side"] = Side(side) ? "old" : "new",
                ["tables"] = new JsonArray([.. tables.Select(t => (JsonNode)new JsonObject
                {
                    ["table"] = t.FullName, ["columns"] = t.Columns.Count, ["key"] = string.Join(", ", t.PrimaryKey),
                    ["refers_to"] = t.ForeignKeys.Count == 0 ? null : new JsonArray([.. t.ForeignKeys.Select(f => (JsonNode)f.RefTable).Distinct()]),
                })]),
            };
        }, cancel);

    [McpServerTool(Name = "describe_table", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One table in full: its columns (type, required or not, default, identity), keys, foreign keys and checks; for an old table, what the old system's code does with each column (who writes and reads it, its rules), when there's a scan.")]
    public Task<string> DescribeTable(
        [Description("old or new.")] string side,
        [Description("The table's name.")] string table,
        CancellationToken cancel = default) =>
        Session("read a table", s =>
        {
            var old = Side(side);
            var t = Schema(s, side).Table(table) ?? throw new ArgumentException($"The {(old ? "old" : "new")} database has no table {table}.");
            var ev = old ? s.Work.Evidence : null;
            return new JsonObject
            {
                ["table"] = t.FullName,
                ["columns"] = new JsonArray([.. t.Columns.Select(c =>
                {
                    var o = new JsonObject { ["column"] = c.Name, ["type"] = c.Type.Declared, ["required"] = !c.Nullable, ["default"] = c.Default, ["identity"] = c.Identity ? true : null };
                    if (ev?.Column(t.Name, c.Name) is { } e)
                    {
                        o["written_by"] = new JsonArray([.. e.Writers.Select(x => (JsonNode)x)]);
                        o["read_by"] = new JsonArray([.. e.Readers.Select(x => (JsonNode)x)]);
                        if (e.Rules.Count > 0) o["rules"] = new JsonArray([.. e.Rules.Select(x => (JsonNode)x)]);
                    }
                    return (JsonNode)o;
                })]),
                ["primary_key"] = new JsonArray([.. t.PrimaryKey.Select(k => (JsonNode)k)]),
                ["foreign_keys"] = new JsonArray([.. t.ForeignKeys.Select(f => (JsonNode)$"{string.Join(", ", f.Columns)} → {f.RefTable}({string.Join(", ", f.RefColumns)})")]),
                ["unique"] = new JsonArray([.. t.Uniques.Select(u => (JsonNode)string.Join(", ", u))]),
                ["checks"] = new JsonArray([.. t.Checks.Select(c => (JsonNode)c)]),
                ["old_code"] = ev?.DeadTables.GetValueOrDefault(t.Name),
            };
        }, cancel);

    // ───────────────────────────── the mapping ─────────────────────────────

    [McpServerTool(Name = "get_table_map", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("How one new table is filled: its old table (or its given rows, for a code table), its filter, and every new column with how it's filled (in words and as JSON), how sure the mapping is (confirmed, or suggested sure, likely, guessed), why, and what the checks say about it.")]
    public Task<string> GetTableMap(
        [Description(TableHelp)] string table,
        CancellationToken cancel = default) =>
        Session("read a table's mapping", s =>
        {
            var t = NewTable(s, table);
            var tm = s.Work.Map.Table(t.Name);
            var findings = s.Work.Findings;
            return new JsonObject
            {
                ["table"] = t.Name, ["from"] = tm == null ? null : tm.From ?? "rows given", ["where"] = tm?.Where, ["certainty"] = tm?.Certainty.ToString().ToLowerInvariant(), ["reason"] = tm?.Reason,
                ["rows_given"] = tm?.Rows == null ? null : new JsonArray([.. tm.Rows.Select(r => (JsonNode)new JsonObject(r.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))))]),
                ["columns"] = new JsonArray([.. t.Columns.Select(c =>
                {
                    var cm = tm?.Column(c.Name);
                    var o = new JsonObject
                    {
                        ["column"] = c.Name, ["type"] = c.Type.Declared, ["required"] = !c.Nullable && c.Default == null && !c.Identity,
                        ["filled_by"] = cm == null ? (c.Identity ? "numbered by the new database" : "nothing") : MapText.Describe(cm),
                        ["mapping"] = cm == null ? null : JsonSerializer.SerializeToNode(cm, MapFile.Json),
                    };
                    var said = findings.Where(f => f.About.Equals($"{t.Name}.{c.Name}", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (said.Count > 0) o["checks"] = new JsonArray([.. said.Select(f => (JsonNode)$"{f.Rule.Id} {f.Severity}: {f.Message}")]);
                    return (JsonNode)o;
                })]),
            };
        }, cancel);

    [McpServerTool(Name = "set_table_source", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Says which old table a new table's rows come from, and which of them (a filter in the mapping's expression language: Deleted = 0). Without from, the table's rows are given (a code table: set_code_rows). Marked a proposal (likely) for the person to confirm. One undo step.")]
    public Task<string> SetTableSource(
        [Description(TableHelp)] string table,
        [Description("The old table its rows come from. Optional: leave it out for a code table whose rows are given.")] string? from = null,
        [Description("Which old rows: a filter such as Deleted = 0. Optional; an empty text clears it.")] string? where = null,
        [Description("Why, in a sentence: kept with the mapping.")] string? reason = null,
        CancellationToken cancel = default) =>
        Session("proposed a table's source", s =>
        {
            var t = NewTable(s, table);
            if (from != null && s.Work.Old?.Table(from) is null) throw new ArgumentException($"The old database has no table {from}.");
            if (where is { Length: > 0 } && from != null) CheckExpression(where, s.Work.Old!.Table(from)!);
            s.Work.SetSource(t.Name, from, where, where != null, Certainty.Likely, "Proposed by an AI agent" + (reason != null ? ": " + reason : "."));
            return TableAfter(s, t.Name);
        }, cancel);

    [McpServerTool(Name = "set_column", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Says how a new column is filled, as a column mapping (JSON, as get_table_map shows them): kind (direct, convert, lookup, key, split, merge, constant, default, expression, generated) and its options: from, as, formats, map, table, unknown (reject, null or default), separator, part, trim, case, value, expression, blankIsNull, ifNull. It's checked against both schemas; marked a proposal (likely) for the person to confirm. One undo step.")]
    public Task<string> SetColumn(
        [Description(TableHelp)] string table,
        [Description(ColumnHelp)] string column,
        [Description("The column mapping, a JSON object: {\"kind\": \"convert\", \"from\": \"DOB\", \"as\": \"date\", \"formats\": [\"MM/dd/yyyy\"], \"unknown\": \"reject\"}.")] JsonElement mapping,
        [Description("Why, in a sentence: kept with the mapping.")] string? reason = null,
        CancellationToken cancel = default) =>
        Session("proposed a column's mapping", s =>
        {
            var t = NewTable(s, table);
            var c = t.Column(column) ?? throw new ArgumentException($"{t.Name} has no column {column}.");
            ColumnMap cm;
            try
            {
                cm = mapping.Deserialize<ColumnMap>(MapFile.Json) ?? throw new FormatException("The mapping is empty.");
            }
            catch (JsonException e)
            {
                throw new FormatException($"That isn't a column mapping: {e.Message}");
            }
            cm.Target = c.Name;
            cm.Certainty = Certainty.Likely;
            cm.Reason = "Proposed by an AI agent" + (reason != null ? ": " + reason : cm.Reason != null ? ": " + cm.Reason : ".");
            if (cm.Kind == ColumnKind.Expression) CheckExpression(cm.Expression ?? "", OldTable(s, t.Name));
            s.Work.PutColumn(t.Name, cm, $"proposed {t.Name}.{c.Name}");
            var o = TableAfter(s, t.Name);
            o["column"] = MapText.Describe(cm);
            return o;
        }, cancel);

    [McpServerTool(Name = "unmap_column", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Takes a new column out of the mapping (nothing fills it). One undo step.")]
    public Task<string> UnmapColumn(
        [Description(TableHelp)] string table,
        [Description(ColumnHelp)] string column,
        CancellationToken cancel = default) =>
        Session("unmapped a column", s =>
        {
            var t = NewTable(s, table);
            s.Work.Unmap(t.Name, column);
            return TableAfter(s, t.Name);
        }, cancel);

    [McpServerTool(Name = "set_code_rows", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Gives a code table its rows (its rows come from no old table): each an object of column → value, such as {\"code\": \"U\", \"meaning\": \"Unknown\"}. Replaces the rows it had. One undo step.")]
    public Task<string> SetCodeRows(
        [Description(TableHelp)] string table,
        [Description("The rows: a list of objects, column → value.")] JsonElement rows,
        CancellationToken cancel = default) =>
        Session("gave a code table its rows", s =>
        {
            var t = NewTable(s, table);
            if (rows.ValueKind != JsonValueKind.Array) throw new ArgumentException("rows is a list of objects.");
            var list = rows.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Object
                ? r.EnumerateObject().ToDictionary(p => t.Column(p.Name)?.Name ?? throw new ArgumentException($"{t.Name} has no column {p.Name}."), p => JsonNode.Parse(p.Value.GetRawText()))
                : throw new ArgumentException("Each row is an object of column → value.")).ToList();
            s.Work.SetRows(t.Name, list);
            return TableAfter(s, t.Name);
        }, cancel);

    [McpServerTool(Name = "suggest", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Suggests mappings for everything not mapped yet (nothing mapped changes): each new table's old table, and each column by name, type, foreign keys and the scan's evidence, marked sure, likely or guessed with its reason. One undo step.")]
    public Task<string> Suggest(CancellationToken cancel = default) =>
        Session("suggested mappings", s =>
        {
            var r = s.Work.Suggest() ?? throw new InvalidOperationException(s.Work.LastSaid);
            var o = Overview(s);
            o["suggested"] = $"{r.Tables} tables and {r.Columns} columns";
            if (r.Notes.Count > 0) o["notes"] = new JsonArray([.. r.Notes.Select(n => (JsonNode)n)]);
            return o;
        }, cancel);

    [McpServerTool(Name = "confirm", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Confirms suggestions and proposals: one column, one table, or (sure_only) every suggestion marked sure. In the open window the person is asked first; confirming is their call. One undo step.")]
    public Task<string> Confirm(
        [Description(TableHelp + " Optional: all tables.")] string? table = null,
        [Description(ColumnHelp + " Optional: the whole table.")] string? column = null,
        [Description("Only the suggestions marked sure. Default false.")] bool sure_only = false,
        CancellationToken cancel = default) =>
        AskFirst("confirmed mappings", s => $"An AI agent asks to confirm {(column != null ? $"{table}.{column}" : table != null ? $"the mapping of {table}" : sure_only ? "every suggestion marked sure" : "every suggestion")}. Confirm?",
            s => new JsonObject { ["confirmed"] = s.Work.Confirm(table == null ? null : NewTable(s, table).Name, column, sure_only) }, requireWindow: false, cancel);

    [McpServerTool(Name = "drop", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Records that an old table or column isn't migrated, and why (the coverage report then shows it dropped). Recorded as the agent's: the person sees who decided. One undo step.")]
    public Task<string> Drop(
        [Description("The old table (FaxQueue) or column (tblPatient.Fax).")] string about,
        [Description("Why, in a sentence, with the evidence: \"No code reads or writes it.\"")] string why,
        CancellationToken cancel = default) =>
        Session("proposed dropping something", s =>
        {
            var parts = about.Split('.');
            var t = s.Work.Old?.Table(parts[0]) ?? throw new ArgumentException($"The old database has no table {parts[0]}.");
            if (parts.Length > 1 && t.Column(parts[^1]) is null) throw new ArgumentException($"{t.Name} has no column {parts[^1]}.");
            var name = parts.Length > 1 ? $"{t.Name}.{t.Column(parts[^1])!.Name}" : t.Name;
            s.Work.Drop(name, "Dropped: " + why.TrimStart().TrimStart("Dropped:".ToCharArray()).Trim(), "AI agent");
            return new JsonObject { ["dropped"] = name };
        }, cancel);

    [McpServerTool(Name = "undo", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Takes back the last change to the mapping (the person's or an agent's: one history).")]
    public Task<string> Undo(CancellationToken cancel = default) =>
        Session("undid a change", s => new JsonObject { ["undone"] = s.Work.Undo(), ["undo_steps"] = s.Work.UndoSteps }, cancel);

    [McpServerTool(Name = "redo", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Makes again the last change undone.")]
    public Task<string> Redo(CancellationToken cancel = default) =>
        Session("redid a change", s => new JsonObject { ["redone"] = s.Work.Redo(), ["undo_steps"] = s.Work.UndoSteps }, cancel);

    [McpServerTool(Name = "check_expression", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Checks an expression (a filter or a computed column) in the mapping's own expression language against an old table's columns: whether it parses, and which columns it reads. Operators = <> < > <= >= + - * / || and or not, is [not] null, in (…), like; functions upper lower trim len left right substr replace coalesce nullif iif concat round abs date year month day.")]
    public Task<string> CheckExpressionTool(
        [Description("The expression: Deleted = 0, or upper(trim(Code)).")] string expression,
        [Description("The old table whose columns it reads.")] string old_table,
        CancellationToken cancel = default) =>
        Session("checked an expression", s =>
        {
            var t = s.Work.Old?.Table(old_table) ?? throw new ArgumentException($"The old database has no table {old_table}.");
            var e = Expression.Parse(expression);
            return new JsonObject { ["ok"] = e.Check(t.Columns.Select(c => c.Name)) == null, ["problem"] = e.Check(t.Columns.Select(c => c.Name)), ["reads"] = new JsonArray([.. e.Columns.Select(c => (JsonNode)c)]) };
        }, cancel);

    [McpServerTool(Name = "try_value", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Runs a new column's mapping on a value you give for its old column, and says what the new column would get, or why the row would be rejected (the same transform the migration runs). Key columns need the dry run's key map: use dry_run.")]
    public Task<string> TryValue(
        [Description(TableHelp)] string table,
        [Description(ColumnHelp)] string column,
        [Description("A value for the old column, as text (null for null).")] string? value,
        CancellationToken cancel = default) =>
        Session("tried a value", s =>
        {
            var t = NewTable(s, table);
            var c = t.Column(column) ?? throw new ArgumentException($"{t.Name} has no column {column}.");
            var cm = s.Work.Map.Table(t.Name)?.Column(c.Name) ?? throw new InvalidOperationException($"{t.Name}.{c.Name} isn't mapped yet.");
            if (cm.Kind == ColumnKind.Key) return new JsonObject { ["note"] = "A key goes through the key map of its table: dry_run tries it." };
            object? v = value == null || value.Trim().Equals("null", StringComparison.OrdinalIgnoreCase) ? null : value;
            var o = Transforms.Apply(cm, _ => v, c, new TransformContext());
            return new JsonObject
            {
                ["mapping"] = MapText.Describe(cm),
                ["result"] = o.Rejected != null ? null : o.UseDefault ? $"the column's default ({c.Default ?? "none"})" : Shown(o.Value),
                ["rejected"] = o.Rejected,
            };
        }, cancel);

    // ───────────────────────────── checks, coverage, evidence ─────────────────────────────

    [McpServerTool(Name = "get_checks", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The mapping checked against both schemas, before any row is read: errors (the migration can't run as mapped: an old column that isn't there, a type that doesn't fit, a key into a table nothing fills) and warnings (rows may be rejected: text cut short, precision lost, nulls into required columns). Each with its rule (M1 to M15), where, and the fix.")]
    public Task<string> GetChecks(
        [Description("Only this severity and worse: info, warning or error. Optional.")] string? severity = null,
        CancellationToken cancel = default) =>
        Session("read the checks", s =>
        {
            var min = severity is { Length: > 0 } sv ? Enum.TryParse<Severity>(sv, true, out var m) ? m : throw new ArgumentException("severity is info, warning or error.") : Severity.Info;
            var f = s.Work.Findings.Where(x => x.Severity >= min).ToList();
            return new JsonObject
            {
                ["errors"] = f.Count(x => x.Severity == Severity.Error), ["warnings"] = f.Count(x => x.Severity == Severity.Warning),
                ["findings"] = new JsonArray([.. f.Select(x => (JsonNode)new JsonObject { ["rule"] = x.Rule.Id, ["severity"] = x.Severity.ToString().ToLowerInvariant(), ["about"] = x.About, ["message"] = x.Message, ["fix"] = x.Fix, ["why"] = x.Rule.Why })]),
            };
        }, cancel);

    [McpServerTool(Name = "get_coverage", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What's filled and what isn't: new required columns nothing fills, new tables not mapped, suggestions not confirmed, and every old table and column nothing reads (with what the old system's code does with it), or that was dropped and why.")]
    public Task<string> GetCoverage(
        [Description("Only one section: required, unmapped, suggested, unread or dropped. Optional.")] string? section = null,
        CancellationToken cancel = default) =>
        Session("read the coverage", s =>
        {
            var want = section?.Trim().ToLowerInvariant() switch
            {
                null or "" => null, "required" => Coverage.Required, "unmapped" => Coverage.Unmapped, "suggested" => Coverage.Suggested,
                "unread" => Coverage.Unread, "dropped" => Coverage.Dropped, _ => throw new ArgumentException("section is required, unmapped, suggested, unread or dropped."),
            };
            var items = s.Work.Coverage.Where(i => want == null || i.Section == want).ToList();
            return new JsonObject
            {
                ["count"] = items.Count,
                ["items"] = new JsonArray([.. items.Take(MaxResults).Select(i => (JsonNode)new JsonObject { ["section"] = i.Section, ["about"] = i.About, ["text"] = i.Text, ["old_code"] = i.Evidence })]),
            };
        }, cancel);

    [McpServerTool(Name = "get_evidence", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What the old system's code (an AVALegacyLens scan) does with an old table or column: the methods that write and read it, and the business rules about it in the original's own words.")]
    public Task<string> GetEvidence(
        [Description("The old table.")] string table,
        [Description("The old column. Optional: every column of the table.")] string? column = null,
        CancellationToken cancel = default) =>
        Session("read the old code's evidence", s =>
        {
            var ev = s.Work.Evidence ?? throw new InvalidOperationException("No scan of the old system is open: open_evidence opens one (.lens, from AVALegacyLens).");
            var t = s.Work.Old?.Table(table) ?? throw new ArgumentException($"The old database has no table {table}.");
            var cols = column == null ? t.Columns : [t.Column(column) ?? throw new ArgumentException($"{t.Name} has no column {column}.")];
            JsonNode Of(ColumnEvidence? e) => e == null ? "not in the scan" : new JsonObject
            {
                ["written_by"] = new JsonArray([.. e.Writers.Select(x => (JsonNode)x)]), ["read_by"] = new JsonArray([.. e.Readers.Select(x => (JsonNode)x)]),
                ["rules"] = new JsonArray([.. e.Rules.Select(x => (JsonNode)x)]), ["unused"] = e.Unused,
            };
            return new JsonObject
            {
                ["table"] = t.Name, ["evidence"] = ev.Source, ["table_code"] = Of(ev.Table(t.Name)), ["dead"] = ev.DeadTables.GetValueOrDefault(t.Name),
                ["columns"] = new JsonObject(cols.Select(c => KeyValuePair.Create(c.Name, (JsonNode?)Of(ev.Column(t.Name, c.Name))))),
            };
        }, cancel);

    // ───────────────────────────── data ─────────────────────────────

    [McpServerTool(Name = "profile", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What an old table's columns really hold, from its rows: counts, nulls, blanks, lengths, value shapes (99/99/9999), the date formats in text and how many values none of them reads, and the commonest values of code-like columns. Other values are masked to their shape unless the person allows values.")]
    public Task<string> ProfileTable(
        [Description("The old table.")] string table,
        CancellationToken cancel = default) =>
        Session("profiled an old table", s =>
        {
            var p = s.Work.ProfileOld(table) ?? throw new InvalidOperationException(s.Work.LastSaid);
            var real = s.SeeRealValues;
            return new JsonObject
            {
                ["table"] = p.Table, ["rows"] = p.Rows,
                ["columns"] = new JsonArray([.. p.Columns.Select(c =>
                {
                    var codes = Mask.IsCodes(c);
                    string V(string v) => real || codes ? v : Mask.Value(v);
                    var o = new JsonObject
                    {
                        ["column"] = c.Name, ["type"] = c.Type.Declared, ["values"] = c.Values, ["nulls"] = c.Nulls, ["blanks"] = c.Blanks, ["distinct"] = c.Distinct,
                        ["length"] = c.MaxLength == null ? null : $"{c.MinLength}–{c.MaxLength}",
                        ["shapes"] = c.Shapes.Count is > 0 and <= 20 ? new JsonArray([.. c.Shapes.Take(8).Select(x => (JsonNode)$"{x.Shape} ×{x.Count}")]) : null,
                        ["range"] = c.Min == null ? null : c.Type.IsNumber ? $"{c.Min} to {c.Max}" : c.Type.IsTemporal ? $"{Year(c.Min)} to {Year(c.Max)}" : null,
                        ["decimal_places"] = c.Scale is > 0 ? c.Scale : null,
                        ["date_formats"] = c.DateFormats.Count > 0 ? new JsonArray([.. c.DateFormats.Select(f => (JsonNode)f)]) : null,
                        ["not_dates"] = c.NotDates.Count > 0 ? new JsonArray([.. c.NotDates.Select(v => (JsonNode)V(v))]) : null,
                        ["common_values"] = codes || real ? new JsonArray([.. c.Top.Take(12).Select(t => (JsonNode)$"{V(t.Value)} ×{t.Count}")]) : null,
                        ["values_masked"] = !real && !codes ? true : null,
                    };
                    return (JsonNode)o;
                })]),
            };
        }, cancel);

    private static string Year(string? v) => v != null && v.Length >= 4 ? v[..4] : v ?? "";

    [McpServerTool(Name = "use_formats", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Sets a date conversion's formats to the ones the profile of its old column found. One undo step.")]
    public Task<string> UseFormats(
        [Description(TableHelp)] string table,
        [Description(ColumnHelp)] string column,
        CancellationToken cancel = default) =>
        Session("used the profile's date formats", s =>
        {
            var t = NewTable(s, table);
            if (!s.Work.UseFormats(t.Name, column)) throw new InvalidOperationException(s.Work.LastSaid);
            return new JsonObject { ["formats"] = new JsonArray([.. s.Work.Map.Table(t.Name)!.Column(column)!.Formats!.Select(f => (JsonNode)f)]) };
        }, cancel);

    [McpServerTool(Name = "use_codes", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Fills a lookup's code table from the codes the profile of its old column found (meanings start as the codes), and maps each code to itself. Say what each means with set_code_rows afterwards. One undo step.")]
    public Task<string> UseCodes(
        [Description(TableHelp)] string table,
        [Description(ColumnHelp)] string column,
        CancellationToken cancel = default) =>
        Session("made code rows from the profile", s =>
        {
            var t = NewTable(s, table);
            if (!s.Work.UseCodes(t.Name, column)) throw new InvalidOperationException(s.Work.LastSaid);
            var code = s.Work.Map.Table(s.Work.Map.Table(t.Name)!.Column(column)!.Table!)!;
            return new JsonObject { ["code_table"] = code.Target, ["rows"] = code.Rows!.Count };
        }, cancel);

    [McpServerTool(Name = "dry_run", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Runs the whole mapping on the old rows in memory, as the migration would, and writes nothing: each new table's rows read, filtered out, that would go in, and rejected; every row rejected for its own values (old key and why, values masked); and rows rejected only because a row they point at isn't migrated, grouped by that first cause.")]
    public Task<string> DryRun(CancellationToken cancel = default) =>
        Session("ran the dry run", s =>
        {
            var runs = s.Work.DryRun() ?? throw new InvalidOperationException(s.Work.LastSaid);
            return Runs(s, runs, 20);
        }, cancel);

    [McpServerTool(Name = "get_rejects", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The rows the last dry run (or migration) rejected from one new table: old key and every reason (values masked unless allowed).")]
    public Task<string> GetRejects(
        [Description(TableHelp)] string table,
        [Description("Only rows rejected for their own values (not because a row they point at isn't migrated). Default false.")] bool own_only = false,
        [Description("The most to return, up to 300. Default 50.")] int limit = 50,
        CancellationToken cancel = default) =>
        Session("read the rejects", s =>
        {
            var run = s.Work.LastRun?.FirstOrDefault(r => r.Target.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No dry run has covered that table: dry_run first.");
            var list = run.Rejects.Where(x => !own_only || Engine.Root(x.Reasons[0]) == null).ToList();
            return new JsonObject
            {
                ["table"] = run.Target, ["rejected"] = run.Rejected,
                ["rows"] = new JsonArray([.. list.Take(Math.Clamp(limit, 1, MaxResults)).Select(x => (JsonNode)new JsonObject { ["old_key"] = Key(s, x.OldKey), ["reasons"] = new JsonArray([.. x.Reasons.Select(r => (JsonNode)Text(s, r))]) })]),
            };
        }, cancel);

    private static JsonObject Runs(IExplorerSession s, List<TableRun> runs, int keep) => new()
    {
        ["kind"] = s.Work.LastRunKind,
        ["in"] = runs.Sum(r => r.Inserted), ["rejected"] = runs.Sum(r => r.Rejected), ["rejected_for_their_own_values"] = runs.Sum(r => r.Rejected - r.Followed),
        ["tables"] = new JsonArray([.. runs.Select(r => (JsonNode)new JsonObject
        {
            ["table"] = r.Target, ["from"] = r.From ?? "rows given", ["read"] = r.Read, ["filtered"] = r.Filtered, ["already_in"] = r.Skipped > 0 ? r.Skipped : null, ["in"] = r.Inserted, ["rejected"] = r.Rejected,
            ["own"] = new JsonArray([.. r.Rejects.Where(x => Engine.Root(x.Reasons[0]) == null).Take(keep).Select(x => (JsonNode)$"{Key(s, x.OldKey)}: {Text(s, string.Join("; ", x.Reasons))}")]),
            ["following"] = new JsonArray([.. r.Rejects.Where(x => Engine.Root(x.Reasons[0]) != null).GroupBy(x => Engine.Root(x.Reasons[0])!).Select(g => (JsonNode)$"{g.Count()} follow a row that's {Text(s, g.Key)}")]),
            ["notes"] = r.Notes.Count > 0 ? new JsonArray([.. r.Notes.Select(n => (JsonNode)n)]) : null,
        })]),
    };

    // ───────────────────────────── hand-offs and the migration ─────────────────────────────

    [McpServerTool(Name = "export_report", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the sign-off report as a new .html page or .xlsx workbook in the working folder: counts that add up, every reject by old key, every column's mapping, decisions and checks (values masked unless allowed).")]
    public Task<string> ExportReport(
        [Description("The new file, relative to the working folder: .html or .xlsx.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote the report", output, "A report", [".html", ".htm", ".xlsx"], (s, full) =>
        {
            if (!s.Work.ExportReport(full, s.SeeRealValues ? null : Mask.Text, s.SeeRealValues ? null : Mask.Key)) throw new InvalidOperationException(s.Work.LastSaid);
            return new JsonObject { ["run"] = s.Work.LastRun == null ? "none: dry_run first to include one" : s.Work.LastRunKind };
        }, cancel);

    [McpServerTool(Name = "write_script", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the migration as a SQL script (INSERTs for an empty new database, keys written out, rejects listed) as a new .sql file. It holds the data, so it needs the person to allow values.")]
    public Task<string> WriteScript(
        [Description("The new .sql file, relative to the working folder.")] string output,
        [Description("sqlserver, postgresql or sqlite. Default: the new database's.")] string? dialect = null,
        CancellationToken cancel = default) =>
        Write("wrote the migration script", output, "A script", [".sql"], (s, full) =>
        {
            NeedValues(s, "The script holds the data");
            Dialect? d = dialect is { Length: > 0 } x ? Enum.TryParse<Dialect>(x, true, out var dd) ? dd : throw new ArgumentException("dialect is sqlserver, postgresql or sqlite.") : null;
            var runs = s.Work.WriteScript(full, d) ?? throw new InvalidOperationException(s.Work.LastSaid);
            return new JsonObject { ["rows"] = runs.Sum(r => r.Inserted), ["rejected"] = runs.Sum(r => r.Rejected) };
        }, cancel);

    [McpServerTool(Name = "write_parity_suite", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes the proof of a migration as an AVAParityCheck suite (.parity) and its expected rows (CSV, beside it): each new table's rows the mapping makes against what's in the new table, matched on old keys. The CSVs hold the data, so it needs the person to allow values.")]
    public Task<string> WriteParitySuite(
        [Description("The new .parity file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote the parity suite", output, "A parity suite", [".parity"], (s, full) =>
        {
            NeedValues(s, "The expected rows hold the data");
            if (!s.Work.WriteParitySuite(full)) throw new InvalidOperationException(s.Work.LastSaid);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "-expected"))) _files.Wrote(f);
            return new JsonObject { ["checks"] = JsonNode.Parse(File.ReadAllText(full))!["checks"]!.AsArray().Count };
        }, cancel);

    private static void NeedValues(IExplorerSession s, string why)
    {
        if (!s.SeeRealValues) throw new InvalidOperationException($"{why}, and agents see values masked here: the person writes it (Migrate tab), or lets agents see values (AI menu).");
    }

    [McpServerTool(Name = "migrate", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Runs the migration into the new database (only into tables that are empty or that this mapping filled; every row recorded so a run can be rolled back). Only in the open window, and only when the person says yes.")]
    public Task<string> Migrate(CancellationToken cancel = default) =>
        AskFirst("ran the migration", s => $"An AI agent asks to migrate into {s.Work.Map.Target.Described ?? "the new database"}. Rows go only into tables that are empty or that this mapping filled; the run can be rolled back. Migrate?",
            s =>
            {
                var m = s.Work.Migrate() ?? throw new InvalidOperationException(s.Work.LastSaid);
                var o = Runs(s, m.Tables, 20);
                o["run"] = m.RunId;
                o["already_in"] = m.Skipped;
                return o;
            }, requireWindow: true, cancel);

    [McpServerTool(Name = "list_runs", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The migration runs of this mapping in the new database (its journal): when, done or rolled back, and how many rows each put in.")]
    public Task<string> ListRuns(CancellationToken cancel = default) =>
        Session("listed the runs", s =>
        {
            s.Work.LoadRuns();
            return new JsonObject { ["runs"] = new JsonArray([.. s.Work.Runs.Select(r => (JsonNode)new JsonObject { ["run"] = r.RunId, ["started"] = r.Started, ["status"] = r.Status, ["rows"] = r.Rows })]) };
        }, cancel);

    [McpServerTool(Name = "roll_back", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Takes out exactly the rows one migration run put in (nothing else is touched). Only in the open window, and only when the person says yes.")]
    public Task<string> RollBack(
        [Description("The run, as list_runs gives it.")] string run,
        CancellationToken cancel = default) =>
        AskFirst("rolled a run back", s => $"An AI agent asks to roll back the migration run of {s.Work.Runs.FirstOrDefault(r => r.RunId == run)?.Started ?? run}: its {s.Work.Runs.FirstOrDefault(r => r.RunId == run)?.Rows ?? 0:N0} rows come out. Roll it back?",
            s => new JsonObject { ["removed"] = s.Work.RollBack(run) }, requireWindow: true, cancel);

    // ───────────────────────────── the view ─────────────────────────────

    private static readonly string[] Tabs = ["mapping", "coverage", "checks", "profile", "dry run", "migrate"];

    [McpServerTool(Name = "get_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What the window shows: the tab, and the new table and column selected.")]
    public Task<string> GetView(CancellationToken cancel = default) =>
        Session("looked at the view", s => new JsonObject { ["tab"] = s.View.Tab, ["table"] = s.View.Table, ["column"] = s.View.Column, ["live"] = host.Live }, cancel);

    [McpServerTool(Name = "show_in_view", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Shows the person something in their window: a new table's mapping (a column selected), or the coverage, checks, profile, dry run or migrate tab. Headless, it only remembers it (render_view draws it).")]
    public Task<string> ShowInView(
        [Description(TableHelp + " Optional.")] string? table = null,
        [Description(ColumnHelp + " Optional.")] string? column = null,
        [Description("mapping, coverage, checks, profile, dry run or migrate. Default mapping.")] string tab = "mapping",
        CancellationToken cancel = default) =>
        Session("showed something", s =>
        {
            var t = tab.Trim().ToLowerInvariant();
            if (!Tabs.Contains(t)) throw new ArgumentException("tab is mapping, coverage, checks, profile, dry run or migrate.");
            var nt = table == null ? null : NewTable(s, table).Name;
            if (column != null && (nt == null || s.Work.New!.Table(nt)!.Column(column) == null)) throw new ArgumentException($"{nt ?? "?"} has no column {column}.");
            s.Show(new ViewState(t, nt, column));
            return new JsonObject { ["showing"] = t, ["table"] = nt, ["column"] = column };
        }, cancel);

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A picture (PNG) to show the person: a new table's mapping, column by column, with the checks' findings under it. Without a table, the table the window shows (or the overview).")]
    public async Task<CallToolResult> RenderView(
        [Description(TableHelp + " Optional.")] string? table = null,
        [Description("Width in pixels, from 200 to 2000. Default 1000.")] int width = 1000,
        CancellationToken cancel = default)
    {
        try
        {
            if (width is < 200 or > 2000) throw new ArgumentException("width is from 200 to 2000 pixels.");
            var picture = await host.RunAsync("drew the view", s => PictureOf(s, table ?? s.View.Table), cancel).ConfigureAwait(false);
            var png = await host.RunAsync("drew the view", _ => host.Painter.Picture(picture, width / 1000.0), cancel).ConfigureAwait(false);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = picture.Title + (picture.Subtitle.Length > 0 ? " — " + picture.Subtitle : "") }] };
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal ViewPicture PictureOf(IExplorerSession s, string? table)
    {
        var w = s.Work;
        if (w.New == null) return new ViewPicture("Nothing open", "", [], 1, 0, 0, false, []);
        if (table == null || w.New.Table(table) is not { } t)
            return new ViewPicture(w.Map.Name ?? "The mapping", $"{w.Old?.Source} → {w.New.Source}",
                [.. w.New.LoadOrder().Select(x => { var tm = w.Map.Table(x.Name); return $"{x.Name,-18} ← {(tm == null ? "nothing" : tm.From ?? "rows given"),-14} {x.Columns.Count(c => c.Identity || tm?.Column(c.Name) != null)}/{x.Columns.Count} columns"; })], 1, 0, 0, false,
                [.. w.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.ToString())]);
        var map = w.Map.Table(t.Name);
        var lines = t.Columns.Select(c => { var cm = map?.Column(c.Name); return $"{c.Name,-16} {c.Type.Declared,-14} {(cm == null ? (c.Identity ? "numbered by the new database" : "nothing") : MapText.Describe(cm))}"; }).ToList();
        var hit = s.View.Column is { } col ? t.Columns.FindIndex(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase)) + 1 : 0;
        return new ViewPicture($"{t.Name} ← {map?.From ?? (map != null ? "rows given" : "nothing")}", map?.Where is { } wh ? $"where {wh}" : "", lines, 1, hit, hit, true,
            [.. w.Findings.Where(f => f.About.StartsWith(t.Name + ".", StringComparison.OrdinalIgnoreCase) || f.About == t.Name).Select(f => $"{f.Rule.Id} {f.About}: {f.Message}")]);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static bool Side(string side) => side.Trim().ToLowerInvariant() switch
    {
        "old" or "source" => true,
        "new" or "target" => false,
        _ => throw new ArgumentException("side is old or new."),
    };

    private static DatabaseSchema Schema(IExplorerSession s, string side) =>
        (Side(side) ? s.Work.Old : s.Work.New) ?? throw new InvalidOperationException($"The {(Side(side) ? "old" : "new")} schema isn't open: open_schema opens it (or open_map).");

    private static Table NewTable(IExplorerSession s, string table) =>
        (s.Work.New ?? throw new InvalidOperationException("The new schema isn't open: open_schema new … (or open_map)."))
        .Table(table) ?? throw new ArgumentException($"The new database has no table {table}. list_tables new lists them.");

    private static Table OldTable(IExplorerSession s, string newTable) =>
        s.Work.Map.Table(newTable)?.From is { } f && s.Work.Old?.Table(f) is { } t ? t : throw new InvalidOperationException($"{newTable} has no old table yet: set_table_source first.");

    private static void CheckExpression(string expression, Table old)
    {
        if (Expression.Parse(expression).Check(old.Columns.Select(c => c.Name)) is { } bad) throw new ArgumentException(bad);
    }

    private static JsonObject TableAfter(IExplorerSession s, string table)
    {
        var said = s.Work.Findings.Where(f => f.About == table || f.About.StartsWith(table + ".", StringComparison.OrdinalIgnoreCase)).Select(f => (JsonNode)$"{f.Rule.Id} {f.Severity} {f.About}: {f.Message}").ToArray();
        return new JsonObject { ["table"] = table, ["change"] = s.Work.LastChange, ["checks"] = new JsonArray(said), ["undo_steps"] = s.Work.UndoSteps };
    }

    private static string Text(IExplorerSession s, string text) => s.SeeRealValues ? text : Mask.Text(text);
    private static string Key(IExplorerSession s, string key) => s.SeeRealValues ? key : Mask.Key(key);

    private static string Shown(object? v) => v switch
    {
        null => "null",
        string x => $"\"{x}\"",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Runs a call on the session (on the window's thread), with "values_masked" added to its answer.</summary>
    private async Task<string> Session(string action, Func<IExplorerSession, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, s =>
            {
                var o = work(s);
                o["values_masked"] = !s.SeeRealValues;
                return o.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>A call the person says yes to first (in the open window); headless, a call that needs the window is refused.</summary>
    private async Task<string> AskFirst(string action, Func<IExplorerSession, string> question, Func<IExplorerSession, JsonObject> work, bool requireWindow, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, async s =>
            {
                if (host.Live)
                {
                    if (!await s.Ask(question(s))) throw new InvalidOperationException("The person said no.");
                }
                else if (requireWindow) throw new InvalidOperationException("That's only done in the open window, with the person saying yes (AVADataMapper --mcp --attach, or its AI Chat).");
                var o = work(s);
                o["values_masked"] = !s.SeeRealValues;
                return o.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that writes one new file in the working folder, and answers with where it went.</summary>
    private async Task<string> Write(string action, string output, string what, string[] extensions, Func<IExplorerSession, string, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            var full = _files.Resolve(output, what, extensions);
            _files.CheckWritable(full);
            return await host.RunAsync(action, s =>
            {
                var o = work(s, full);
                _files.Wrote(full);
                o["wrote"] = _files.Relative(full);
                o["values_masked"] = !s.SeeRealValues;
                return o.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> Run<T>(string action, Func<IExplorerSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or ExplorerBusyException or JsonException or KeyNotFoundException or InvalidDataException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
