using System.Data.Common;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Data;

/// <summary>A row the new database wouldn't take (or a migration didn't), its old key, why, and the old row's values.</summary>
public sealed record Reject(string OldKey, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, object?>? Old);

/// <summary>What happened to one new table: rows read, filtered out, put in (or that would be), and rejected.</summary>
public sealed class TableRun
{
    public required string Target { get; init; }
    public string? From { get; init; }
    public long Read { get; set; }
    public long Filtered { get; set; }

    /// <summary>Rows a previous run of the same mapping already put in (a rerun picks up where it left off).</summary>
    public long Skipped { get; set; }

    public long Inserted { get; set; }
    public List<Reject> Rejects { get; } = [];
    public long Rejected { get; set; }

    /// <summary>Of those rejected, how many only because a row they point at isn't migrated.</summary>
    public long Followed { get; set; }

    public List<string> Notes { get; } = [];

    /// <summary>The first new rows (for a look at what's made).</summary>
    public List<IReadOnlyDictionary<string, object?>> Sample { get; } = [];

    /// <summary>Rejects by their first reason with its values taken out ("procedure_code takes 5 characters…"), commonest first.</summary>
    public List<(string Reason, int Count)> Groups =>
        [.. Rejects.GroupBy(r => Pattern(r.Reasons[0])).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal)];

    internal static string Pattern(string reason) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(reason, "\"[^\"]*\"", "\"…\""), @"(?<![A-Za-z_])-?\d+(\.\d+)?(?![A-Za-z_])", "#");
}

/// <summary>Where the engine's rows go: a dry run counts them; a migration writes them. Returns the row's new key.</summary>
public interface IRowSink
{
    /// <summary>Called once before a table's rows.</summary>
    void Begin(Table table);

    /// <summary>The new key of the row an earlier run made from this old row, or null when there's none.</summary>
    object? Existing(Table table, string oldKey);

    /// <summary>Puts a row in (or pretends to); returns its key: the identity the database gave it, or its primary key's value.</summary>
    object? Insert(Table table, string oldKey, IReadOnlyDictionary<string, object?> values);

    void End(Table table);
}

/// <summary>Counts, and numbers identity keys as a database would: what a dry run needs.</summary>
public sealed class CountingSink : IRowSink
{
    private readonly Dictionary<string, long> _next = new(StringComparer.OrdinalIgnoreCase);

    public void Begin(Table table) => _next[table.Name] = 0;

    public object? Existing(Table table, string oldKey) => null;

    public object? Insert(Table table, string oldKey, IReadOnlyDictionary<string, object?> values)
    {
        var id = table.Columns.FirstOrDefault(c => c.Identity);
        if (id != null) return (int)++_next[table.Name];
        return table.PrimaryKey.Count == 1 ? values.GetValueOrDefault(table.PrimaryKey[0]) : null;
    }

    public void End(Table table) { }
}

public sealed class RunOptions
{
    /// <summary>How many rejects to keep each table's details of (all are counted).</summary>
    public int KeepRejects { get; init; } = 500;

    /// <summary>How many new rows to keep each table's sample of.</summary>
    public int KeepSample { get; init; } = 20;

    /// <summary>Only these new tables (null: all mapped).</summary>
    public IReadOnlyCollection<string>? Tables { get; init; }

    public IProgress<string>? Progress { get; init; }
}

/// <summary>
/// Makes the new database's rows from the old one's, table by table in the order foreign keys need: given rows for code
/// tables; for the rest, each old row through the filter and every column's transform, then checked as the new database
/// would (required columns, lengths, keys and codes that must exist, unique columns, its checks). A row that passes goes
/// to the sink, and its new key into the key map for the tables after it; a row that doesn't is rejected with every
/// reason it has, and rows that point at it are rejected in turn. The dry run and the migration both run this.
/// </summary>
public static class Engine
{
    public static List<TableRun> Run(MapFile map, DatabaseSchema old, DatabaseSchema neu, DbConnection oldConnection, Dialect oldDialect, IRowSink sink, RunOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new RunOptions();
        var ctx = new TransformContext();
        var runs = new List<TableRun>();
        // Values of every new column a foreign key or a unique key names, as rows are made: for checking later rows.
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var nt in neu.LoadOrder())
        {
            cancel.ThrowIfCancellationRequested();
            var tm = map.Table(nt.Name);
            if (tm == null || options.Tables != null && !options.Tables.Contains(nt.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var run = new TableRun { Target = nt.Name, From = tm.From };
            runs.Add(run);
            options.Progress?.Report($"{nt.Name}…");
            ctx.Keys[nt.Name] = new(StringComparer.OrdinalIgnoreCase);
            ctx.Gone[nt.Name] = new(StringComparer.OrdinalIgnoreCase);
            var checks = new List<(string Text, Expression? Parsed)>();
            foreach (var ck in nt.Checks)
                try { checks.Add((ck, Expression.Parse(ck))); }
                catch (FormatException) { run.Notes.Add($"The check ({ck}) isn't in a form the dry run reads: the database checks it."); }
            sink.Begin(nt);
            if (tm.From == null)
            {
                var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (given, i) in (tm.Rows ?? []).Select((r, i) => (r, i)))
                {
                    run.Read++;
                    var givenKey = nt.PrimaryKey.Count == 1 && given.TryGetValue(nt.PrimaryKey[0], out var gk) ? Expression.Str(Transforms.Clr(gk)) ?? $"row {i + 1}" : $"row {i + 1}";
                    if (Skip(run, nt, givenKey, ctx, seen, sink, neu)) { codes.Add(givenKey); continue; }
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var reasons = new List<string>();
                    foreach (var c in nt.Columns.Where(c => !c.Identity))
                    {
                        var o = Transforms.Fit(given.TryGetValue(c.Name, out var v) ? Transforms.Clr(v) : null, c);
                        if (o.Rejected != null) reasons.Add(o.Rejected);
                        else if (!o.UseDefault) values[c.Name] = o.Value;
                    }
                    Finish(run, nt, givenKey, values, reasons, null, ctx, seen, checks, sink, options, neu);
                    if (reasons.Count == 0 && nt.PrimaryKey.Count == 1 && values.TryGetValue(nt.PrimaryKey[0], out var code)) codes.Add(Expression.Str(code)!);
                }
                ctx.Codes[nt.Name] = codes;
            }
            else
            {
                var ot = old.Table(tm.From) ?? throw new InvalidOperationException($"The old database has no table {tm.From}.");
                var where = tm.Where == null ? null : Expression.Parse(tm.Where);
                var keyCols = tm.OldKey ?? ot.PrimaryKey;
                foreach (var row in Rows.Read(oldConnection, oldDialect, ot, cancel))
                {
                    run.Read++;
                    var oldKey = keyCols.Count == 0 ? $"#{run.Read}" : string.Join("|", keyCols.Select(k => TransformContext.KeyText(row[k])));
                    if (where != null && !where.Test(c => row[c]))
                    {
                        run.Filtered++;
                        ctx.Gone[nt.Name][oldKey] = $"filtered out by \"{tm.Where}\"";
                        continue;
                    }
                    if (Skip(run, nt, oldKey, ctx, seen, sink, neu)) continue;
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var reasons = new List<string>();
                    foreach (var c in nt.Columns)
                    {
                        var cm = tm.Column(c.Name);
                        if (cm == null)
                        {
                            if (!c.Identity && !c.Nullable && c.Default == null) reasons.Add($"{c.Name} is required, and nothing fills it");
                            continue;
                        }
                        if (cm.Kind is ColumnKind.Generated or ColumnKind.Default) continue;
                        var o = Transforms.Apply(cm, n => row[n], c, ctx);
                        if (o.Rejected != null) reasons.Add(o.Rejected);
                        else if (!o.UseDefault) values[c.Name] = o.Value;
                    }
                    Finish(run, nt, oldKey, values, reasons, row, ctx, seen, checks, sink, options, neu);
                }
            }
            sink.End(nt);
        }
        return runs;
    }

    private static void Finish(TableRun run, Table nt, string oldKey, Dictionary<string, object?> values, List<string> reasons, Row? row,
        TransformContext ctx, Dictionary<string, HashSet<string>> seen, List<(string Text, Expression? Parsed)> checks, IRowSink sink, RunOptions options, DatabaseSchema neu)
    {
        if (reasons.Count == 0)
        {
            // What the new database would refuse: a key or code that isn't there, a duplicate, a broken check.
            foreach (var fk in nt.ForeignKeys)
            {
                var vals = fk.Columns.Select(c => values.GetValueOrDefault(c)).ToList();
                if (vals.Any(v => v == null)) continue;
                var target = neu.Table(fk.RefTable);
                var set = seen.GetValueOrDefault(Slot(target?.Name ?? fk.RefTable, fk.RefColumns));
                if (set != null && !set.Contains(Text(vals))) reasons.Add($"{string.Join(", ", fk.Columns)} {Text(vals)} isn't in {target?.Name ?? fk.RefTable}");
            }
            foreach (var key in Keys(nt))
            {
                var vals = key.Select(c => values.GetValueOrDefault(c)).ToList();
                if (vals.All(v => v == null)) continue;
                var slot = Slot(nt.Name, key);
                if (seen.TryGetValue(slot, out var set) && set.Contains(Text(vals))) reasons.Add($"{string.Join(", ", key)} {Text(vals)} is already in {nt.Name}");
            }
            foreach (var (text, parsed) in checks)
                if (parsed?.Evaluate(c => values.GetValueOrDefault(c)) is false) reasons.Add($"it breaks {nt.Name}'s check ({text})");
        }
        if (reasons.Count > 0)
        {
            // A row that's gone because its parent is gone says the parent's first cause, once, not the whole chain.
            var own = reasons.Where(r => Root(r) == null).ToList();
            var roots = reasons.Select(Root).OfType<string>().Distinct().ToList();
            reasons = [.. own, .. roots.Select(r => reasons.First(x => Root(x) == r))];
            run.Rejected++;
            if (own.Count == 0) run.Followed++;
            ctx.Gone[nt.Name][oldKey] = own.Count > 0 ? "rejected: " + own[0] : roots[0];
            if (run.Rejects.Count < options.KeepRejects) run.Rejects.Add(new Reject(oldKey, reasons, row?.Values));
            return;
        }
        object? newKey;
        try
        {
            newKey = sink.Insert(nt, oldKey, values);
        }
        catch (DbException e)
        {
            run.Rejected++;
            ctx.Gone[nt.Name][oldKey] = "rejected by the database: " + e.Message;
            if (run.Rejects.Count < options.KeepRejects) run.Rejects.Add(new Reject(oldKey, ["the database refused it: " + e.Message], row?.Values));
            return;
        }
        run.Inserted++;
        if (run.Sample.Count < options.KeepSample) run.Sample.Add(values);
        if (newKey != null) ctx.Keys[nt.Name][oldKey] = newKey;
        var identity = nt.Columns.FirstOrDefault(c => c.Identity);
        if (identity != null && newKey != null) values[identity.Name] = newKey;
        foreach (var key in Keys(nt).Concat(Referenced(nt, neu)).DistinctBy(k => string.Join(",", k)))
        {
            var vals = key.Select(c => values.GetValueOrDefault(c)).ToList();
            if (vals.Any(v => v == null)) continue;
            var slot = Slot(nt.Name, key);
            if (!seen.TryGetValue(slot, out var set)) seen[slot] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(Text(vals));
        }
    }

    /// <summary>A row an earlier run already put in: its key goes into the key map (and the keys seen), and it's skipped.</summary>
    private static bool Skip(TableRun run, Table nt, string oldKey, TransformContext ctx, Dictionary<string, HashSet<string>> seen, IRowSink sink, DatabaseSchema neu)
    {
        if (sink.Existing(nt, oldKey) is not { } key) return false;
        run.Skipped++;
        ctx.Keys[nt.Name][oldKey] = key;
        if (nt.PrimaryKey.Count == 1)
        {
            var slot = Slot(nt.Name, nt.PrimaryKey);
            if (!seen.TryGetValue(slot, out var set)) seen[slot] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(Text([key]));
        }
        return true;
    }

    private const string Marker = " row isn't migrated (";

    /// <summary>The first cause behind a "its patient row isn't migrated (…)" reason, or null for a row's own reason.</summary>
    public static string? Root(string reason)
    {
        var i = reason.IndexOf(Marker, StringComparison.Ordinal);
        return i < 0 || !reason.EndsWith(')') ? null : reason[(i + Marker.Length)..^1];
    }

    private static IEnumerable<List<string>> Keys(Table t) => (t.PrimaryKey.Count > 0 ? [t.PrimaryKey] : Array.Empty<List<string>>()).Concat(t.Uniques);

    /// <summary>The column sets of this table other tables' foreign keys point at.</summary>
    private static IEnumerable<List<string>> Referenced(Table t, DatabaseSchema neu) =>
        neu.Tables.SelectMany(x => x.ForeignKeys).Where(f => neu.Table(f.RefTable) == t).Select(f => f.RefColumns.ToList());

    private static string Slot(string table, IEnumerable<string> columns) => table + ":" + string.Join(",", columns).ToLowerInvariant();

    private static string Text(IEnumerable<object?> vals) => string.Join("|", vals.Select(v => Expression.Str(v)?.TrimEnd() ?? ""));
}

/// <summary>A dry run: the whole migration made in memory from the old rows, and nothing written anywhere.</summary>
public static class DryRun
{
    public static List<TableRun> Run(MapFile map, DatabaseSchema old, DatabaseSchema neu, DbConnection oldConnection, Dialect oldDialect, RunOptions? options = null, CancellationToken cancel = default) =>
        Engine.Run(map, old, neu, oldConnection, oldDialect, new CountingSink(), options, cancel);
}
