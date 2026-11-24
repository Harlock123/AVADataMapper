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
            w.ShowTable("patient", "birth_date");
            Save(w, Path.Combine(dir, "mapping.png"));
        }
        finally { w.Close(); }
    });
}
