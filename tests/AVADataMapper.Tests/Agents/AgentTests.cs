using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using AVADataMapper.Agents;
using AVADataMapper.Agents.Chat;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;
using AVADataMapper.Mcp;
using ModelContextProtocol.Server;
using static AVADataMapper.Tests.Agents.FakeModelServer;

namespace AVADataMapper.Tests.Agents;

/// <summary>
/// Before any test runs: the AI side's settings, keys, token and saved chats go to a temporary folder, so no test
/// reads or writes the person's own (and none ever finds a real API key).
/// </summary>
internal static class AgentSettingsIsolation
{
    [ModuleInitializer]
    internal static void Isolate() =>
        AgentSettings.FolderOverride = Directory.CreateTempSubdirectory("avadatamapper-agents-").FullName;
}

internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on Avalonia's headless platform, on a copy of the samples. Values masked unless a test says otherwise, as agents
/// see them by default. The chat's models are scripted fakes of both APIs; no real model or key is ever used.
/// </summary>
public sealed class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    internal static readonly string[] ToolNames =
    [
        "list_files", "open_map", "new_map", "open_schema", "open_evidence", "save_map_as", "get_overview", "list_tables", "describe_table",
        "get_table_map", "set_table_source", "set_column", "unmap_column", "set_code_rows", "suggest", "confirm", "drop", "undo", "redo",
        "check_expression", "try_value", "get_checks", "get_coverage", "get_evidence", "profile", "use_formats", "use_codes", "dry_run",
        "get_rejects", "export_report", "write_script", "write_parity_suite", "migrate", "list_runs", "roll_back", "get_view", "show_in_view",
        "render_view", "batch", "get_recording", "save_steps", "run_steps", "checkpoint", "diff", "render_checkpoint", "restore_checkpoint",
    ];

    /// <summary>A server on a copy of the samples, with no window round it.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public HeadlessDesk Desk { get; }
        public string Root { get; } = Samples.Copy();
        public McpToolBridge Bridge { get; }
        public HeadlessSession Session => (HeadlessSession)Desk.Session;

        public Rig(bool real = false)
        {
            Desk = new HeadlessDesk(null, real, new Core.Schema.SavedConnections(Directory.CreateTempSubdirectory("dm-conn-").FullName));
            var stage = new Panel();
            _window = new Window { Width = 20, Height = 20, Content = stage };
            _window.Show();
            Bridge = Wait(McpToolBridge.StartAsync(new ExplorerHost(new AvaloniaThread(), Desk, new ViewPainter(stage), Root)));
        }

        public ToolOutcome Call(string tool, object? args = null) =>
            Wait(Bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public string Error(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.True(o.IsError, $"{tool} should have failed: {o.Text}");
            return o.Text;
        }

        public void Dispose()
        {
            Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            _window.Close();
        }
    }

    internal static T Wait<T>(Task<T> task, int seconds = 30)
    {
        for (var i = 0; i < seconds * 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    private static void WithRig(Action<Rig> test, bool real = false) => HeadlessApp.Run(() =>
    {
        using var rig = new Rig(real);
        test(rig);
    });

    private static IEnumerable<string> Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!);

    [Fact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are() => WithRig(rig =>
    {
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name);
            if (t.InputSchema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    Assert.True(p.Value.TryGetProperty("description", out _), $"{t.Name}.{p.Name} has no description.");
                    Assert.Matches("^[a-z_]+$", p.Name);
                }
        }
        Assert.Contains("masked to their shape", MapperServer.Instructions);
        Assert.Contains("has no argument \"nope\"", rig.Error("list_files", new { nope = 1 }));
    });

    [Fact]
    public void A_mapping_is_opened_read_and_its_old_side_described_with_the_old_codes_evidence() => WithRig(rig =>
    {
        var files = rig.Ok("list_files").GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("file").GetString()!, f => f.GetProperty("kind").GetString());
        Assert.Equal("mapping", files["clinic.dmap"]);
        Assert.Equal("SQLite database", files["old/clinic-old.db"]);
        Assert.Equal("AVALegacyLens scan", files["old/clinic-old.lens"]);
        var o = rig.Ok("open_map", new { file = "clinic.dmap" });
        Assert.Equal("Clinic: legacy to rebuild", o.GetProperty("name").GetString());
        Assert.Equal(8, o.GetProperty("checks").GetProperty("warnings").GetInt32());
        Assert.True(o.GetProperty("values_masked").GetBoolean());
        Assert.Equal(["payer", "sex_codes", "claim_status", "patient", "visit", "charge", "claim"], rig.Ok("list_tables", new { side = "new" }).GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("table").GetString()));

        var map = rig.Ok("get_table_map", new { table = "patient" });
        Assert.Equal("tblPatient", map.GetProperty("from").GetString());
        var family = map.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "family_name");
        Assert.Equal("PatName split at ',', part 1; trimmed", family.GetProperty("filled_by").GetString());
        Assert.Equal("split", family.GetProperty("mapping").GetProperty("kind").GetString());
        Assert.Contains("M6 Warning", Strings(family, "checks").Single());

        var old = rig.Ok("describe_table", new { side = "old", table = "tblPatient" });
        var dob = old.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "DOB");
        Assert.Equal(["Birth date must be MM/DD/YYYY."], Strings(dob, "rules"));
        var ev = rig.Ok("get_evidence", new { table = "tblPatient", column = "Fax" });
        Assert.True(ev.GetProperty("columns").GetProperty("Fax").GetProperty("unused").GetBoolean());
        Assert.Equal(2, rig.Ok("get_coverage", new { section = "dropped" }).GetProperty("count").GetInt32());
        Assert.Equal(8, rig.Ok("get_checks").GetProperty("warnings").GetInt32());
    });

    [Fact]
    public void An_agents_changes_are_proposals_tried_checked_undone_and_confirmed() => WithRig(rig =>
    {
        rig.Ok("open_map", new { file = "clinic.dmap" });
        var set = rig.Ok("set_column", new { table = "visit", column = "reason", mapping = new { kind = "expression", expression = "left(Reason, 200)" }, reason = "Keep what fits." });
        Assert.StartsWith("left(Reason, 200)", set.GetProperty("column").GetString());
        var cm = rig.Session.Work.Map.Table("visit")!.Column("reason")!;
        Assert.Equal((Certainty.Likely, "Proposed by an AI agent: Keep what fits."), (cm.Certainty, cm.Reason));
        Assert.Equal("\"short\"", rig.Ok("try_value", new { table = "visit", column = "reason", value = "short" }).GetProperty("result").GetString());
        Assert.Contains("isn't a column", rig.Error("set_column", new { table = "visit", column = "reason", mapping = new { kind = "expression", expression = "left(Nope, 2)" } }));
        Assert.Contains("isn't a column mapping", rig.Error("set_column", new { table = "visit", column = "reason", mapping = new { kind = "sideways" } }));
        Assert.True(rig.Ok("undo").GetProperty("undone").GetBoolean());
        Assert.Equal(ColumnKind.Direct, rig.Session.Work.Map.Table("visit")!.Column("reason")!.Kind);
        Assert.Contains("isn't a date", rig.Ok("try_value", new { table = "patient", column = "birth_date", value = "31/12/1999" }).GetProperty("rejected").GetString());
        // Headless there's no one to ask, so confirming goes through; in the window the person is asked.
        rig.Ok("redo");
        Assert.Equal(1, rig.Ok("confirm", new { table = "visit", column = "reason" }).GetProperty("confirmed").GetInt32());
        rig.Ok("drop", new { about = "tblPatient.Deleted", why = "Only used to filter." });
        Assert.Equal("AI agent", rig.Session.Work.Map.Decisions.Single(d => d.About == "tblPatient.Deleted").By);
        Assert.Contains("unsaved changes", rig.Error("open_map", new { file = "clinic.dmap" }));
        var bad = rig.Ok("check_expression", new { expression = "Gone = 1", old_table = "tblPatient" });
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("isn't a column", bad.GetProperty("problem").GetString());
        Assert.True(rig.Ok("check_expression", new { expression = "Deleted = 0", old_table = "tblPatient" }).GetProperty("ok").GetBoolean());
    });

    [Fact]
    public void A_new_mapping_is_suggested_profiled_and_dry_run_with_values_masked() => WithRig(rig =>
    {
        rig.Ok("new_map");
        rig.Ok("open_schema", new { side = "old", file = "old/clinic-old.db" });
        rig.Ok("open_schema", new { side = "new", file = "new/clinic-new.db" });
        rig.Ok("open_evidence", new { file = "old/clinic-old.lens" });
        Assert.Equal("7 tables and 32 columns", rig.Ok("suggest").GetProperty("suggested").GetString());
        var p = rig.Ok("profile", new { table = "tblPatient" });
        var cols = p.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("column").GetString()!);
        Assert.Equal(["MM/dd/yyyy", "yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy"], Strings(cols["DOB"], "date_formats"));
        Assert.Equal(["99/99/9999"], Strings(cols["DOB"], "not_dates"));
        Assert.Equal(["F ×19", "M ×19", "X ×1"], Strings(cols["Sex"], "common_values"));
        Assert.False(cols["PatName"].TryGetProperty("common_values", out var none) && none.ValueKind != JsonValueKind.Null);
        rig.Ok("use_formats", new { table = "patient", column = "birth_date" });
        rig.Ok("use_codes", new { table = "patient", column = "sex_code" });
        rig.Ok("set_code_rows", new { table = "sex_codes", rows = new object[] { new { code = "M", meaning = "Male" }, new { code = "F", meaning = "Female" }, new { code = "U", meaning = "Unknown" } } });
        rig.Ok("set_column", new { table = "patient", column = "sex_code", mapping = new { kind = "lookup", from = "Sex", table = "sex_codes", map = new { M = "M", F = "F", X = "U" }, ifNull = "U", unknown = "reject" } });
        rig.Ok("use_codes", new { table = "claim", column = "status" });
        rig.Ok("set_column", new { table = "claim", column = "source_system", mapping = new { kind = "constant", value = "CLINIC2006" } });
        Assert.Equal(0, rig.Ok("get_checks", new { severity = "error" }).GetProperty("errors").GetInt32());
        var run = rig.Ok("dry_run");
        var patient = run.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("table").GetString() == "patient");
        Assert.Contains(Strings(patient, "own"), r => r.StartsWith("5: DOB \"99/99/9999\" isn't a date"));
        var claims = rig.Ok("get_rejects", new { table = "claim", own_only = true }).GetProperty("rows").EnumerateArray().ToList();
        Assert.All(claims, c => Assert.StartsWith("C", c.GetProperty("old_key").GetString()));
        Assert.DoesNotContain(claims, c => c.GetProperty("old_key").GetString()!.Contains("2026"));
    });

    [Fact]
    public void Files_are_written_new_in_the_folder_and_the_data_only_with_values_allowed() => WithRig(rig =>
    {
        rig.Ok("open_map", new { file = "clinic.dmap" });
        rig.Ok("dry_run");
        rig.Ok("export_report", new { output = "out/report.html" });
        Assert.Contains("<h2>The dry run</h2>", File.ReadAllText(Path.Combine(rig.Root, "out", "report.html")));
        rig.Ok("save_map_as", new { output = "out/copy.dmap" });
        Assert.Equal("../old/clinic-old.db", MapFile.Load(Path.Combine(rig.Root, "out", "copy.dmap")).Source.File);
        Assert.Contains("already exists", rig.Error("save_map_as", new { output = "clinic.dmap" }));
        Assert.Contains("outside", rig.Error("export_report", new { output = "../x.html" }));
        Assert.Contains("hold the data", rig.Error("write_parity_suite", new { output = "out/proof.parity" }));
        Assert.Contains("holds the data", rig.Error("write_script", new { output = "out/m.sql" }));
        Assert.Contains("only done in the open window", rig.Error("migrate"));
        Assert.Contains("only done in the open window", rig.Error("roll_back", new { run = "x" }));
        Assert.Empty(rig.Ok("list_runs").GetProperty("runs").EnumerateArray());
    });

    [Fact]
    public void With_values_allowed_the_script_and_the_parity_suite_are_written() => WithRig(rig =>
    {
        rig.Ok("open_map", new { file = "clinic.dmap" });
        var p = rig.Ok("profile", new { table = "tblPatient" });
        Assert.False(p.GetProperty("values_masked").GetBoolean());
        Assert.Equal(225, rig.Ok("write_script", new { output = "out/m.sql", dialect = "postgresql" }).GetProperty("rows").GetInt32());
        Assert.Equal(7, rig.Ok("write_parity_suite", new { output = "out/proof.parity" }).GetProperty("checks").GetInt32());
        Assert.True(File.Exists(Path.Combine(rig.Root, "out", "proof-expected", "patient.csv")));
    }, real: true);

    [Fact]
    public void The_view_is_drawn_shown_and_batches_steps_and_checkpoints_work() => WithRig(rig =>
    {
        rig.Ok("open_map", new { file = "clinic.dmap" });
        var shot = rig.Call("render_view", new { table = "patient" });
        Assert.False(shot.IsError, shot.Text);
        Assert.Single(shot.Images);
        rig.Ok("show_in_view", new { table = "patient", column = "birth_date", tab = "mapping" });
        Assert.Equal(("mapping", "patient", "birth_date"), (rig.Session.View.Tab, rig.Session.View.Table, rig.Session.View.Column));
        Assert.Single(rig.Call("render_view").Images);
        var batch = rig.Ok("batch", new { steps = new object[] { new { tool = "get_table_map", args = new { table = "visit" } }, new { tool = "get_table_map", args = new { table = "claim" } } } });
        Assert.Equal(2, batch.GetProperty("answers").GetArrayLength());
        rig.Ok("checkpoint", new { name = "before" });
        rig.Ok("set_column", new { table = "claim", column = "source_system", mapping = new { kind = "constant", value = "OLD" } });
        var diff = rig.Ok("diff", new { since = "before" });
        Assert.Contains("claim.source_system", Strings(diff, "changed_elements"));
        Assert.Single(rig.Call("render_checkpoint", new { name = "before" }).Images);
        rig.Ok("restore_checkpoint", new { name = "before" });
        Assert.Equal("CLINIC2006", (string?)rig.Session.Work.Map.Table("claim")!.Column("source_system")!.Value);
        rig.Ok("save_steps", new { path = "steps/look", steps = new object[] { new { tool = "get_table_map", args = new { table = "${table}" } } }, parameters = new { table = "patient" } });
        Assert.Equal("visit", rig.Ok("run_steps", new { path = "steps/look.avasteps.json", values = new { table = "visit" } }).GetProperty("answers")[0].GetProperty("answer").GetProperty("table").GetString());
    });

    // ───────────────────────────── values ─────────────────────────────

    /// <summary>Every value in the old sample that's a person's: names (each part), birth dates, phones, faxes, claim numbers, the long visit reason.</summary>
    internal static HashSet<string> Phi()
    {
        var phi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Catalogs.Open(Dialect.Sqlite, $"Data Source={Samples.Path("old/clinic-old.db")};Mode=ReadOnly");
        foreach (var row in Core.Data.Rows.Read(c, Dialect.Sqlite, Samples.Old.Table("tblPatient")!))
        {
            var name = (string)row["PatName"]!;
            phi.Add(name.Split(',')[0].Trim());
            foreach (var part in name.Split(',', ' ', '-').Select(x => x.Trim()).Where(x => x.Length >= 3 && x is not "VAN" and not "DER")) phi.Add(part);
            foreach (var k in new[] { "DOB", "Phone", "Fax" }) if (row[k] is string v && v.Trim().Length > 0) phi.Add(v.Trim());
        }
        foreach (var row in Core.Data.Rows.Read(c, Dialect.Sqlite, Samples.Old.Table("tblClaim")!)) phi.Add((string)row["ClaimNo"]!);
        phi.Add("Patient reports intermittent dizziness");
        return phi;
    }

    [Fact]
    public void The_list_of_real_values_the_no_leak_test_looks_for_covers_every_kind()
    {
        var phi = Phi();
        foreach (var real in new[] { "HOLLOWAY", "LEOPOLD", "19530604", "13/45/1980", "5555550101", "5555550210", "CLM2026007", "WORTHINGTON", "VAN DER BERG-ELLINGTON-WORTHINGTON-SMYTHE" }) Assert.Contains(real, phi);
        Assert.True(phi.Count > 100, $"{phi.Count}");
    }

    [Fact]
    public void No_tool_result_carries_a_value_from_the_old_database_when_values_are_masked() => WithRig(rig =>
    {
        var phi = Phi();
        var seen = new List<(string Call, string Text)>();
        void Check(string tool, object? args = null)
        {
            var o = rig.Call(tool, args);
            Assert.False(o.IsError, $"{tool} {JsonSerializer.Serialize(args)}: {o.Text}");
            seen.Add(($"{tool} {JsonSerializer.Serialize(args)}", o.Text));
        }
        Check("list_files");
        Check("open_map", new { file = "clinic.dmap" });
        Check("get_overview");
        foreach (var side in new[] { "old", "new" })
        {
            Check("list_tables", new { side });
            foreach (var t in (side == "old" ? Samples.Old : Samples.New).Tables) Check("describe_table", new { side, table = t.Name });
        }
        foreach (var t in Samples.New.Tables)
        {
            Check("get_table_map", new { table = t.Name });
            Check("render_view", new { table = t.Name });
        }
        foreach (var t in Samples.Old.Tables)
        {
            Check("profile", new { table = t.Name });
            Check("get_evidence", new { table = t.Name });
        }
        Check("get_checks");
        Check("get_coverage");
        Check("dry_run");
        foreach (var t in Samples.New.Tables) Check("get_rejects", new { table = t.Name, limit = 300 });
        Check("export_report", new { output = "leak/report.html" });
        Check("list_runs");
        Check("show_in_view", new { table = "patient", column = "birth_date" });
        Check("get_view");
        Check("batch", new { steps = new object[] { new { tool = "profile", args = new { table = "tblClaim" } }, new { tool = "get_rejects", args = new { table = "claim" } } } });
        Check("get_recording");
        Check("checkpoint", new { name = "x" });
        Check("diff", new { since = "x" });

        var written = Directory.EnumerateFiles(Path.Combine(rig.Root, "leak"), "*", SearchOption.AllDirectories).Select(p => ($"file {Path.GetFileName(p)}", File.ReadAllText(p)));
        var words = phi.Select(real => (real, new Regex($"(?<![A-Za-z0-9]){Regex.Escape(real)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase))).ToList();
        var leaks = new List<string>();
        foreach (var (call, text) in seen.Concat(written))
            foreach (var (real, word) in words)
                if (word.IsMatch(text))
                    leaks.Add($"{call}: \"{real}\"");
        Assert.True(seen.Count > 30, $"Only {seen.Count} calls were made.");
        Assert.All(seen.Where(s => s.Text.StartsWith('{')), s => Assert.DoesNotContain("\"values_masked\":false", s.Text));
        Assert.True(leaks.Count == 0, $"{leaks.Count} leaks:\n" + string.Join("\n", leaks.Distinct().Take(60)));
    });

    // ───────────────────────────── recipes ─────────────────────────────

    [Fact]
    public void Every_recipe_runs_call_by_call_with_values_masked() => HeadlessApp.Run(() =>
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(8, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.True(calls.Count >= 3, recipe.Name);
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var tool = rest[..space];
                Assert.Contains(tool, ToolNames);
                var args = JsonDocument.Parse(rest[(space + 1)..]).RootElement;
                var outcome = Wait(rig.Bridge.CallAsync(tool, args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
                Assert.False(outcome.IsError, $"{recipe.Name}: {tool} failed: {outcome.Text}");
            }
        }
    });

    // ───────────────────────────── the chat ─────────────────────────────

    [Fact]
    public void A_chat_on_a_local_openai_compatible_model_reads_shows_and_its_reply_can_be_undone() => WithRig(rig =>
    {
        rig.Session.Work.OpenMap(Path.Combine(rig.Root, "clinic.dmap"));
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll look at the claim table."), Call("c1", "get_table_map", new { table = "claim" }))
            .Reply(Call("c2", "set_column", new { table = "claim", column = "source_system", mapping = new { kind = "constant", value = "LEGACY" } }))
            .Reply(Say("I've proposed LEGACY as the source system; confirm it if that's right."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen/qwen3-8b" };
        Wait(chat.SendAsync("what should source_system be?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
        Assert.Equal(["get_table_map", "set_column"], chat.Entries.OfType<ToolEntry>().Select(t => t.Name));
        Assert.All(chat.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        var system = (string?)server.Requests[0]["messages"]![0]!["content"];
        Assert.Contains("AVA Data Mapper", system);
        Assert.Contains("masked", system);
        Assert.Equal("LEGACY", (string?)rig.Session.Work.Map.Table("claim")!.Column("source_system")!.Value);
        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Equal("CLINIC2006", (string?)rig.Session.Work.Map.Table("claim")!.Column("source_system")!.Value);
    });

    [Fact]
    public void A_chat_on_a_local_server_that_speaks_the_messages_api_uses_the_core_tools() => WithRig(rig =>
    {
        rig.Session.Work.OpenMap(Path.Combine(rig.Root, "clinic.dmap"));
        var claude = new FakeMessagesServer()
            .Reply(new FakeMessagesServer.ToolUse("t1", "get_checks", new { severity = "warning" }))
            .Reply(new FakeMessagesServer.Words("Eight warnings, no errors."));
        var client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = "http://localhost:1234", HttpClient = new HttpClient(claude) };
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.Anthropic, Client = client, Model = "qwen/qwen3-8b", ClaudeServer = "localhost:1234", ClaudeCoreToolsOnly = true, ClaudeSeesPictures = false };
        Wait(chat.SendAsync("is it ready?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
        var offered = claude.Requests[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ChatTools.Core.Order(), offered.Order());
        var call = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.False(call.IsError, call.Result);
        Assert.Contains("\"warnings\":8", call.Result);
    });
}
