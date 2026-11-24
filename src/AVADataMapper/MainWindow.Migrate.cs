using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Handoff;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper;

/// <summary>The Migrate tab: running the migration (and rerunning, and rolling a run back), the script, the parity suite and the report.</summary>
public sealed partial class MainWindow
{
    public StackPanel MigratePanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };

    /// <summary>Asks before a migration writes (the question); tests answer it themselves.</summary>
    public Func<string, Task<bool>> AskSure { get; set; } = null!;

    public MigrationResult? LastMigration { get; private set; }
    public List<RunRecord> MigrationRuns { get; private set; } = [];

    /// <summary>Migrates into the new database (after the checks), keeping a journal; a rerun picks up where the last stopped.</summary>
    public MigrationResult? Migrate()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        try
        {
            var (oc, od) = Core.Data.Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc)
            {
                var (nc, nd) = Core.Data.Rows.OpenSide(Map.Target, MapPath, Connections);
                using (nc)
                {
                    LastMigration = Migrator.Run(Map, Old, New, oc, od, nc, nd);
                    MigrationRuns = Journal.List(nc, nd, Map.Id);
                }
            }
            LastRun = LastMigration.Tables;
            ShowMigrate();
            ShowDryRun();
            Say($"Migrated: {LastMigration.Inserted:N0} rows in, {LastMigration.Rejected:N0} rejected" + (LastMigration.Skipped > 0 ? $", {LastMigration.Skipped:N0} already in from an earlier run" : "") + ". Write the parity suite to prove it.");
            return LastMigration;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"The migration didn't run: {e.Message}");
            return null;
        }
    }

    /// <summary>Takes out exactly the rows one run put in; nothing else is touched.</summary>
    public long RollBack(string runId)
    {
        if (New == null) return 0;
        try
        {
            var (nc, nd) = Core.Data.Rows.OpenSide(Map.Target, MapPath, Connections);
            using (nc)
            {
                var n = Migrator.Rollback(New, nc, nd, runId);
                MigrationRuns = Journal.List(nc, nd, Map.Id);
                ShowMigrate();
                Say($"Rolled back: {n:N0} rows the run put in are out again. Nothing else was touched.");
                return n;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            Say($"Couldn't roll back: {e.Message}");
            return 0;
        }
    }

    private void LoadRuns()
    {
        MigrationRuns = [];
        if (New == null) return;
        try
        {
            var (nc, nd) = Core.Data.Rows.OpenSide(Map.Target, MapPath, Connections);
            using (nc) MigrationRuns = Journal.List(nc, nd, Map.Id);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException) { }
    }

    /// <summary>Writes the migration as a SQL script for an empty new database (in the new database's dialect, unless given).</summary>
    public List<TableRun>? WriteScript(string path, Dialect? dialect = null)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return null; }
        try
        {
            var (oc, od) = Core.Data.Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc)
            {
                using var w = new StreamWriter(path);
                var runs = ScriptWriter.Write(Map, Old, New, oc, od, dialect ?? Map.Target.Dialect, w);
                Say($"Wrote {Path.GetFileName(path)}: {runs.Sum(r => r.Inserted):N0} rows as INSERTs for an empty {dialect ?? Map.Target.Dialect} database, the {runs.Sum(r => r.Rejected):N0} rejected listed at its end.");
                return runs;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the script: {e.Message}");
            return null;
        }
    }

    /// <summary>Writes the AVAParityCheck suite that proves the migration: expected rows (CSV) against the new tables, by old key.</summary>
    public bool WriteParitySuite(string path)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return false; }
        try
        {
            var (oc, od) = Core.Data.Rows.OpenSide(Map.Source, MapPath, Connections);
            using (oc) ParityPlan.Write(Map, MapPath, Old, New, oc, od, Map.Target.Dialect, path);
            Say($"Wrote {Path.GetFileName(path)}: open it in AVAParityCheck (or AVAParityCheck --run) after the migration. Every new table's rows are checked against what the mapping makes, by old key." +
                (Map.Target.File == null ? " Name the new database \"new\" in AVAParityCheck's connections." : ""));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the parity suite: {e.Message}");
            return false;
        }
    }

    /// <summary>Writes the sign-off report (.html, or .xlsx for a workbook) with the last dry run or migration.</summary>
    public bool ExportReport(string path)
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return false; }
        try
        {
            var kind = LastMigration != null && ReferenceEquals(LastRun, LastMigration.Tables) ? "migration" : "dry run";
            if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) MigrationReport.Excel(path, Map, Old, New, LastRun, Evidence);
            else File.WriteAllText(path, MigrationReport.Html(Map, Old, New, LastRun, kind, Evidence, DateTime.Now));
            Say($"Wrote {Path.GetFileName(path)}." + (LastRun == null ? " Run the dry run first to include what would go in and what wouldn't." : ""));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't write the report: {e.Message}");
            return false;
        }
    }

    private async Task MigrateAsked()
    {
        if (Old == null || New == null) { Say("Open both schemas first."); return; }
        var into = Map.Target.Described ?? Map.Target.Name;
        if (!await AskSure($"Migrate into {into}?\n\nRows go only into tables that are empty or that this mapping filled before; nothing else is changed. Every row it puts in is recorded, so the run can be rolled back.")) return;
        Migrate();
    }

    private void ShowMigrate()
    {
        MigratePanel.Children.Clear();
        MigratePanel.Children.Add(new TextBlock { Text = "Migrate", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        MigratePanel.Children.Add(new TextBlock
        {
            Text = "The migration puts the rows the dry run makes into the new database, in batches, and records each row's old and new keys in a journal there (avadm_runs, avadm_keys). " +
                   "It writes only to tables that are empty or that this mapping filled, and never changes a row it didn't put in. Run it again and it picks up where it stopped; roll a run back and exactly its rows come out.",
            Foreground = Look.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left,
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 6) };
        Button B(string text, Func<Task> click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += async (_, _) => await click();
            bar.Children.Add(b);
            return b;
        }
        B("Migrate…", MigrateAsked);
        B("Write the Script…", async () => { if (await PickSaveFile("The migration as a SQL script", (Map.Name ?? "migration").Replace(':', '-') + ".sql") is { } p) WriteScript(p); });
        B("Write the Parity Suite…", async () => { if (await PickSaveFile("The proof, for AVAParityCheck", "migration" + ParityPlan.Extension) is { } p) WriteParitySuite(p); });
        B("Export the Report…", async () => { if (await PickSaveFile("The sign-off report (.html or .xlsx)", "migration-report.html") is { } p) ExportReport(p); });
        MigratePanel.Children.Add(bar);
        if (LastMigration is { } m)
            MigratePanel.Children.Add(new TextBlock { Text = $"Last run: {m.Inserted:N0} rows in, {m.Rejected:N0} rejected, {m.Skipped:N0} already in. Its rejects are on the Dry Run tab.", Foreground = Look.Clean, FontSize = 13 });
        MigratePanel.Children.Add(new TextBlock { Text = $"RUNS OF THIS MAPPING IN {(Map.Target.Described ?? "THE NEW DATABASE").ToUpperInvariant()}", Foreground = Look.Heading, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
        if (MigrationRuns.Count == 0) MigratePanel.Children.Add(new TextBlock { Text = "None yet.", Foreground = Look.Dim, FontSize = 12.5 });
        foreach (var r in MigrationRuns)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = r.Started, Foreground = Look.Code, FontFamily = Look.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = r.Status, Foreground = r.Status == "done" ? Look.Clean : r.Status == "rolled back" ? Look.Dim : Look.Warn, FontSize = 12.5, Width = 90, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{r.Rows:N0} rows", Foreground = Look.Text, FontSize = 12.5, Width = 90, VerticalAlignment = VerticalAlignment.Center });
            if (r.Status != "rolled back" && r.Rows > 0)
            {
                var back = new Button { Content = "Roll Back", FontSize = 11.5, Padding = new Thickness(8, 1) };
                var id = r.RunId;
                back.Click += async (_, _) => { if (await AskSure($"Take out the {r.Rows:N0} rows this run put in?")) RollBack(id); };
                row.Children.Add(back);
            }
            MigratePanel.Children.Add(row);
        }
    }

    private async Task<bool> AskYesNo(string question)
    {
        var answer = false;
        var yes = new Button { Content = "Yes", MinWidth = 90, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 90, IsCancel = true };
        var dialog = new Window
        {
            Title = "AVA Data Mapper", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 14,
                Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yes, no } } },
            },
        };
        yes.Click += (_, _) => { answer = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return answer;
    }

    private partial IEnumerable<TabItem> EvenLaterTabs()
    {
        AskSure = AskYesNo;
        ShowMigrate();
        return [new TabItem { Header = "Migrate", Content = new ScrollViewer { Content = MigratePanel }, FontSize = 14 }, .. AgentTabs()];
    }

    partial void Ran()
    {
        ProfileTable.ItemsSource = Old?.Tables.Select(t => t.Name).ToList();
        if (_runsFor != (Map.Id, New?.Source))
        {
            _runsFor = (Map.Id, New?.Source);
            LoadRuns();
            ShowMigrate();
        }
    }

    private (string, string?) _runsFor;

    private partial IEnumerable<object> EvenLaterMappingItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        new Separator(),
        item("_Migrate…", () => _ = MigrateAsked(), null, KeyModifiers.None),
        item("Write the _Script…", () => { Tabs.SelectedIndex = 5; }, null, KeyModifiers.None),
        item("Write the Parity S_uite…", () => { Tabs.SelectedIndex = 5; }, null, KeyModifiers.None),
    ];

    private partial IEnumerable<object> EvenLaterViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
        [item("_Migrate", () => Tabs.SelectedIndex = 5, Key.D6, KeyModifiers.Control)];

    private partial IEnumerable<TabItem> AgentTabs();
}
