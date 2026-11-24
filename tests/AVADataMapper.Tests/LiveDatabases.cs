using System.Data.Common;
using AVADataMapper.Core.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace AVADataMapper.Tests;

/// <summary>
/// Throwaway databases on real servers, for the opt-in tests: MAPPER_TEST_MSSQL and MAPPER_TEST_PG are connection strings
/// to a server (any database); each test makes a database of its own, named mapper_test_…, and drops it after.
/// </summary>
internal sealed class LiveDatabase : IDisposable
{
    private readonly string _server;

    public Dialect Dialect { get; }
    public string Name { get; } = "mapper_test_" + Guid.NewGuid().ToString("N")[..10];
    public string Connection { get; }

    public static string? Server(Dialect d) => Environment.GetEnvironmentVariable(d == Dialect.SqlServer ? "MAPPER_TEST_MSSQL" : "MAPPER_TEST_PG") is { Length: > 0 } s ? s : null;

    public LiveDatabase(Dialect dialect)
    {
        Dialect = dialect;
        _server = Server(dialect) ?? throw new InvalidOperationException("No server.");
        using (var c = Catalogs.Open(dialect, _server)) Exec(c, $"CREATE DATABASE {Name}");
        Connection = dialect == Dialect.SqlServer
            ? new SqlConnectionStringBuilder(_server) { InitialCatalog = Name }.ToString()
            : new NpgsqlConnectionStringBuilder(_server) { Database = Name }.ToString();
    }

    /// <summary>Runs a script: T-SQL split at GO lines, PostgreSQL as one batch.</summary>
    public void Run(string script)
    {
        using var c = Catalogs.Open(Dialect, Connection);
        var batches = Dialect == Dialect.SqlServer
            ? System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : [script];
        foreach (var b in batches.Where(b => b.Trim().Length > 0)) Exec(c, b);
    }

    public DatabaseSchema Schema()
    {
        using var c = Catalogs.Open(Dialect, Connection);
        return Catalogs.Read(c, Dialect, Catalogs.Describe(Dialect, Connection));
    }

    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Dialect == Dialect.SqlServer) SqlConnection.ClearAllPools();
        else NpgsqlConnection.ClearAllPools();
        try
        {
            using var c = Catalogs.Open(Dialect, _server);
            Exec(c, Dialect == Dialect.SqlServer ? $"ALTER DATABASE {Name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Name}" : $"DROP DATABASE {Name} WITH (FORCE)");
        }
        catch (DbException) { }
    }
}

/// <summary>The catalogs of real SQL Server and PostgreSQL servers read the same as the samples' DDL (opt-in).</summary>
public sealed class LiveCatalogTests
{
    [Fact]
    public void SQL_Servers_catalog_reads_as_the_tsql_ddl_does()
    {
        if (LiveDatabase.Server(Dialect.SqlServer) == null) return;
        using var db = new LiveDatabase(Dialect.SqlServer);
        db.Run(File.ReadAllText(Samples.Path("old/clinic-old.tsql.sql")));
        var live = db.Schema();
        var ddl = SchemaFiles.Load(Samples.Path("old/clinic-old.tsql.sql"));
        Assert.Equal(Norm(ddl), Norm(live));
        Assert.Equal("'Y'", live.Table("tblPatient")!.Column("Active")!.Default);
        Assert.StartsWith("localhost,11453/mapper_test_", live.Source);
    }

    [Fact]
    public void PostgreSQLs_catalog_reads_as_the_postgres_ddl_does()
    {
        if (LiveDatabase.Server(Dialect.PostgreSql) == null) return;
        using var db = new LiveDatabase(Dialect.PostgreSql);
        db.Run(File.ReadAllText(Samples.Path("new/clinic-new.pg.sql")));
        var live = db.Schema();
        var ddl = SchemaFiles.Load(Samples.Path("new/clinic-new.pg.sql"));
        Assert.Equal(Norm(ddl), Norm(live));
    }

    /// <summary>A schema's shape, tables in name order, checks without the server's extra parentheses and casts.</summary>
    private static List<string> Norm(DatabaseSchema s) =>
        [.. SchemaTests.Shape(s).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Replace("(", "").Replace(")", ""), @"::\w+", "")).Order(StringComparer.Ordinal)];
}

/// <summary>Dry runs reading the old rows from a real SQL Server (opt-in): the same as from the SQLite copy.</summary>
public sealed class LiveDryRunTests
{
    [Fact]
    public void A_dry_run_reading_SQL_Server_makes_the_same_rows_as_one_reading_SQLite()
    {
        if (LiveDatabase.Server(Dialect.SqlServer) == null) return;
        using var db = new LiveDatabase(Dialect.SqlServer);
        db.Run(File.ReadAllText(Samples.Path("old/clinic-old.tsql.sql")));
        db.Run(File.ReadAllText(Samples.Path("old/clinic-old.data.sql")));
        var old = db.Schema();
        using var c = Catalogs.Open(Dialect.SqlServer, db.Connection);
        var runs = Core.Data.DryRun.Run(Samples.Map, old, Samples.New, c, Dialect.SqlServer);
        var lite = DryRunTests.RunSample();
        Assert.Equal(DryRunTests.Summary(lite), DryRunTests.Summary(runs));
        Assert.Equal(DryRunTests.Own(lite), DryRunTests.Own(runs));
        var p = Core.Data.Profiler.Profile(c, Dialect.SqlServer, old.Table("tblPatient")!);
        Assert.Equal(["MM/dd/yyyy", "yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy"], p.Column("DOB")!.DateFormats);
    }
}
