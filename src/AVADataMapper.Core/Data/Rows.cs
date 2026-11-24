using System.Data.Common;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Data;

/// <summary>An old row: its values by column name, any case.</summary>
public sealed class Row(Dictionary<string, object?> values)
{
    public object? this[string column] => values.TryGetValue(column, out var v) ? v : null;
    public IReadOnlyDictionary<string, object?> Values => values;
}

/// <summary>
/// Reads rows from a database: the old one for profiling, dry runs and migrations. Names are quoted for the dialect;
/// values come back as .NET values (text, long, decimal, double, bool, DateTime, bytes) or null.
/// </summary>
public static class Rows
{
    public static string Quote(Dialect d, string name) => d == Dialect.SqlServer ? $"[{name.Replace("]", "]]")}]" : $"\"{name.Replace("\"", "\"\"")}\"";

    public static string QuoteTable(Dialect d, Table t) => t.Schema.Length > 0 && d != Dialect.Sqlite ? $"{Quote(d, t.Schema)}.{Quote(d, t.Name)}" : Quote(d, t.Name);

    /// <summary>Every row of a table, in its key's order (so runs are the same each time), streamed.</summary>
    public static IEnumerable<Row> Read(DbConnection c, Dialect d, Table t, CancellationToken cancel = default)
    {
        using var cmd = c.CreateCommand();
        var order = t.PrimaryKey.Count > 0 ? " ORDER BY " + string.Join(", ", t.PrimaryKey.Select(k => Quote(d, k))) : "";
        cmd.CommandText = $"SELECT {string.Join(", ", t.Columns.Select(col => Quote(d, col.Name)))} FROM {QuoteTable(d, t)}{order}";
        cmd.CommandTimeout = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            cancel.ThrowIfCancellationRequested();
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.FieldCount; i++) values[t.Columns[i].Name] = r.IsDBNull(i) ? null : Normal(r.GetValue(i));
            yield return new Row(values);
        }
    }

    public static long Count(DbConnection c, Dialect d, Table t)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteTable(d, t)}";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Normal(object v) => v switch
    {
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        float f => (double)f,
        DateTimeOffset o => o.DateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeSpan t => t.ToString(),
        Guid g => g.ToString(),
        char ch => ch.ToString(),
        _ => v,
    };

    /// <summary>The old database's connection for a mapping: its SQLite file, or the saved connection named old.</summary>
    public static (DbConnection Connection, Dialect Dialect) OpenSide(Side side, string? mapPath, SavedConnections saved)
    {
        if (MapFile.FullPath(side, mapPath) is { } file)
        {
            if (!SchemaFiles.IsSqlite(file)) throw new InvalidOperationException($"The {side.Name} schema is a DDL script ({Path.GetFileName(file)}): it has no rows. Connect to the {side.Name} database to read them.");
            return (Catalogs.Open(Dialect.Sqlite, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = side.Name == "old" ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString()), Dialect.Sqlite);
        }
        if (saved.Get(side.Name) is { } s) return (Catalogs.Open(s.Dialect, s.Connection), s.Dialect);
        throw new InvalidOperationException($"The {side.Name} database isn't connected: File › Connect to the {(side.Name == "old" ? "Old" : "New")} Database.");
    }
}
