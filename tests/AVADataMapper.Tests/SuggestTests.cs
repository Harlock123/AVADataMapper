using AVADataMapper.Core.Mapping;

namespace AVADataMapper.Tests;

public sealed class SuggestTests
{
    internal static Evidence Lens => Evidence.Load(Samples.Path("old/clinic-old.lens"));

    /// <summary>A column map as one line: target kind from (certainty) and its options.</summary>
    internal static string Line(TableMap t, ColumnMap c) =>
        $"{t.Target}.{c.Target} {c.Kind.ToString().ToLowerInvariant()}{(c.From != null ? " " + c.From : "")}{(c.Table != null ? " → " + c.Table : "")}{(c.As != null ? " as " + c.As : "")}" +
        $"{(c.Part != null ? $" part {c.Part}" : "")}{(c.Formats != null ? $" [{string.Join(" ", c.Formats)}]" : "")}{(c.Map != null ? $" {{{string.Join(",", c.Map.Keys)}}}" : "")} ({c.Certainty.ToString().ToLowerInvariant()})";

    [Fact]
    public void An_empty_mapping_is_suggested_from_names_types_keys_and_the_old_codes_rules()
    {
        var map = new MapFile();
        var r = Suggester.Suggest(map, Samples.Old, Samples.New, Lens);
        Assert.Equal((7, 32), (r.Tables, r.Columns));
        Assert.Equal(["visit ← tblVisit (sure)", "charge ← tblCharge (sure)", "payer ← tblPayer (sure)", "patient ← tblPatient (sure)", "claim ← tblClaim (sure)", "sex_codes ← rows given (likely)", "claim_status ← rows given (likely)"],
            map.Tables.Select(t => $"{t.Target} ← {t.From ?? "rows given"} ({t.Certainty.ToString().ToLowerInvariant()})"));
        var lines = map.Tables.SelectMany(t => t.Columns.Select(c => Line(t, c))).ToList();
        foreach (var expected in new[]
        {
            "visit.patient_id key PatID → patient (sure)",
            "visit.visit_at direct VisitDt (likely)",
            "charge.patient_id key PatID → patient (likely)",
            "charge.visit_id key VisitID → visit (sure)",
            "patient.family_name split PatName part 1 (likely)",
            "patient.given_name split PatName part 2 (likely)",
            "patient.birth_date convert DOB as date [MM/dd/yyyy] (likely)",
            "patient.sex_code lookup Sex → sex_codes {M,F,U} (likely)",
            "patient.payer_id key PayerCode → payer (sure)",
            "patient.is_active convert Active as bool {Y,N} (likely)",
            "patient.created_by direct EnteredBy (likely)",
            "claim.status lookup Status → claim_status {OPEN,SENT,PAID,DENIED,VOID} (likely)",
            "payer.copay_percent direct CopayPct (sure)",
            "claim.claim_number direct ClaimNo (sure)",
        })
            Assert.Contains(expected, lines);
        Assert.Contains("\"Enter the name as LAST, FIRST.\"", map.Table("patient")!.Column("family_name")!.Reason);
        Assert.Contains("shorter (255 → 200)", map.Table("visit")!.Column("reason")!.Reason);
        // What's left for a person: one required column nothing fills, and three old things nothing reads.
        var coverage = Coverage.Of(map, Samples.Old, Samples.New, Lens).Where(i => i.Section != Coverage.Suggested).ToList();
        Assert.Equal(
        [
            "required, not filled | claim.source_system",
            "old, not read | tblPatient.Fax | No code in the old system reads or writes it.",
            "old, not read | tblPatient.Deleted | Written by PatientScreen.DeletePatient; read by PatientScreen.FindPatients.",
            "old, not read | FaxQueue | FaxQueue: no code reads or writes this table",
        ], coverage.Select(i => $"{i.Section} | {i.About}" + (i.Evidence != null ? $" | {i.Evidence}" : "")));
    }

    [Fact]
    public void Suggestions_never_change_what_a_person_mapped_and_without_a_scan_are_guesses()
    {
        var map = MapFile.Read("""{ "format": "avadatamapper.map", "tables": [ { "target": "patient", "from": "tblPatient", "columns": [ { "target": "family_name", "kind": "constant", "value": "X" } ] } ] }""");
        Suggester.Suggest(map, Samples.Old, Samples.New);
        var patient = map.Table("patient")!;
        Assert.Equal(Certainty.Confirmed, patient.Certainty);
        Assert.Equal(ColumnKind.Constant, patient.Column("family_name")!.Kind);
        Assert.Equal("patient.given_name split PatName part 2 (guessed)", Line(patient, patient.Column("given_name")!));
        Assert.Equal("patient.birth_date convert DOB as date (likely)", Line(patient, patient.Column("birth_date")!));
        Assert.Null(patient.Column("sex_code")!.Map);
        // The sample's own mapping, all confirmed, leaves nothing to suggest but what it chose not to fill.
        var done = Samples.Map;
        var before = done.ToJson();
        var r = Suggester.Suggest(done, Samples.Old, Samples.New, Lens);
        Assert.Equal((0, 0), (r.Tables, r.Columns));
        Assert.Equal(before, done.ToJson());
        Assert.Equal(["old, dropped | tblPatient.Fax", "old, dropped | FaxQueue"], Coverage.Of(done, Samples.Old, Samples.New, Lens).Select(i => $"{i.Section} | {i.About}"));
    }

    [Theory]
    [InlineData("PatID", "patient id")]
    [InlineData("tblPatient", "patient")]
    [InlineData("visit", "visit")]
    [InlineData("DOB", "birth @when")]
    [InlineData("VisitDt", "visit @when")]
    [InlineData("ClaimNo", "claim number")]
    [InlineData("is_active", "active")]
    [InlineData("sex_codes", "sex code")]
    [InlineData("claim_status", "claim status")]
    [InlineData("EnteredBy", "@made by")]
    public void Names_read_as_words(string name, string words) => Assert.Equal(words, string.Join(" ", Names.Words(name)));

    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("MAPPER_DUMP") is not { } path) return;
        var map = new MapFile();
        var r = Suggester.Suggest(map, Samples.Old, Samples.New, Lens);
        var lines = new List<string> { $"tables {r.Tables} columns {r.Columns}" };
        lines.AddRange(r.Notes);
        foreach (var t in map.Tables)
        {
            lines.Add($"== {t.Target} ← {t.From ?? "rows given"} ({t.Certainty}): {t.Reason}");
            lines.AddRange(t.Columns.Select(c => Line(t, c) + "  | " + c.Reason));
        }
        lines.Add("== coverage");
        lines.AddRange(Coverage.Of(map, Samples.Old, Samples.New, Lens).Select(i => $"{i.Section} | {i.About} | {i.Text} | {i.Evidence}"));
        File.WriteAllLines(path, lines);
    }
}
