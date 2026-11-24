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
