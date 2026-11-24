using Avalonia.Controls;
using Avalonia.VisualTree;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

public sealed class WindowTests
{
    [Fact]
    public void A_mapping_opens_with_both_schemas_and_shows_each_table_and_column() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow(Samples.Path("clinic.dmap"));
        w.Show();
        HeadlessApp.Settle();
        Assert.Equal(6, w.Old!.Tables.Count);
        Assert.Equal(7, w.New!.Tables.Count);
        Assert.StartsWith("Opened clinic.dmap: 7 tables mapped.", w.txtStatusBar.Text);
        Assert.Equal(7, w.TargetTables.ItemCount);
        w.ShowTable("patient");
        Assert.Equal(9, w.Rows.Count);
        Assert.Equal("tblPatient", w.SourceBox.SelectedItem);
        Assert.Equal("Deleted = 0", w.WhereBox.Text);
        var text = string.Join("\n", w.ColumnPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("PatName split at ',', part 1; trimmed", text);
        Assert.Contains("DOB as date (MM/dd/yyyy, yyyyMMdd, yyyy-MM-dd, M/d/yyyy); blank is null, else reject", text);
        Assert.Contains("the new key of payer row whose old key is PayerCode", text);
        // FaxQueue isn't read by anything; tblPatient is.
        var old = w.OldTree.Items.OfType<TreeViewItem>().ToList();
        Assert.Equal(6, old.Count);
        w.Close();
    });

    [Fact]
    public void Schemas_open_columns_are_mapped_by_hand_undone_and_the_mapping_saved() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow();
        w.Show();
        w.OpenSchema(true, Path.Combine(root, "old", "clinic-old.tsql.sql"));
        Assert.Equal("The old schema: 6 tables from clinic-old.tsql.sql (SqlServer).", w.txtStatusBar.Text);
        w.OpenSchema(false, Path.Combine(root, "new", "clinic-new.pg.sql"));
        Assert.Equal(Dialect.PostgreSql, w.Map.Target.Dialect);
        w.SetSource("visit", "tblVisit");
        Assert.Equal(ColumnKind.Generated, w.Map.Table("visit")!.Column("visit_id")!.Kind);
        w.ShowTable("visit", "patient_id");
        w.SetColumn("visit", "patient_id", ColumnKind.Key, "PatID");
        var key = w.Map.Table("visit")!.Column("patient_id")!;
        Assert.Equal(("PatID", "patient"), (key.From, key.Table));
        w.SetColumn("visit", "reason", ColumnKind.Direct, "Reason");
        Assert.Equal(["visit_id", "patient_id", "reason"], w.Map.Table("visit")!.Columns.Select(c => c.Target));
        w.Undo();
        Assert.Null(w.Map.Table("visit")!.Column("reason"));
        w.Redo();
        Assert.NotNull(w.Map.Table("visit")!.Column("reason"));
        // The JSON editor replaces a column's mapping.
        w.ShowTable("visit", "clinician");
        w.ColumnJson.Text = """{ "target": "x", "kind": "direct", "from": "Clinician", "trim": true, "case": "title" }""";
        w.ApplyColumnJson();
        Assert.Equal(("Clinician", true, "title"), (w.Map.Table("visit")!.Column("clinician")!.From, w.Map.Table("visit")!.Column("clinician")!.Trim, w.Map.Table("visit")!.Column("clinician")!.Case));
        w.ColumnJson.Text = "{ nope";
        w.ApplyColumnJson();
        Assert.StartsWith("That isn't a column mapping", w.txtStatusBar.Text);

        var saved = Path.Combine(root, "maps", "mine.dmap");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        w.SaveMap(saved);
        Assert.False(w.Dirty);
        var map = MapFile.Load(saved);
        Assert.Equal("../old/clinic-old.tsql.sql", map.Source.File);
        Assert.Equal("../new/clinic-new.pg.sql", map.Target.File);
        w.Close();

        var again = new MainWindow(saved);
        again.Show();
        Assert.Equal(6, again.Old!.Tables.Count);
        Assert.Equal(Dialect.PostgreSql, again.New!.Dialect);
        again.Close();
    });

    [Fact]
    public void A_live_database_is_read_through_a_saved_connection_and_never_named_in_the_mapping() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow();
        w.Show();
        var db = Path.Combine(root, "old", "clinic-old.db");
        w.ConnectSchema(true, Dialect.Sqlite, $"Data Source={db}");
        Assert.Equal(6, w.Old!.Tables.Count);
        Assert.Null(w.Map.Source.File);
        Assert.Equal("clinic-old.db", w.Map.Source.Described);
        Assert.Equal((Dialect.Sqlite, $"Data Source={db}"), w.Connections.Get("old"));
        var saved = Path.Combine(root, "live.dmap");
        w.SaveMap(saved);
        Assert.DoesNotContain("Data Source", File.ReadAllText(saved));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(w.Connections.Path));
        w.Close();
        var again = new MainWindow(saved);
        again.Show();
        Assert.Equal(6, again.Old!.Tables.Count);
        again.Close();
    });
}

public sealed class SuggestWindowTests
{
    [Fact]
    public void Suggestions_fill_an_empty_mapping_and_are_confirmed_dropped_and_undone_from_the_window() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow();
        w.Show();
        try
        {
        w.OpenSchema(true, Path.Combine(root, "old", "clinic-old.db"));
        w.OpenSchema(false, Path.Combine(root, "new", "clinic-new.pg.sql"));
        w.OpenEvidence(Path.Combine(root, "old", "clinic-old.lens"));
        Assert.StartsWith("The old system's code (clinic-old.lens) is evidence now", w.txtStatusBar.Text);
        Assert.Contains(w.CoverageItems, i => i.Section == Core.Mapping.Coverage.Unmapped);

        var r = w.Suggest()!;
        Assert.Equal((7, 32), (r.Tables, r.Columns));
        Assert.StartsWith("Suggested 7 tables and 32 columns", w.txtStatusBar.Text);
        Assert.Contains(w.CoverageItems, i => i.Section == Core.Mapping.Coverage.Required && i.About == "claim.source_system");
        w.ShowTable("patient", "family_name");
        Assert.Contains("rule: \"Enter the name as LAST, FIRST.\"", w.EvidenceText.Text);

        // Confirming the sure ones leaves the likely ones (amber) to check.
        var sure = w.Confirm(sureOnly: true);
        Assert.True(sure > 20);
        Assert.Equal(Certainty.Likely, w.Map.Table("patient")!.Column("birth_date")!.Certainty);
        Assert.Equal(1, w.Confirm("patient", "birth_date"));
        Assert.Equal(Certainty.Confirmed, w.Map.Table("patient")!.Column("birth_date")!.Certainty);

        w.Drop("tblPatient.Fax", "Dropped: nobody faxes now.");
        Assert.Contains(w.CoverageItems, i => i.Section == Core.Mapping.Coverage.Dropped && i.About == "tblPatient.Fax");
        var text = string.Join("\n", w.CoveragePanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Old code: FaxQueue: no code reads or writes this table", text);
        Assert.Contains("Dropped: nobody faxes now.", text);

        // The suggestions were one step: undone, they're all gone.
        w.Undo(); w.Undo(); w.Undo(); w.Undo();
        Assert.Empty(w.Map.Tables);
        var saved = Path.Combine(root, "suggested.dmap");
        w.Redo(); w.Redo();
        w.SaveMap(saved);
        Assert.Equal("old/clinic-old.lens", MapFile.Load(saved).Evidence);
        }
        finally { w.Close(); }
    });
}

public sealed class ChecksWindowTests
{
    [Fact]
    public void The_checks_tab_lists_findings_rows_show_theirs_and_a_value_can_be_tried() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow(Samples.Path("clinic.dmap"));
        w.Show();
        try
        {
            Assert.Equal(8, w.Findings.Count);
            var text = string.Join("\n", w.ChecksPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("0 errors (the migration can't run as mapped) and 8 warnings", text);
            w.ShowTable("charge", "procedure_code");
            var rows = string.Join("\n", w.ColumnPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("M6 tblCharge.ProcCode holds up to 10 characters; procedure_code takes 5.", rows);
            w.TryBox.Text = "99213-25";
            w.TryIt();
            Assert.Equal("Rejected: procedure_code takes 5 characters; \"99213-25\" has 8", w.TryResult.Text);
            w.ShowTable("patient", "birth_date");
            w.TryBox.Text = "19530604";
            w.TryIt();
            Assert.Equal("→ 1953-06-04", w.TryResult.Text);
            w.TryBox.Text = "13/45/1980";
            w.TryIt();
            Assert.StartsWith("Rejected: DOB \"13/45/1980\" isn't a date", w.TryResult.Text);
            // An error appears as soon as the mapping breaks.
            w.SetColumn("patient", "birth_date", ColumnKind.Direct, "DOB");
            Assert.Contains(w.Findings, f => f.Rule.Id == "M5" && f.About == "patient.birth_date");
            w.Undo();
            Assert.DoesNotContain(w.Findings, f => f.Rule.Id == "M5");
        }
        finally { w.Close(); }
    });
}

public sealed class RunWindowTests
{
    [Fact]
    public void A_suggested_mapping_takes_the_profiles_formats_and_codes_then_dry_runs() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow();
        w.Show();
        try
        {
            w.OpenSchema(true, Path.Combine(root, "old", "clinic-old.db"));
            w.OpenSchema(false, Path.Combine(root, "new", "clinic-new.db"));
            w.OpenEvidence(Path.Combine(root, "old", "clinic-old.lens"));
            w.Suggest();
            Assert.Contains(w.Findings, f => f.Rule.Id == "M15" && f.About == "sex_codes");
            Assert.Null(w.RunDryRun());
            Assert.StartsWith("The mapping has", w.txtStatusBar.Text);

            var p = w.ProfileOld("tblPatient")!;
            Assert.Equal(40, p.Rows);
            var text = string.Join("\n", w.ProfilePanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("→ patient.birth_date: dates in MM/dd/yyyy, yyyy-MM-dd, yyyyMMdd, M/d/yyyy; patient.birth_date reads MM/dd/yyyy; not dates in any of them: 13/45/1980", text);
            Assert.Contains("→ patient.sex_code: 1 null; patient.sex_code is required; X isn't in the lookup", text);
            w.UseFormats("patient", "birth_date");
            Assert.Equal(["MM/dd/yyyy", "yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy"], w.Map.Table("patient")!.Column("birth_date")!.Formats);
            w.UseCodes("patient", "sex_code");
            Assert.Equal(["F", "M", "X"], w.Map.Table("sex_codes")!.Rows!.Select(r => (string?)r["code"]));
            w.ProfileOld("tblClaim");
            w.UseCodes("claim", "status");
            Assert.Equal(6, w.Map.Table("claim_status")!.Rows!.Count);
            // The old code allows U, the data has X: a person decides X and nulls are U, and adds U to the codes.
            Assert.Contains(w.Findings, f => f.Rule.Id == "M11" && f.Message == "It gives U, which sex_codes doesn't have.");
            w.Change("decided about sex codes", () =>
            {
                var rows = w.Map.Table("sex_codes")!.Rows!;
                rows.RemoveAll(r => (string?)r["code"] == "X");
                rows.Add(new() { ["code"] = "U", ["meaning"] = "Unknown" });
                var sex = w.Map.Table("patient")!.Column("sex_code")!;
                sex.Map!["X"] = "U";
                sex.IfNull = "U";
            });
            Assert.DoesNotContain(w.Findings, f => f.Rule.Id == "M11");
            // The suggestion left one required column; a constant fills it, then the dry run runs.
            w.SetColumn("claim", "source_system", ColumnKind.Constant);
            w.Map.Table("claim")!.Column("source_system")!.Value = "CLINIC2006";
            var runs = w.RunDryRun() ?? throw new Xunit.Sdk.XunitException(w.txtStatusBar.Text + " " + string.Join(" | ", w.Findings.Where(f => f.Severity == Core.Mapping.Severity.Error)));
            // Without the filter and a value for a null EnteredBy, the dry run finds what's left to decide.
            var patients = runs.Single(r => r.Target == "patient");
            Assert.Equal((40L, 0L, 28L), (patients.Read, patients.Filtered, patients.Inserted));
            Assert.Contains(("created_by is required, and the value is null", 9), patients.Groups);
            Assert.Equal(10, patients.Rejects.Count(x => x.Reasons.Contains("created_by is required, and the value is null")));
            Assert.StartsWith("Dry run: ", w.txtStatusBar.Text);
            Assert.EndsWith("Nothing was written.", w.txtStatusBar.Text);
            var shown = string.Join("\n", w.DryRunPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("PATIENT · 12 REJECTED", shown);
        }
        finally { w.Close(); }
    });
}

public sealed class MigrateWindowTests
{
    [Fact]
    public void The_window_migrates_writes_the_script_suite_and_report_and_rolls_a_run_back() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow(Path.Combine(root, "clinic.dmap"));
        w.Show();
        try
        {
            w.AskSure = _ => Task.FromResult(true);
            var m = w.Migrate()!;
            Assert.Equal(225, m.Inserted);
            Assert.StartsWith("Migrated: 225 rows in, 49 rejected.", w.txtStatusBar.Text);
            Assert.Equal([("done", 225L)], w.MigrationRuns.Select(r => (r.Status, r.Rows)));
            Assert.Equal(0, w.Migrate()!.Inserted);
            Assert.Contains("225 already in from an earlier run", w.txtStatusBar.Text);

            Assert.NotNull(w.WriteScript(Path.Combine(root, "out.sql"), Core.Schema.Dialect.PostgreSql));
            Assert.Contains("OVERRIDING SYSTEM VALUE", File.ReadAllText(Path.Combine(root, "out.sql")));
            Assert.True(w.WriteParitySuite(Path.Combine(root, "proof.parity")));
            Assert.True(File.Exists(Path.Combine(root, "proof-expected", "claim.csv")));
            Assert.True(w.ExportReport(Path.Combine(root, "report.html")));
            var html = File.ReadAllText(Path.Combine(root, "report.html"));
            Assert.Contains("<h2>The migration</h2>", html);
            Assert.Contains("Every row read is in, filtered out, already in from an earlier run, or rejected", html);
            Assert.Contains("Dropped: the practice stopped faxing in 2019.", html);
            Assert.DoesNotContain("HOLLOWAY", html);
            Assert.True(w.ExportReport(Path.Combine(root, "report.xlsx")));
            using (var book = new ClosedXML.Excel.XLWorkbook(Path.Combine(root, "report.xlsx")))
                Assert.Equal(["Run", "Rejected", "Mapping", "Not migrated", "Checks"], book.Worksheets.Select(s => s.Name));

            Assert.Equal(225, w.RollBack(w.MigrationRuns[0].RunId));
            Assert.Equal("rolled back", w.MigrationRuns[0].Status);
            var text = string.Join("\n", w.MigratePanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("rolled back", text);
        }
        finally { w.Close(); }
    });
}
