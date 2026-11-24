using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace AVADataMapper.Core.Schema;

/// <summary>
/// Reads a live database's schema from its catalog (SQL Server's sys views, PostgreSQL's pg_catalog, SQLite's pragmas):
/// never a table's rows. Profiling, dry runs and migrations read rows; reading a schema doesn't.
/// </summary>
public static class Catalogs
{
    /// <summary>Opens a connection for a dialect and connection string (SQLite: a file's path or a connection string).</summary>
    public static DbConnection Open(Dialect dialect, string connection)
    {
        DbConnection c = dialect switch
        {
            Dialect.SqlServer => new SqlConnection(connection),
            Dialect.PostgreSql => new NpgsqlConnection(connection),
            _ => new SqliteConnection(connection.Contains('=') ? connection : new SqliteConnectionStringBuilder { DataSource = connection }.ToString()),
        };
        c.Open();
        return c;
    }

    /// <summary>How a database is named for people: its server and database, or a SQLite file's name. Never the connection string.</summary>
    public static string Describe(Dialect dialect, string connection)
    {
        try
        {
            return dialect switch
            {
                Dialect.SqlServer => new SqlConnectionStringBuilder(connection) is var b ? $"{b.DataSource}/{b.InitialCatalog}" : "",
                Dialect.PostgreSql => new NpgsqlConnectionStringBuilder(connection) is var p ? $"{p.Host}:{p.Port}/{p.Database}" : "",
                _ => Path.GetFileName(connection.Contains('=') ? new SqliteConnectionStringBuilder(connection).DataSource : connection),
            };
        }
        catch (ArgumentException)
        {
            return dialect.ToString();
        }
    }

    public static DatabaseSchema Read(DbConnection connection, Dialect dialect, string source) => dialect switch
    {
        Dialect.SqlServer => SqlServer(connection, source),
        Dialect.PostgreSql => Postgres(connection, source),
        _ => Sqlite(connection, source),
    };

    /// <summary>A SQLite file's schema.</summary>
    public static DatabaseSchema ReadSqliteFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"There's no file {path}.");
        using var c = Open(Dialect.Sqlite, new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        return Sqlite(c, Path.GetFileName(path));
    }

    private static List<object?[]> Rows(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static string S(object? v) => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    private static int I(object? v) => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);

    private static DatabaseSchema Sqlite(DbConnection c, string source)
    {
        var schema = new DatabaseSchema { Dialect = Dialect.Sqlite, Source = source };
        foreach (var t in Rows(c, "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid"))
        {
            var name = S(t[0]);
            var table = new Table { Name = name };
            var q = name.Replace("\"", "\"\"");
            var info = Rows(c, $"PRAGMA table_info(\"{q}\")");
            var pk = info.Where(r => I(r[5]) > 0).OrderBy(r => I(r[5])).Select(r => S(r[1])).ToList();
            foreach (var r in info)
            {
                var declared = S(r[2]);
                var identity = pk.Count == 1 && pk[0] == S(r[1]) && declared.Equals("integer", StringComparison.OrdinalIgnoreCase);
                table.Columns.Add(new Column
                {
                    Name = S(r[1]), Type = DataType.Parse(declared.Length > 0 ? declared : "blob"), Nullable = I(r[3]) == 0 && !pk.Contains(S(r[1])),
                    Default = r[4] == null ? null : S(r[4]), Identity = identity, Ordinal = I(r[0]) + 1,
                });
            }
            table.PrimaryKey.AddRange(pk);
            // SQLite numbers foreign keys newest first: read them in the order they were declared.
            foreach (var g in Rows(c, $"PRAGMA foreign_key_list(\"{q}\")").GroupBy(r => I(r[0])).OrderByDescending(g => g.Key))
                table.ForeignKeys.Add(new ForeignKey($"fk_{name}_{g.Key}", [.. g.OrderBy(r => I(r[1])).Select(r => S(r[3]))], S(g.First()[2]), [.. g.OrderBy(r => I(r[1])).Select(r => S(r[4]))]));
            foreach (var ix in Rows(c, $"PRAGMA index_list(\"{q}\")").Where(r => I(r[2]) == 1 && S(r[3]) == "u"))
                table.Uniques.Add([.. Rows(c, $"PRAGMA index_info(\"{S(ix[1]).Replace("\"", "\"\"")}\")").OrderBy(r => I(r[0])).Select(r => S(r[2]))]);
            // Checks aren't in a pragma: they're read from the table's own CREATE TABLE.
            if (t[1] is string sql)
                try
                {
                    var parsed = DdlReader.Read(sql, Dialect.Sqlite, source).Tables.FirstOrDefault();
                    if (parsed != null) table.Checks.AddRange(parsed.Checks);
                }
                catch (FormatException) { }
            schema.Tables.Add(table);
        }
        return schema;
    }

    private static DatabaseSchema SqlServer(DbConnection c, string source)
    {
        var schema = new DatabaseSchema { Dialect = Dialect.SqlServer, Source = source };
        var tables = new Dictionary<int, Table>();
        foreach (var r in Rows(c, """
            SELECT t.object_id, s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 ORDER BY s.name, t.name
            """))
        {
            var t = new Table { Schema = S(r[1]), Name = S(r[2]) };
            tables[I(r[0])] = t;
            schema.Tables.Add(t);
        }
        foreach (var r in Rows(c, """
            SELECT c.object_id, c.name, c.column_id, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, dc.definition
            FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            WHERE t.is_ms_shipped = 0 ORDER BY c.object_id, c.column_id
            """))
        {
            if (!tables.TryGetValue(I(r[0]), out var t)) continue;
            var type = S(r[3]);
            var len = I(r[4]);
            var declared = type switch
            {
                "varchar" or "char" or "varbinary" or "binary" => $"{type}({(len < 0 ? "max" : len)})",
                "nvarchar" or "nchar" => $"{type}({(len < 0 ? "max" : len / 2)})",
                "decimal" or "numeric" => $"{type}({I(r[5])},{I(r[6])})",
                "datetime2" or "time" or "datetimeoffset" => $"{type}({I(r[6])})",
                _ => type,
            };
            var d = r[9] == null ? null : S(r[9]);
            while (d is { Length: > 1 } && d[0] == '(' && d[^1] == ')') d = d[1..^1];
            t.Columns.Add(new Column { Name = S(r[1]), Type = DataType.Parse(declared), Nullable = Convert.ToBoolean(r[7]), Identity = Convert.ToBoolean(r[8]), Default = d, Ordinal = I(r[2]) });
        }
        foreach (var g in Rows(c, """
            SELECT kc.parent_object_id, kc.type, kc.name, col.name, ic.key_ordinal
            FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            ORDER BY kc.parent_object_id, kc.name, ic.key_ordinal
            """).GroupBy(r => (I(r[0]), S(r[1]).Trim(), S(r[2]))))
        {
            if (!tables.TryGetValue(g.Key.Item1, out var t)) continue;
            var cols = g.Select(r => S(r[3])).ToList();
            if (g.Key.Item2 == "PK") t.PrimaryKey.AddRange(cols);
            else t.Uniques.Add(cols);
        }
        foreach (var g in Rows(c, """
            SELECT fk.parent_object_id, fk.name, pc.name, rs.name, rt.name, rc.name, fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY fk.parent_object_id, fk.name, fkc.constraint_column_id
            """).GroupBy(r => (I(r[0]), S(r[1]))))
        {
            if (!tables.TryGetValue(g.Key.Item1, out var t)) continue;
            var first = g.First();
            t.ForeignKeys.Add(new ForeignKey(g.Key.Item2, [.. g.Select(r => S(r[2]))], $"{S(first[3])}.{S(first[4])}", [.. g.Select(r => S(r[5]))]));
        }
        foreach (var r in Rows(c, "SELECT parent_object_id, definition FROM sys.check_constraints"))
            if (tables.TryGetValue(I(r[0]), out var t)) t.Checks.Add(S(r[1]).Trim('(', ')'));
        return schema;
    }

    private static DatabaseSchema Postgres(DbConnection c, string source)
    {
        var schema = new DatabaseSchema { Dialect = Dialect.PostgreSql, Source = source };
        var tables = new Dictionary<long, Table>();
        foreach (var r in Rows(c, """
            SELECT cl.oid::bigint, n.nspname, cl.relname FROM pg_class cl JOIN pg_namespace n ON n.oid = cl.relnamespace
            WHERE cl.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
            ORDER BY n.nspname, cl.relname
            """))
        {
            var t = new Table { Schema = S(r[1]), Name = S(r[2]) };
            tables[Convert.ToInt64(r[0])] = t;
            schema.Tables.Add(t);
        }
        foreach (var r in Rows(c, """
            SELECT a.attrelid::bigint, a.attname, a.attnum, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid), a.attidentity
            FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attrelid, a.attnum
            """))
        {
            if (!tables.TryGetValue(Convert.ToInt64(r[0]), out var t)) continue;
            var d = r[5] == null ? null : S(r[5]);
            var identity = S(r[6]).Length > 0 && S(r[6]) != "\0" || d?.StartsWith("nextval(", StringComparison.Ordinal) == true;
            t.Columns.Add(new Column { Name = S(r[1]), Type = DataType.Parse(S(r[3])), Nullable = !Convert.ToBoolean(r[4]), Default = identity ? null : d, Identity = identity, Ordinal = Convert.ToInt32(r[2]) });
        }
        foreach (var r in Rows(c, "SELECT conrelid::bigint, conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE contype IN ('p', 'u', 'f', 'c') ORDER BY conrelid, oid"))
        {
            if (!tables.TryGetValue(Convert.ToInt64(r[0]), out var t)) continue;
            DdlReader.Element(t, [new DdlReader.Token("CONSTRAINT", false, false), new DdlReader.Token(S(r[1]), true, false), .. DdlReader.Tokens(S(r[2]))]);
        }
        // A foreign key's table is named without its schema when it's on the search path: name it in full.
        foreach (var t in schema.Tables)
            for (var i = 0; i < t.ForeignKeys.Count; i++)
                if (!t.ForeignKeys[i].RefTable.Contains('.') && schema.Table(t.ForeignKeys[i].RefTable) is { } p)
                    t.ForeignKeys[i] = t.ForeignKeys[i] with { RefTable = p.FullName };
        return schema;
    }
}
