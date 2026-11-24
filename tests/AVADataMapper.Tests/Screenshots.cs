using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace AVADataMapper.Tests;

/// <summary>The README's pictures (MAPPER_SHOTS set to a folder), headless. Off unless MAPPER_SHOTS is set; run with --filter Screenshots.</summary>
public sealed class Screenshots
{
    internal static void Save(Window w, string path)
    {
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
    }

    [Fact]
    public void Mapping() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MAPPER_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var w = new MainWindow(Samples.Path("clinic.dmap")) { Width = 1500, Height = 900 };
        w.Show();
        try
        {
            foreach (var item in w.OldTree.Items.OfType<TreeViewItem>().Take(2)) item.IsExpanded = true;
            foreach (var item in w.NewTree.Items.OfType<TreeViewItem>().Skip(3).Take(1)) item.IsExpanded = true;
            w.ShowTable("patient", "family_name");
            Save(w, Path.Combine(dir, "mapping.png"));
            w.Tabs.SelectedIndex = 2;
            Save(w, Path.Combine(dir, "checks.png"));
            w.ProfileOld("tblPatient");
            w.Tabs.SelectedIndex = 3;
            Save(w, Path.Combine(dir, "profile.png"));
            w.RunDryRun();
            w.Tabs.SelectedIndex = 4;
            Save(w, Path.Combine(dir, "dry-run.png"));
            w.Tabs.SelectedIndex = 0;
            w.ShowTable("charge", "procedure_code");
            w.TryBox.Text = "99213-25";
            Save(w, Path.Combine(dir, "try.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Migrated() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MAPPER_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow(Path.Combine(root, "clinic.dmap")) { Width = 1500, Height = 900 };
        w.Show();
        try
        {
            w.Migrate();
            w.Migrate();
            w.Tabs.SelectedIndex = 5;
            Save(w, Path.Combine(dir, "migrate.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Suggested() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MAPPER_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var w = new MainWindow { Width = 1500, Height = 900 };
        w.Show();
        try
        {
            w.OpenSchema(true, Samples.Path("old/clinic-old.db"));
            w.OpenSchema(false, Samples.Path("new/clinic-new.pg.sql"));
            w.OpenEvidence(Samples.Path("old/clinic-old.lens"));
            w.Suggest();
            w.Confirm(sureOnly: true);
            w.ShowTable("patient", "family_name");
            Save(w, Path.Combine(dir, "suggested.png"));
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "coverage.png"));
        }
        finally { w.Close(); }
    });
}
