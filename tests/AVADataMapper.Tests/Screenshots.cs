using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AVADataMapper.Agents.Chat;
using AVADataMapper.Tests.Agents;
using static AVADataMapper.Tests.Agents.FakeModelServer;

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

    [Fact]
    public void Ai_chat() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MAPPER_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow(Path.Combine(root, "clinic.dmap")) { Width = 1700, Height = 950 };
        w.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        w.AgentSettings.ChatProvider = "OpenAICompatible";
        w.AgentSettings.ChatServerUrl = "http://localhost:1234/v1";
        w.AgentSettings.ChatServerModel = "qwen3-8b";
        w.AgentSettings.WorkingFolder = root;
        w.Show();
        try
        {
            if (!w.ChatHolder.IsVisible) w.ToggleChat();
            var server = new FakeModelServer("http://localhost:1234/v1")
                .Reply(Say("I'll run the dry run: nothing is written."), Call("c1", "dry_run", new { }))
                .Reply(Call("c2", "get_rejects", new { table = "patient", own_only = true }))
                .Reply(Call("c3", "get_evidence", new { table = "tblPatient", column = "DOB" }))
                .Reply(Say("Showing you patient.birth_date."), Call("c4", "show_in_view", new { table = "patient", column = "birth_date", tab = "dry run" }))
                .Reply(Say("225 rows would go in and 49 wouldn't, but only 11 are wrong in themselves: the other 38 follow them (a visit whose patient is rejected, and its charges).\n\n" +
                           "• Patient 5: the birth date has the shape 99/99/9999 but isn't a date (month 13). The old screen says \"Birth date must be MM/DD/YYYY.\" and didn't enforce it: a person fixes it in the old data.\n" +
                           "• Patient 8: the name has no comma, so splitting \"LAST, FIRST\" leaves given_name empty, and it's required.\n" +
                           "• Patient 11: the family name is 41 characters; the new column holds 40.\n" +
                           "• Patient 9: sex X isn't in the lookup (M, F, U). Map X to U if that's what it meant: I can propose that for you to confirm.\n\n" +
                           "Values here are masked to their shape."));
            var panel = w.Chat!;
            panel.ServerFactory = (_, key) => server.Client(key);
            panel.Input.Text = "Why would 49 rows be rejected, and what should we fix first?";
            panel.Send();
            AgentTests.Wait(panel.Running.ContinueWith(_ => true));
            Save(w, Path.Combine(dir, "ai-chat.png"));
            panel.SetupOpen = true;
            Save(w, Path.Combine(dir, "ai-chat-setup.png"));
        }
        finally { w.Close(); }
    });
}
