using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Handoff;

/// <summary>
/// The proof, as an AVAParityCheck suite (.parity): for each new table, the rows the mapping says should be there (a CSV
/// made from the old rows by the same engine as the migration) against what the new database holds after it, matched
/// on each row's old key through the migration's journal. Columns that point at another table compare that table's old
/// keys, so the check doesn't depend on how the new database numbered its rows. The new database is the saved
/// connection "new" in AVAParityCheck (or its SQLite file): no connection string is written.
/// </summary>
public static class ParityPlan
{
    public const string Format = "avaparitycheck.suite";
    public const string Extension = ".parity";

    /// <summary>Writes the suite and its expected-rows CSVs (in an "expected" folder beside it); returns the dry run it came from.</summary>
    public static List<TableRun> Write(MapFile map, string? mapPath, DatabaseSchema old, DatabaseSchema neu, DbConnection oldConnection, Dialect oldDialect, Dialect newDialect, string suitePath, CancellationToken cancel = default)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(suitePath))!;
        var expected = Path.Combine(folder, Path.GetFileNameWithoutExtension(suitePath) + "-expected");
        Directory.CreateDirectory(expected);
        var sink = new ExpectedSink();
        var runs = Engine.Run(map, old, neu, oldConnection, oldDialect, sink, new RunOptions(), cancel);
        var checks = new JsonArray();
        var newFile = MapFile.FullPath(map.Target, mapPath) is { } f && SchemaFiles.IsSqlite(f) ? f : null;
        foreach (var t in neu.LoadOrder())
        {
            if (map.Table(t.Name) is not { } tm || !sink.Rows.TryGetValue(t.Name, out var rows) || t.PrimaryKey.Count != 1) continue;
            var columns = t.Columns.Where(c => !c.Identity).ToList();
            var parents = columns.ToDictionary(c => c.Name, c => t.ForeignKeys.FirstOrDefault(k => k.Columns.Count == 1 && k.Columns[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } fk
                && neu.Table(fk.RefTable) is { } p && p.Columns.Any(x => x.Identity) ? p : null, StringComparer.OrdinalIgnoreCase);
            // The expected rows: the old key, then every column, a parent's key as that parent's old key.
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", new[] { "old_key" }.Concat(columns.Select(c => c.Name)).Select(Csv)));
            foreach (var (oldKey, values) in rows)
                csv.AppendLine(string.Join(",", new[] { oldKey }.Concat(columns.Select(c => parents[c.Name] is { } p && values.GetValueOrDefault(c.Name) is { } v
                    ? sink.OldKey(p.Name, v) ?? Expression.Str(v) ?? "" : Text(values.GetValueOrDefault(c.Name), c))).Select(Csv)));
            var file = Path.Combine(expected, $"{t.Name}.csv");
            File.WriteAllText(file, csv.ToString());
            var pk = t.Column(t.PrimaryKey[0])!;
            string Cast(string x) => newDialect switch { Dialect.SqlServer => $"CAST({x} AS nvarchar(400))", Dialect.PostgreSql => $"CAST({x} AS text)", _ => $"CAST({x} AS TEXT)" };
            string Q(string n) => Rows.Quote(newDialect, n);
            var id = map.Id.Replace("'", "''");
            var select = new List<string> { $"k.old_key AS old_key" };
            foreach (var c in columns)
            {
                if (parents[c.Name] is { } p)
                {
                    var ppk = p.Column(p.PrimaryKey[0])!;
                    select.Add($"(SELECT k2.old_key FROM {Journal.Keys} k2 WHERE k2.map_id = '{id}' AND k2.new_table = '{p.Name}' AND k2.new_key = {Cast("t." + Q(c.Name))}) AS {Q(c.Name)}");
                    _ = ppk;
                }
                else if (c.Type.Kind is TypeKind.Boolean or TypeKind.Bit)
                    select.Add(newDialect == Dialect.PostgreSql && c.Type.Kind == TypeKind.Boolean
                        ? $"CASE WHEN t.{Q(c.Name)} THEN 'true' WHEN NOT t.{Q(c.Name)} THEN 'false' END AS {Q(c.Name)}"
                        : $"CASE WHEN t.{Q(c.Name)} = 1 THEN 'true' WHEN t.{Q(c.Name)} = 0 THEN 'false' END AS {Q(c.Name)}");
                else select.Add($"t.{Q(c.Name)}");
            }
            var sql = $"SELECT {string.Join(", ", select)} FROM {Rows.QuoteTable(newDialect, t)} t JOIN {Journal.Keys} k ON k.map_id = '{id}' AND k.new_table = '{t.Name}' AND k.new_key = {Cast("t." + Q(pk.Name))} ORDER BY k.old_key";
            var newSide = new JsonObject { ["kind"] = "sql", ["sql"] = sql };
            if (newFile != null) newSide["path"] = Path.GetRelativePath(folder, newFile).Replace('\\', '/');
            else newSide["connection"] = "new";
            var rules = new JsonArray();
            foreach (var c in columns.Where(c => parents[c.Name] == null))
            {
                if (c.Type.Kind is TypeKind.Decimal or TypeKind.Money or TypeKind.Float) rules.Add(new JsonObject { ["column"] = c.Name, ["abs_tolerance"] = 0.001 });
                else if (c.Type.IsTemporal) rules.Add(new JsonObject { ["column"] = c.Name, ["date"] = true });
                else if (c.Type.IsText) rules.Add(new JsonObject { ["column"] = c.Name, ["trim"] = true });
            }
            var run = runs.First(r => r.Target == t.Name);
            checks.Add(new JsonObject
            {
                ["name"] = $"table {t.Name}",
                ["old"] = new JsonObject { ["kind"] = "csv", ["path"] = Path.GetRelativePath(folder, file).Replace('\\', '/') },
                ["new"] = newSide,
                ["keys"] = new JsonArray("old_key"),
                ["mapping"] = new JsonObject(),
                ["ignored"] = new JsonArray(),
                ["rules"] = rules,
                ["accept"] = new JsonArray(),
                ["note"] = $"The {run.Inserted:N0} rows the mapping makes{(tm.From != null ? $" from {tm.From}" : " (given rows)")}, against what's in {t.Name} after the migration, matched on each row's old key (the migration's journal, {Journal.Keys})." +
                           (parents.Values.Any(p => p != null) ? $" {string.Join(", ", parents.Where(p => p.Value != null).Select(p => p.Key))} compare their rows' old keys, not the new numbers." : "") +
                           (run.Rejected > 0 ? $" {run.Rejected:N0} rows were rejected and aren't expected; see the migration report." : ""),
            });
        }
        var suite = new JsonObject { ["format"] = Format, ["version"] = 1, ["checks"] = checks };
        File.WriteAllText(suitePath, suite.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        return runs;
    }

    private static string Text(object? v, Column c) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        DateTime t => c.Type.Kind == TypeKind.Date ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Expression.Str(v) ?? "",
    };

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s != s.Trim() ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Keeps every row the engine makes, with its old key, and which old key each new key stands for.</summary>
    private sealed class ExpectedSink : IRowSink
    {
        private readonly CountingSink _numbers = new();
        private readonly Dictionary<string, Dictionary<string, string>> _old = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<(string OldKey, IReadOnlyDictionary<string, object?> Values)>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? OldKey(string table, object newKey) => _old.TryGetValue(table, out var m) && m.TryGetValue(Expression.Str(newKey) ?? "", out var k) ? k : null;

        public void Begin(Table table)
        {
            _numbers.Begin(table);
            Rows[table.Name] = [];
            _old[table.Name] = new(StringComparer.OrdinalIgnoreCase);
        }

        public object? Existing(Table table, string oldKey) => null;

        public object? Insert(Table table, string oldKey, IReadOnlyDictionary<string, object?> values)
        {
            var key = _numbers.Insert(table, oldKey, values);
            Rows[table.Name].Add((oldKey, values));
            if (key != null) _old[table.Name][Expression.Str(key)!] = oldKey;
            return key;
        }

        public void End(Table table) { }
    }
}
