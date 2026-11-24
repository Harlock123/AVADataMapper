using System.Data.Common;
using System.Globalization;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Handoff;

/// <summary>
/// The migration as a SQL script, for a DBA to read and run on an empty new database: the same rows the dry run makes,
/// as INSERTs in load order with their keys written out (identity columns numbered from 1, as a fresh table would),
/// and the rejected rows listed at the end. No journal: run the migration from the program to rerun or roll back.
/// </summary>
public static class ScriptWriter
{
    public static List<TableRun> Write(MapFile map, DatabaseSchema old, DatabaseSchema neu, DbConnection oldConnection, Dialect oldDialect, Dialect target, TextWriter output, CancellationToken cancel = default)
    {
        var body = new StringWriter();
        var sink = new ScriptSink(target, body);
        var runs = Engine.Run(map, old, neu, oldConnection, oldDialect, sink, new RunOptions { KeepRejects = int.MaxValue }, cancel);
        output.WriteLine($"-- AVA Data Mapper: \"{map.Name ?? "a mapping"}\" as a script for a new, empty {target} database.");
        output.WriteLine($"-- Made {DateTime.Now:yyyy-MM-dd HH:mm} from {map.Source.Described ?? map.Source.Name}. Read and check it before running it.");
        output.WriteLine($"-- {runs.Sum(r => r.Inserted):N0} rows go in; {runs.Sum(r => r.Rejected):N0} are rejected (listed at the end) and {runs.Sum(r => r.Filtered):N0} filtered out.");
        output.WriteLine("-- Keys are written out: identity columns are numbered from 1, as a fresh table numbers them.");
        output.WriteLine();
        output.Write(body.ToString());
        output.WriteLine();
        output.WriteLine("-- Rejected rows (old key: why):");
        foreach (var r in runs.Where(r => r.Rejected > 0))
            foreach (var x in r.Rejects)
                output.WriteLine($"--   {r.Target} {x.OldKey}: {string.Join("; ", x.Reasons).Replace('\n', ' ')}");
        return runs;
    }

    private sealed class ScriptSink(Dialect d, TextWriter w) : IRowSink
    {
        private readonly Dictionary<string, int> _next = new(StringComparer.OrdinalIgnoreCase);
        private Column? _identity;

        public void Begin(Table table)
        {
            _next[table.Name] = 0;
            _identity = table.Columns.FirstOrDefault(c => c.Identity);
            w.WriteLine($"-- {table.Name}");
            if (_identity != null && d == Dialect.SqlServer) w.WriteLine($"SET IDENTITY_INSERT {Rows.QuoteTable(d, table)} ON;");
        }

        public object? Existing(Table table, string oldKey) => null;

        public object? Insert(Table table, string oldKey, IReadOnlyDictionary<string, object?> values)
        {
            object? key = null;
            var cols = new List<(Column Col, object? Value)>();
            if (_identity != null)
            {
                key = ++_next[table.Name];
                cols.Add((_identity, key));
            }
            cols.AddRange(table.Columns.Where(c => !c.Identity && values.ContainsKey(c.Name)).Select(c => (c, values[c.Name])));
            var overriding = _identity != null && d == Dialect.PostgreSql ? " OVERRIDING SYSTEM VALUE" : "";
            w.WriteLine($"INSERT INTO {Rows.QuoteTable(d, table)} ({string.Join(", ", cols.Select(c => Rows.Quote(d, c.Col.Name)))}){overriding} VALUES ({string.Join(", ", cols.Select(c => Literal(c.Value, c.Col)))});");
            return key ?? (table.PrimaryKey.Count == 1 ? values.GetValueOrDefault(table.PrimaryKey[0]) : null);
        }

        public void End(Table table)
        {
            if (_identity != null && d == Dialect.SqlServer) w.WriteLine($"SET IDENTITY_INSERT {Rows.QuoteTable(d, table)} OFF;");
            if (_identity != null && d == Dialect.PostgreSql && _next[table.Name] > 0)
                w.WriteLine($"SELECT setval(pg_get_serial_sequence('{Rows.QuoteTable(d, table).Replace("'", "''")}', '{_identity.Name}'), {_next[table.Name]});");
            if (d == Dialect.SqlServer) w.WriteLine("GO");
            w.WriteLine();
        }

        private string Literal(object? v, Column c) => v switch
        {
            null => "NULL",
            bool b => d == Dialect.PostgreSql ? (b ? "TRUE" : "FALSE") : b ? "1" : "0",
            DateTime t => $"'{(c.Type.Kind == TypeKind.Date ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}'",
            string s => (d == Dialect.SqlServer ? "N'" : "'") + s.Replace("'", "''") + "'",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => "'" + v.ToString()!.Replace("'", "''") + "'",
        };
    }
}
