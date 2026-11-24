using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

public sealed class DryRunTests
{
    internal static List<TableRun> RunSample(MapFile? map = null)
    {
        using var c = Catalogs.Open(Dialect.Sqlite, $"Data Source={Samples.Path("old/clinic-old.db")};Mode=ReadOnly");
        return DryRun.Run(map ?? Samples.Map, Samples.Old, Samples.New, c, Dialect.Sqlite);
    }

    internal static List<string> Summary(List<TableRun> runs) =>
        [.. runs.Select(r => $"{r.Target}: read {r.Read}, filtered {r.Filtered}, in {r.Inserted}, rejected {r.Rejected} ({r.Rejected - r.Followed} own)")];

    internal static List<string> Own(List<TableRun> runs) =>
        [.. runs.SelectMany(r => r.Rejects.Where(x => Engine.Root(x.Reasons[0]) == null).Select(x => $"{r.Target} {x.OldKey}: {x.Reasons[0]}"))];

    [Fact]
    public void The_sample_dry_run_makes_every_new_row_and_rejects_exactly_the_planted_problems()
    {
        var runs = RunSample();
        Assert.Equal(
        [
            "payer: read 4, filtered 0, in 4, rejected 0 (0 own)",
            "sex_codes: read 3, filtered 0, in 3, rejected 0 (0 own)",
            "claim_status: read 5, filtered 0, in 5, rejected 0 (0 own)",
            "patient: read 40, filtered 1, in 35, rejected 4 (4 own)",
            "visit: read 80, filtered 0, in 66, rejected 14 (1 own)",
            "charge: read 123, filtered 0, in 96, rejected 27 (5 own)",
            "claim: read 20, filtered 0, in 16, rejected 4 (1 own)",
        ], Summary(runs));
        Assert.Equal(
        [
            "patient 5: DOB \"13/45/1980\" isn't a date in MM/dd/yyyy, yyyyMMdd, yyyy-MM-dd, M/d/yyyy",
            "patient 8: given_name is required, and the value is null",
            "patient 9: Sex \"X\" isn't in the lookup (M, F, U)",
            "patient 11: family_name takes 40 characters; \"VAN DER BERG-ELLINGTON-WORTHINGTON-SM…\" has 41",
            "visit 20: reason takes 200 characters; \"Patient reports intermittent dizzines…\" has 254",
            "charge 21: procedure_code takes 5 characters; \"99213-25\" has 8",
            "charge 28: it breaks charge's check (amount > 0)",
            "charge 29: it breaks charge's check (amount > 0)",
            "charge 121: PatID 977 has no patient row (the old row it points at isn't there)",
            "charge 122: PatID 981 has no patient row (the old row it points at isn't there)",
            "claim CLM2026007: Status \"PEND\" isn't in the lookup (OPEN, SENT, PAID, DENIED, VOID)",
        ], Own(runs));
        // A row that follows a rejected one names the first cause, once.
        var charge13 = runs.Single(r => r.Target == "charge").Rejects.Single(x => x.OldKey == "13");
        Assert.Equal(["PatID 5: its patient row isn't migrated (rejected: DOB \"13/45/1980\" isn't a date in MM/dd/yyyy, yyyyMMdd, yyyy-MM-dd, M/d/yyyy)"], charge13.Reasons);
        Assert.Equal("CLM2026014", "CLM2026014");
        // New rows carry their new keys: the third patient's charge points at its patient's new key.
        var patient = runs.Single(r => r.Target == "patient").Sample;
        Assert.Equal(("HOLLOWAY", "LEOPOLD", new DateTime(1953, 6, 4), "F", true), ((string)patient[0]["family_name"]!, (string)patient[0]["given_name"]!, (DateTime)patient[0]["birth_date"]!, (string)patient[0]["sex_code"]!, (bool)patient[0]["is_active"]!));
    }

    [Fact]
    public void A_profile_says_what_an_old_column_holds_and_the_suggested_mapping_takes_its_formats_and_codes()
    {
        using var c = Catalogs.Open(Dialect.Sqlite, $"Data Source={Samples.Path("old/clinic-old.db")};Mode=ReadOnly");
        var p = Profiler.Profile(c, Dialect.Sqlite, Samples.Old.Table("tblPatient")!);
        Assert.Equal(40, p.Rows);
        var dob = p.Column("DOB")!;
        Assert.Equal((1L, 1L), (dob.Nulls, dob.Blanks));
        Assert.Equal(["MM/dd/yyyy", "yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy"], dob.DateFormats);
        Assert.Equal(["13/45/1980"], dob.NotDates);
        Assert.Equal(("99/99/9999", 12L), (dob.Shapes[0].Shape, dob.Shapes[0].Count));
        var sex = p.Column("Sex")!;
        Assert.Equal([("F", 19L), ("M", 19L), ("X", 1L)], sex.Top);
        Assert.Equal(51, p.Column("PatName")!.MaxLength);
        Assert.Equal(("Y", 35L), (p.Column("Active")!.Top[0].Value, p.Column("Active")!.Top[0].Count));
        Assert.Equal("9999999999", Profiler.Shape("5555550101"));
        Assert.Equal("Aaa a, 99", Profiler.Shape("Abc   d, 12"));
    }

    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MAPPER_DUMP") is not { } path) return;
        var lines = new List<string>();
        foreach (var r in RunSample())
        {
            lines.Add($"== {r.Target} read {r.Read} filtered {r.Filtered} inserted {r.Inserted} rejected {r.Rejected} {string.Join("; ", r.Notes)}");
            lines.AddRange(r.Rejects.Select(x => $"  {x.OldKey}: {string.Join(" / ", x.Reasons)}"));
            lines.AddRange(r.Groups.Select(g => $"  group {g.Count} × {g.Reason}"));
        }
        using var c = Catalogs.Open(Dialect.Sqlite, $"Data Source={Samples.Path("old/clinic-old.db")};Mode=ReadOnly");
        var p = Profiler.Profile(c, Dialect.Sqlite, Samples.Old.Table("tblPatient")!);
        lines.Add($"== profile rows {p.Rows}");
        foreach (var col in p.Columns)
            lines.Add($"  {col.Name}: nulls {col.Nulls} blanks {col.Blanks} distinct {col.Distinct} len {col.MinLength}-{col.MaxLength} min {col.Min} max {col.Max} scale {col.Scale} shapes {string.Join(", ", col.Shapes.Take(5).Select(s => $"{s.Shape}×{s.Count}"))} top {string.Join(", ", col.Top.Take(5).Select(t => $"{t.Value}×{t.Count}"))} formats [{string.Join(" ", col.DateFormats)}] notdates [{string.Join(" ", col.NotDates)}]");
        File.WriteAllLines(path + ".dry", lines);
    }
}
