using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

public sealed class MigrateTests
{
    internal static long Count(System.Data.Common.DbConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    internal static object? Scalar(System.Data.Common.DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void A_migration_puts_in_what_the_dry_run_said_reruns_skip_and_rollback_takes_out_only_its_own()
    {
        var root = Samples.Copy();
        using var old = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "old", "clinic-old.db")};Mode=ReadOnly");
        using var neu = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "new", "clinic-new.db")}");
        var result = Migrator.Run(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, neu, Dialect.Sqlite);
        Assert.Equal("done", result.Status);
        Assert.Equal(DryRunTests.Summary(DryRunTests.RunSample()), DryRunTests.Summary(result.Tables));
        Assert.Equal(35, Count(neu, "patient"));
        Assert.Equal(96, Count(neu, "charge"));
        Assert.Equal(225, Count(neu, Journal.Keys));
        // Rows point at their parents' new keys: old charge 6's new row points at the new row of old charge 6's patient.
        var oldName = (string)Scalar(old, "SELECT p.PatName FROM tblCharge c JOIN tblPatient p ON p.PatID = c.PatID WHERE c.ChgID = 6")!;
        Assert.Equal(oldName.Split(',')[0].Trim(), Scalar(neu, "SELECT p.family_name FROM charge c JOIN patient p ON p.patient_id = c.patient_id WHERE c.charge_id = (SELECT CAST(new_key AS INTEGER) FROM avadm_keys WHERE new_table = 'charge' AND old_key = '6')"));
        Assert.Equal("1953-06-04", Scalar(neu, "SELECT birth_date FROM patient WHERE family_name = 'HOLLOWAY'"));
        Assert.Equal(1L, Scalar(neu, "SELECT is_active FROM patient WHERE family_name = 'HOLLOWAY'"));

        // Again: everything's there, nothing goes in twice.
        var again = Migrator.Run(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, neu, Dialect.Sqlite);
        Assert.Equal((0L, 225L), (again.Inserted, again.Skipped));
        Assert.Equal(35, Count(neu, "patient"));
        var runs = Journal.List(neu, Dialect.Sqlite, Samples.Map.Id);
        Assert.Equal([("done", 225L), ("done", 0L)], runs.Select(r => (r.Status, r.Rows)));

        // A row it didn't write: it won't touch the table, and rollback leaves that row alone.
        using (var cmd = neu.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO claim_status (code, meaning) VALUES ('HELD', 'Held by hand')";
            cmd.ExecuteNonQuery();
        }
        var map2 = MapFile.Read(Samples.Map.ToJson());
        map2.Id = "another";
        Assert.Contains("payer has 4 rows this mapping didn't put there", Assert.Throws<InvalidOperationException>(() => Migrator.Run(map2, Samples.Old, Samples.New, old, Dialect.Sqlite, neu, Dialect.Sqlite)).Message);
        Assert.Equal(225, Migrator.Rollback(Samples.New, neu, Dialect.Sqlite, runs[0].RunId));
        Assert.Equal(0, Count(neu, "patient"));
        Assert.Equal(1, Count(neu, "claim_status"));
        Assert.Equal("rolled back", Journal.List(neu, Dialect.Sqlite).First(r => r.RunId == runs[0].RunId).Status);
    }
}

public sealed class HandoffTests
{
    [Fact]
    public void The_script_run_on_an_empty_database_gives_what_the_migration_gives()
    {
        var root = Samples.Copy();
        using var old = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "old", "clinic-old.db")};Mode=ReadOnly");
        var script = new StringWriter();
        var runs = Core.Handoff.ScriptWriter.Write(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, Dialect.Sqlite, script);
        var text = script.ToString();
        Assert.Contains("-- 225 rows go in; 49 are rejected (listed at the end) and 1 filtered out.", text);
        Assert.Contains("--   charge 21: procedure_code takes 5 characters; \"99213-25\" has 8", text);
        Assert.Contains("INSERT INTO \"patient\" (\"patient_id\", \"family_name\", \"given_name\", \"birth_date\", \"sex_code\", \"payer_id\", \"phone\", \"is_active\", \"created_by\") VALUES (1, 'HOLLOWAY', 'LEOPOLD', '1953-06-04', 'F', 2, '5555550101', 1, 'IMPORT2011');", text);
        using var neu = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "new", "clinic-new.db")}");
        using (var cmd = neu.CreateCommand()) { cmd.CommandText = text; cmd.ExecuteNonQuery(); }
        Assert.Equal(35, MigrateTests.Count(neu, "patient"));
        Assert.Equal(96, MigrateTests.Count(neu, "charge"));
        Assert.Equal(runs.Sum(r => r.Inserted), new[] { "payer", "sex_codes", "claim_status", "patient", "visit", "charge", "claim" }.Sum(t => MigrateTests.Count(neu, t)));
        // The PostgreSQL and SQL Server scripts write their keys out their own ways.
        var pg = new StringWriter();
        Core.Handoff.ScriptWriter.Write(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, Dialect.PostgreSql, pg);
        Assert.Contains("OVERRIDING SYSTEM VALUE VALUES (1, 'HOLLOWAY', 'LEOPOLD', '1953-06-04', 'F', 2, '5555550101', TRUE, 'IMPORT2011');", pg.ToString());
        Assert.Contains("SELECT setval(pg_get_serial_sequence('\"patient\"', 'patient_id'), 35);", pg.ToString());
        var ms = new StringWriter();
        Core.Handoff.ScriptWriter.Write(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, Dialect.SqlServer, ms);
        Assert.Contains("SET IDENTITY_INSERT [patient] ON;", ms.ToString());
        Assert.Contains("N'HOLLOWAY'", ms.ToString());
    }

    [Fact]
    public void The_parity_suite_expects_the_migrated_rows_by_their_old_keys_and_AVAParityCheck_passes_it()
    {
        var root = Samples.Copy();
        var map = Samples.Map;
        var mapPath = Path.Combine(root, "clinic.dmap");
        using (var old = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "old", "clinic-old.db")};Mode=ReadOnly"))
        using (var neu = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "new", "clinic-new.db")};Pooling=False"))
        {
            Migrator.Run(map, Samples.Old, Samples.New, old, Dialect.Sqlite, neu, Dialect.Sqlite);
            Core.Handoff.ParityPlan.Write(map, mapPath, Samples.Old, Samples.New, old, Dialect.Sqlite, Dialect.Sqlite, Path.Combine(root, "proof", "clinic.parity"));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var suite = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "proof", "clinic.parity")))!;
        Assert.Equal("avaparitycheck.suite", (string?)suite["format"]);
        var checks = suite["checks"]!.AsArray();
        Assert.Equal(7, checks.Count);
        var patient = checks.Single(c => (string?)c!["name"] == "table patient")!;
        Assert.Equal("../new/clinic-new.db", (string?)patient["new"]!["path"]);
        Assert.Equal("clinic-expected/patient.csv", (string?)patient["old"]!["path"]);
        var csv = File.ReadAllLines(Path.Combine(root, "proof", "clinic-expected", "patient.csv"));
        Assert.Equal("old_key,family_name,given_name,birth_date,sex_code,payer_id,phone,is_active,created_by", csv[0]);
        Assert.Equal("1,HOLLOWAY,LEOPOLD,1953-06-04,F,MDCR,5555550101,true,IMPORT2011", csv[1]);
        Assert.Equal(36, csv.Length);
        // A charge names its patient and visit by their old keys (2 and 4), not the numbers the new database gave them.
        Assert.Equal("6,2,4,90837,180,0,2026-04-16 11:26:00", File.ReadAllLines(Path.Combine(root, "proof", "clinic-expected", "charge.csv"))[6]);
        // AVAParityCheck runs it (MAPPER_PARITYCHECK: its repository): every check passes.
        if (Environment.GetEnvironmentVariable("MAPPER_PARITYCHECK") is not { Length: > 0 } repo) return;
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", ["run", "--project", Path.Combine(repo, "AVAParityCheck"), "--", "--run", Path.Combine(root, "proof", "clinic.parity")]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var said = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, said);
    }
}

/// <summary>Migrates a copy of the samples and writes its parity suite (MAPPER_PROOF: the copy's folder), to run AVAParityCheck on by hand.</summary>
public sealed class ProofDump
{
    [Fact]
    public void Make()
    {
        if (Environment.GetEnvironmentVariable("MAPPER_PROOF") is not { Length: > 0 } root) return;
        using var old = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "old", "clinic-old.db")};Mode=ReadOnly");
        using var neu = Catalogs.Open(Dialect.Sqlite, $"Data Source={Path.Combine(root, "new", "clinic-new.db")};Pooling=False");
        Migrator.Run(Samples.Map, Samples.Old, Samples.New, old, Dialect.Sqlite, neu, Dialect.Sqlite);
        Core.Handoff.ParityPlan.Write(Samples.Map, Path.Combine(root, "clinic.dmap"), Samples.Old, Samples.New, old, Dialect.Sqlite, Dialect.Sqlite, Path.Combine(root, "proof", "clinic.parity"));
    }
}
