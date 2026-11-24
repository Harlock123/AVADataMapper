using System.Text.Json.Nodes;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

public sealed class TransformTests
{
    private static Column Col(string name, string type, bool nullable = true, string? @default = null) =>
        new() { Name = name, Type = DataType.Parse(type), Nullable = nullable, Default = @default };

    private static string Run(ColumnMap cm, object? value, Column target, TransformContext? ctx = null)
    {
        var o = Transforms.Apply(cm, c => value, target, ctx ?? new TransformContext());
        return o.Rejected != null ? "rejected: " + o.Rejected : o.UseDefault ? "default" : o.Value switch
        {
            null => "null",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
            bool b => b ? "true" : "false",
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            var x => $"{x}",
        };
    }

    [Fact]
    public void The_sample_mappings_column_maps_do_what_they_say()
    {
        var patient = Samples.Map.Table("patient")!;
        var neu = Samples.New.Table("patient")!;
        string P(string column, object? v) => Run(patient.Column(column)!, v, neu.Column(column)!);
        Assert.Equal("HOLLOWAY", P("family_name", "HOLLOWAY, LEOPOLD"));
        Assert.Equal("NOLAN", P("given_name", "  LOCKHART ,  NOLAN  "));
        Assert.Equal("rejected: given_name is required, and the value is null", P("given_name", "CHER"));
        Assert.Equal("rejected: family_name takes 40 characters; \"VAN DER BERG-ELLINGTON-WORTHINGTON-SM…\" has 41", P("family_name", "VAN DER BERG-ELLINGTON-WORTHINGTON-SMYTHE, ARABELLA"));
        foreach (var dob in new[] { "06/04/1953", "19530604", "1953-06-04", "6/4/1953" }) Assert.Equal("1953-06-04 00:00", P("birth_date", dob));
        Assert.Equal("null", P("birth_date", ""));
        Assert.Equal("null", P("birth_date", null));
        Assert.Equal("rejected: DOB \"13/45/1980\" isn't a date in MM/dd/yyyy, yyyyMMdd, yyyy-MM-dd, M/d/yyyy", P("birth_date", "13/45/1980"));
        Assert.Equal("M", P("sex_code", "M"));
        Assert.Equal("U", P("sex_code", null));
        Assert.Equal("rejected: Sex \"X\" isn't in the lookup (M, F, U)", P("sex_code", "X"));
        Assert.Equal("true", P("is_active", "y"));
        Assert.Equal("false", P("is_active", "N"));
        Assert.Equal("MIGRATION", P("created_by", null));
        var claim = Samples.Map.Table("claim")!;
        Assert.Equal("VOID", Run(claim.Column("status")!, "VOID ", Samples.New.Table("claim")!.Column("status")!));
        Assert.Equal("PAID", Run(claim.Column("status")!, "paid", Samples.New.Table("claim")!.Column("status")!));
        Assert.Equal("CLINIC2006", Run(claim.Column("source_system")!, null, Samples.New.Table("claim")!.Column("source_system")!));
        var charge = Samples.Map.Table("charge")!;
        var nc = Samples.New.Table("charge")!;
        Assert.Equal("rejected: procedure_code takes 5 characters; \"99213-25\" has 8", Run(charge.Column("procedure_code")!, "99213-25", nc.Column("procedure_code")!));
        Assert.Equal("0", Run(charge.Column("copay")!, null, nc.Column("copay")!));
        Assert.Equal("rejected: amount keeps 2 decimal places; 12.345 would be rounded", Run(charge.Column("amount")!, 12.345m, nc.Column("amount")!));
        var ctx = new TransformContext();
        ctx.Keys["patient"] = new() { ["3"] = 103 };
        Assert.Equal("103", Run(charge.Column("patient_id")!, 3L, nc.Column("patient_id")!, ctx));
        Assert.Equal("rejected: PatID 977 has no patient row (the old row it points at isn't there)", Run(charge.Column("patient_id")!, 977L, nc.Column("patient_id")!, ctx));
    }

    [Fact]
    public void Values_are_fitted_to_the_new_columns_type()
    {
        Assert.Equal("default", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, null, Col("c", "numeric(5,2)", false, "0")));
        Assert.Equal("rejected: c takes a whole number; 1.5 isn't one", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, 1.5m, Col("c", "int")));
        Assert.Equal("rejected: c (smallint) can't hold 40000", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, 40000, Col("c", "smallint")));
        Assert.Equal("rejected: c (numeric(5,2)) can't hold 1234.5", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, 1234.5m, Col("c", "numeric(5,2)")));
        Assert.Equal("2026-03-04 09:30", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, "2026-03-04 09:30:00", Col("c", "timestamp")));
        Assert.Equal("rejected: c takes a date; 2026-03-04 09:30 has a time (convert it as date)", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, "2026-03-04 09:30:00", Col("c", "date")));
        Assert.Equal("true", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x" }, 1L, Col("c", "boolean")));
        Assert.Equal("Ada Lovelace", Run(new ColumnMap { Kind = ColumnKind.Direct, From = "x", Case = "title", Trim = true }, "  ADA LOVELACE ", Col("c", "text")));
        Assert.Equal("a-b", Transforms.Apply(new ColumnMap { Kind = ColumnKind.Merge, Froms = ["a", "z", "b"], Separator = "-" }, c => c == "z" ? null : c, Col("c", "text"), new()).Value);
        Assert.Equal("LAST", Run(new ColumnMap { Kind = ColumnKind.Split, From = "x", Separator = " ", Part = -1 }, "FIRST MIDDLE LAST", Col("c", "text")));
        Assert.Equal("null", Run(new ColumnMap { Kind = ColumnKind.Convert, From = "x", As = "date", Unknown = "null" }, "someday", Col("c", "date")));
        Assert.Equal("default", Run(new ColumnMap { Kind = ColumnKind.Lookup, From = "x", Map = new() { ["A"] = "a" }, Unknown = "default" }, "Q", Col("c", "text", false, "'?'")));
    }

    [Theory]
    [InlineData("Deleted = 0", "true")]
    [InlineData("Deleted <> 0 or Active = 'n'", "true")]
    [InlineData("upper(trim(Name)) = 'CHER'", "true")]
    [InlineData("Status in ('OPEN', 'SENT')", "false")]
    [InlineData("Status not in ('OPEN', 'SENT')", "true")]
    [InlineData("Name like 'CH%'", "true")]
    [InlineData("Phone is null", "true")]
    [InlineData("Amount * 2 + 1", "51.00")]
    [InlineData("coalesce(Phone, 'none')", "none")]
    [InlineData("iif(Amount > 20, 'big', 'small')", "big")]
    [InlineData("substr(Name, 2, 2)", "HE")]
    [InlineData("left(Name, 2) || '-' || right(Name, 1)", "CH-R")]
    [InlineData("year(date(Born, 'MM/dd/yyyy'))", "1984")]
    [InlineData("round(Amount / 3, 2)", "8.33")]
    public void Expressions_evaluate_on_a_row(string expression, string expected)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Deleted"] = 0L, ["Active"] = "N", ["Name"] = " cher ", ["Status"] = "PAID", ["Phone"] = null, ["Amount"] = 25.00m, ["Born"] = "05/25/1984" };
        row["Name"] = "CHER";
        var v = Expression.Parse(expression).Evaluate(c => row[c]);
        Assert.Equal(expected, v switch { bool b => b ? "true" : "false", decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture), var x => $"{x}" });
    }

    [Fact]
    public void Expressions_that_are_wrong_say_so_before_anything_runs()
    {
        Assert.Contains("no function frob()", Assert.Throws<FormatException>(() => Expression.Parse("frob(x)")).Message);
        Assert.Contains("isn't closed", Assert.Throws<FormatException>(() => Expression.Parse("x = 'open")).Message);
        Assert.Contains(") expected", Assert.Throws<FormatException>(() => Expression.Parse("upper(x")).Message);
        Assert.Equal("Nope isn't a column of the old table.", Expression.Parse("Nope = 1 and Deleted = 0").Check(["Deleted"]));
        Assert.Equal(["Deleted", "Active"], Expression.Parse("Deleted = 0 and upper(Active) = 'Y'").Columns);
    }

    [Fact]
    public void The_sample_mapping_is_checked_against_both_schemas_before_any_row_is_read()
    {
        Assert.Equal(
        [
            "Warning M6 patient.family_name: tblPatient.PatName holds up to 70 characters; family_name takes 40.",
            "Warning M6 patient.given_name: tblPatient.PatName holds up to 70 characters; given_name takes 30.",
            "Warning M6 visit.reason: tblVisit.Reason holds up to 255 characters; reason takes 200.",
            "Warning M7 charge.amount: tblCharge.Amt is money; amount is numeric(9,2): 4 decimal places into 2.",
            "Warning M7 charge.copay: tblCharge.Copay is money; copay is numeric(9,2): 4 decimal places into 2.",
            "Warning M10 charge.patient_id: tblCharge.PatID has no foreign key to tblPatient: it may hold keys of rows that are gone.",
            "Warning M6 charge.procedure_code: tblCharge.ProcCode holds up to 10 characters; procedure_code takes 5.",
            "Warning M7 claim.amount: tblClaim.Amt is money; amount is numeric(9,2): 4 decimal places into 2.",
        ], MapChecks.Check(Samples.Map, Samples.Old, Samples.New).Select(f => $"{f.Severity} {f}"));
        // The PostgreSQL target reads the same, so it checks the same.
        Assert.Equal(8, MapChecks.Check(Samples.Map, SchemaFiles.Load(Samples.Path("old/clinic-old.tsql.sql")), SchemaFiles.Load(Samples.Path("new/clinic-new.pg.sql"))).Count);
    }

    [Fact]
    public void A_broken_mapping_says_what_cant_run()
    {
        var map = MapFile.Read(Samples.Map.ToJson());
        map.Table("patient")!.Where = "Gone = 0";
        map.Table("patient")!.Column("phone")!.From = "Telephone";
        map.Table("patient")!.Column("birth_date")!.Kind = ColumnKind.Direct;
        map.Table("patient")!.Columns.RemoveAll(c => c.Target == "created_by");
        map.Table("patient")!.Column("sex_code")!.Map!["X"] = "Z";
        map.Table("visit")!.Columns.Add(new ColumnMap { Target = "nonsense", Kind = ColumnKind.Constant, Value = 1 });
        map.Table("claim")!.Column("patient_id")!.Table = "sex_codes";
        map.Table("claim")!.Column("source_system")!.Kind = ColumnKind.Expression;
        map.Table("claim")!.Column("source_system")!.Expression = "upper(Sistema)";
        var found = MapChecks.Check(map, Samples.Old, Samples.New).Where(f => f.Severity == Severity.Error).Select(f => $"{f.Rule.Id} {f.About}").ToList();
        Assert.Equal(["M12 patient", "M5 patient.birth_date", "M4 patient.created_by", "M2 patient.phone", "M11 patient.sex_code", "M3 visit.nonsense", "M9 claim.patient_id", "M12 claim.source_system"], found);
        Assert.Contains("Convert it (as date).", MapChecks.Check(map, Samples.Old, Samples.New).Single(f => f.Rule.Id == "M5").Fix);
    }

    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MAPPER_DUMP") is not { } path) return;
        File.WriteAllLines(path + ".checks", MapChecks.Check(Samples.Map, Samples.Old, Samples.New).Select(f => $"{f.Severity} {f}  | {f.Fix}"));
    }
}
