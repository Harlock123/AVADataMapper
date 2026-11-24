using System.Data.Common;
using System.Globalization;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Data;

/// <summary>A migration's run, as the journal keeps it.</summary>
public sealed record RunRecord(string RunId, string MapId, string Started, string? Finished, string Status, long Rows);

/// <summary>What a migration did: its run, and each table's counts and rejects.</summary>
public sealed class MigrationResult
{
    public required string RunId { get; init; }
    public required List<TableRun> Tables { get; init; }
    public required string Status { get; init; }
    public long Inserted => Tables.Sum(t => t.Inserted);
    public long Rejected => Tables.Sum(t => t.Rejected);
    public long Skipped => Tables.Sum(t => t.Skipped);
}

/// <summary>
/// The journal a migration keeps in the new database: its runs (avadm_runs) and, for every row it put in, the table,
/// the old key and the new key (avadm_keys). Reruns pick up from it, rollback removes exactly what one run put in,
/// and the parity suite keys its checks on it.
/// </summary>
public static class Journal
{
    public const string Runs = "avadm_runs";
    public const string Keys = "avadm_keys";

    public static void Ensure(DbConnection c, Dialect d)
    {
        var text = d == Dialect.SqlServer ? "nvarchar" : "varchar";
        Exec(c, null, d == Dialect.SqlServer
            ? $"IF OBJECT_ID('{Runs}') IS NULL CREATE TABLE {Runs} (run_id varchar(40) NOT NULL PRIMARY KEY, map_id varchar(40) NOT NULL, started varchar(30) NOT NULL, finished varchar(30) NULL, status varchar(20) NOT NULL)"
            : $"CREATE TABLE IF NOT EXISTS {Runs} (run_id varchar(40) NOT NULL PRIMARY KEY, map_id varchar(40) NOT NULL, started varchar(30) NOT NULL, finished varchar(30) NULL, status varchar(20) NOT NULL)");
        Exec(c, null, d == Dialect.SqlServer
            ? $"IF OBJECT_ID('{Keys}') IS NULL CREATE TABLE {Keys} (run_id varchar(40) NOT NULL, map_id varchar(40) NOT NULL, new_table {text}(256) NOT NULL, old_key {text}(400) NOT NULL, new_key {text}(400) NOT NULL)"
            : $"CREATE TABLE IF NOT EXISTS {Keys} (run_id varchar(40) NOT NULL, map_id varchar(40) NOT NULL, new_table varchar(256) NOT NULL, old_key varchar(400) NOT NULL, new_key varchar(400) NOT NULL)");
    }

    public static bool Exists(DbConnection c, Dialect d)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = d switch
        {
            Dialect.SqlServer => $"SELECT COUNT(*) FROM sys.tables WHERE name = '{Runs}'",
            Dialect.PostgreSql => $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{Runs}'",
            _ => $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{Runs}'",
        };
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static List<RunRecord> List(DbConnection c, Dialect d, string? mapId = null)
    {
        if (!Exists(c, d)) return [];
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT r.run_id, r.map_id, r.started, r.finished, r.status, (SELECT COUNT(*) FROM {Keys} k WHERE k.run_id = r.run_id) FROM {Runs} r" + (mapId != null ? " WHERE r.map_id = @m" : "") + " ORDER BY r.started";
        if (mapId != null) Param(cmd, "@m", mapId);
        using var r = cmd.ExecuteReader();
        var list = new List<RunRecord>();
        while (r.Read()) list.Add(new RunRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture)));
        return list;
    }

    internal static void Exec(DbConnection c, DbTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) Param(cmd, n, v);
        cmd.ExecuteNonQuery();
    }

    internal static void Param(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes rows into the new database, in batches (a transaction each), keeping the journal. It only writes to a table
/// that's empty or holds only rows this mapping put there; it never changes or deletes a row it didn't write.
/// </summary>
public sealed class MigrationSink(DbConnection c, Dialect d, string mapId, string runId, int batch = 500) : IRowSink
{
    private DbTransaction? _tx;
    private int _inBatch;
    private Dictionary<string, string> _done = new(StringComparer.OrdinalIgnoreCase);

    public void Begin(Table table)
    {
        long rows, ours;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {Rows.QuoteTable(d, table)}";
            rows = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        _done = new(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT k.old_key, k.new_key FROM {Journal.Keys} k JOIN {Journal.Runs} r ON r.run_id = k.run_id WHERE k.map_id = @m AND k.new_table = @t AND r.status <> 'rolled back'";
            Journal.Param(cmd, "@m", mapId);
            Journal.Param(cmd, "@t", table.Name);
            using var r = cmd.ExecuteReader();
            while (r.Read()) _done[r.GetString(0)] = r.GetString(1);
        }
        ours = _done.Count;
        if (rows > ours)
            throw new InvalidOperationException($"{table.Name} has {rows - ours:N0} row{(rows - ours == 1 ? "" : "s")} this mapping didn't put there: a migration only writes to empty tables (or ones it filled), and never changes rows it didn't write.");
        _tx = c.BeginTransaction();
        _inBatch = 0;
    }

    public object? Existing(Table table, string oldKey)
    {
        if (!_done.TryGetValue(oldKey, out var key)) return null;
        var pk = table.PrimaryKey.Count == 1 ? table.Column(table.PrimaryKey[0]) : null;
        return pk?.Type.IsWholeNumber == true && long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)(int)n : key;
    }

    public object? Insert(Table table, string oldKey, IReadOnlyDictionary<string, object?> values)
    {
        var identity = table.Columns.FirstOrDefault(x => x.Identity);
        var cols = table.Columns.Where(x => !x.Identity && values.ContainsKey(x.Name)).ToList();
        var names = string.Join(", ", cols.Select(x => Rows.Quote(d, x.Name)));
        var ps = string.Join(", ", cols.Select((_, i) => $"@p{i}"));
        var into = cols.Count == 0 ? (d == Dialect.SqlServer ? "DEFAULT VALUES" : "DEFAULT VALUES") : $"({names}) {{0}}VALUES ({ps})";
        var output = identity != null && d == Dialect.SqlServer ? $"OUTPUT INSERTED.{Rows.Quote(d, identity.Name)} " : "";
        var returning = identity != null && d != Dialect.SqlServer ? $" RETURNING {Rows.Quote(d, identity.Name)}" : "";
        using var cmd = c.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = $"INSERT INTO {Rows.QuoteTable(d, table)} " + (cols.Count == 0 ? output + into : string.Format(CultureInfo.InvariantCulture, into, output)) + returning;
        for (var i = 0; i < cols.Count; i++) Journal.Param(cmd, $"@p{i}", Value(values[cols[i].Name], cols[i]));
        object? key;
        if (d == Dialect.PostgreSql) Journal.Exec(c, _tx, "SAVEPOINT avadm_row");
        try
        {
            key = identity != null ? Normal(cmd.ExecuteScalar()) : null;
            if (identity == null) cmd.ExecuteNonQuery();
        }
        catch (DbException)
        {
            if (d == Dialect.PostgreSql) Journal.Exec(c, _tx, "ROLLBACK TO SAVEPOINT avadm_row");
            throw;
        }
        key ??= table.PrimaryKey.Count == 1 ? values.GetValueOrDefault(table.PrimaryKey[0]) : null;
        Journal.Exec(c, _tx, $"INSERT INTO {Journal.Keys} (run_id, map_id, new_table, old_key, new_key) VALUES (@r, @m, @t, @o, @n)",
            ("@r", runId), ("@m", mapId), ("@t", table.Name), ("@o", oldKey), ("@n", Expression.Str(key) ?? ""));
        if (++_inBatch >= batch)
        {
            _tx!.Commit();
            _tx = c.BeginTransaction();
            _inBatch = 0;
        }
        return key;
    }

    public void End(Table table)
    {
        _tx?.Commit();
        _tx = null;
    }

    /// <summary>A value as the new database's driver takes it (SQLite has no booleans or dates of its own).</summary>
    private object? Value(object? v, Column col) => (d, v) switch
    {
        (_, null) => null,
        (Dialect.Sqlite, bool b) => b ? 1L : 0L,
        (Dialect.Sqlite, DateTime t) => col.Type.Kind == TypeKind.Date ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        (Dialect.PostgreSql, DateTime t) when col.Type.Kind == TypeKind.Date => DateOnly.FromDateTime(t),
        _ => v,
    };

    private static object? Normal(object? v) => v switch { long l when l is >= int.MinValue and <= int.MaxValue => (int)l, DBNull => null, _ => v };
}

/// <summary>
/// Migrates: the engine with a <see cref="MigrationSink"/>, under a journal. A run that stops (cancelled, or the
/// database gone) leaves what it put in, recorded; running the same mapping again skips those rows and carries on.
/// </summary>
public static class Migrator
{
    public static MigrationResult Run(MapFile map, DatabaseSchema old, DatabaseSchema neu, DbConnection oldConnection, Dialect oldDialect,
        DbConnection newConnection, Dialect newDialect, RunOptions? options = null, CancellationToken cancel = default)
    {
        var errors = MapChecks.Check(map, old, neu).Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count > 0) throw new InvalidOperationException($"The mapping has {errors.Count} error{(errors.Count == 1 ? "" : "s")}: {errors[0]}");
        Journal.Ensure(newConnection, newDialect);
        var runId = Guid.NewGuid().ToString("N");
        Journal.Exec(newConnection, null, $"INSERT INTO {Journal.Runs} (run_id, map_id, started, status) VALUES (@r, @m, @s, 'running')", ("@r", runId), ("@m", map.Id), ("@s", Journal.Now()));
        var status = "stopped";
        try
        {
            var tables = Engine.Run(map, old, neu, oldConnection, oldDialect, new MigrationSink(newConnection, newDialect, map.Id, runId), options, cancel);
            status = "done";
            return new MigrationResult { RunId = runId, Tables = tables, Status = status };
        }
        finally
        {
            Journal.Exec(newConnection, null, $"UPDATE {Journal.Runs} SET finished = @f, status = @s WHERE run_id = @r", ("@f", Journal.Now()), ("@s", status), ("@r", runId));
        }
    }

    /// <summary>
    /// Removes exactly the rows a run put in, children before parents, and marks the run rolled back. Rows it didn't
    /// write are never touched. Returns how many rows it removed.
    /// </summary>
    public static long Rollback(DatabaseSchema neu, DbConnection c, Dialect d, string runId)
    {
        if (!Journal.Exists(c, d)) throw new InvalidOperationException("The new database has no migration journal: nothing here was put in by a migration.");
        var removed = 0L;
        using var tx = c.BeginTransaction();
        foreach (var t in Enumerable.Reverse(neu.LoadOrder()))
        {
            var key = t.PrimaryKey.Count == 1 ? t.Column(t.PrimaryKey[0]) : null;
            if (key == null) continue;
            var cast = d switch { Dialect.SqlServer => $"CAST({Rows.Quote(d, key.Name)} AS nvarchar(400))", Dialect.PostgreSql => $"CAST({Rows.Quote(d, key.Name)} AS text)", _ => $"CAST({Rows.Quote(d, key.Name)} AS TEXT)" };
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {Rows.QuoteTable(d, t)} WHERE {cast} IN (SELECT new_key FROM {Journal.Keys} WHERE run_id = @r AND new_table = @t)";
            Journal.Param(cmd, "@r", runId);
            Journal.Param(cmd, "@t", t.Name);
            removed += cmd.ExecuteNonQuery();
        }
        Journal.Exec(c, tx, $"DELETE FROM {Journal.Keys} WHERE run_id = @r", ("@r", runId));
        Journal.Exec(c, tx, $"UPDATE {Journal.Runs} SET status = 'rolled back', finished = @f WHERE run_id = @r", ("@f", Journal.Now()), ("@r", runId));
        tx.Commit();
        return removed;
    }
}
