using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AVADataMapper.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for what agents are often asked to do with a mapping,
/// written with the real tool names and arguments. Each step that calls a tool shows the call on a line of its own,
/// "→ tool {arguments}" (the tests run every recipe through the server, call by call, on the samples, so a recipe that
/// stops working fails the build). The defaults name the samples' files and tables.
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "map_a_table", Title = "Map a table end to end")]
    [Description("Works through one new table: where its rows come from, how each column is filled, what the old code and the data say, and what's left to decide.")]
    public static string MapATable(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null,
        [Description("The new table (default \"patient\").")] string? table = null,
        [Description("Its old table (default \"tblPatient\").")] string? old_table = null)
    {
        var (m, t, o) = (Or(map, "clinic.dmap"), Or(table, "patient"), Or(old_table, "tblPatient"));
        var r = new Recipe($"Work through how the new table {t} is filled, and say what's left to decide.");
        r.Step(null, "open_map", new { file = m });
        r.Step("How each column is filled now, and what the checks say.", "get_table_map", new { table = t });
        r.Step("The old table.", "describe_table", new { side = "old", table = o });
        r.Step("What the old code does with it, and its rules.", "get_evidence", new { table = o });
        r.Step("What its columns really hold (values masked unless allowed).", "profile", new { table = o });
        r.Step("The checks for the whole mapping.", "get_checks", new { });
        return r.Text($"Explain, column by column, how {t} is filled and why, quoting the old code's rules where they apply. Then list what's left: required columns with nothing to fill them, conversions the profile says will reject values, and suggestions not confirmed. Propose a set_column for each, and say the person confirms them.");
    }

    [McpServerPrompt(Name = "review_suggestions", Title = "Suggest a mapping and review it")]
    [Description("Starts a mapping from two schemas and a scan of the old system, suggests every table and column, and goes through what the person should check.")]
    public static string ReviewSuggestions(
        [Description("The old database (default \"old/clinic-old.db\").")] string? old = null,
        [Description("The new database or DDL (default \"new/clinic-new.db\").")] string? @new = null,
        [Description("The AVALegacyLens scan of the old system (default \"old/clinic-old.lens\").")] string? scan = null)
    {
        var r = new Recipe("Suggest a mapping from the old schema to the new one, and say what to check before confirming it.");
        r.Step(null, "new_map", new { });
        r.Step(null, "open_schema", new { side = "old", file = Or(old, "old/clinic-old.db") });
        r.Step(null, "open_schema", new { side = "new", file = Or(@new, "new/clinic-new.db") });
        r.Step("The old code as evidence: its rules make suggestions likelier.", "open_evidence", new { file = Or(scan, "old/clinic-old.lens") });
        r.Step(null, "suggest", new { });
        r.Step("What's suggested and not confirmed, and what nothing fills.", "get_coverage", new { section = "suggested" });
        r.Step(null, "get_coverage", new { section = "required" });
        r.Step("A table with conversions to look at.", "get_table_map", new { table = "patient" });
        return r.Text("Group the suggestions: sure ones the person can confirm at once (confirm with sure_only), likely ones with the evidence for each, and guesses. List the required columns nothing fills, with a proposal for each. Don't confirm anything yourself.");
    }

    [McpServerPrompt(Name = "explain_rejects", Title = "Explain what the dry run rejects")]
    [Description("Runs the dry run and explains every row it would reject: its own problems first, then the rows that only follow a rejected one.")]
    public static string ExplainRejects(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null,
        [Description("A table to look at closely (default \"patient\").")] string? table = null)
    {
        var t = Or(table, "patient");
        var r = new Recipe("Run the dry run and explain what it would reject, and why.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step("The whole migration in memory; nothing is written.", "dry_run", new { });
        r.Step($"{t}'s rows rejected for their own values.", "get_rejects", new { table = t, own_only = true });
        r.Step("Show the person the table's mapping.", "render_view", new { table = t });
        return r.Text("Say how many rows would go in and how many wouldn't. For each kind of reason, say what's wrong in the data (from the masked shapes), and whether the fix is in the mapping (a format, a lookup, ifNull, a filter) or in the old data. Say how many rows are rejected only because a row they point at is: fixing the first fixes them.");
    }

    [McpServerPrompt(Name = "rejects_to_rules", Title = "Turn rejects into mapping rules")]
    [Description("Uses the profile to fix what rejects rows: the date formats text really holds, codes the lookup doesn't know, nulls in required columns; tries each change, then runs the dry run again.")]
    public static string RejectsToRules(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null)
    {
        var r = new Recipe("Find what rejects rows, fix what the mapping can fix, and prove it with the dry run.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step(null, "dry_run", new { });
        r.Step("What the birth dates really hold.", "profile", new { table = "tblPatient" });
        r.Step("Try a value the dry run rejected.", "try_value", new { table = "patient", column = "birth_date", value = "13/45/1980" });
        r.Step("Try the shape the profile shows most.", "try_value", new { table = "patient", column = "birth_date", value = "06/04/1953" });
        r.Step("Run it again after any change.", "dry_run", new { });
        return r.Text("For each kind of reject, say whether a mapping change fixes it (propose it with set_column, try it with try_value) or the old row is wrong and needs a person (a date that isn't one, a name with no comma). Never change data to make a check pass.");
    }

    [McpServerPrompt(Name = "find_dropped", Title = "Find what isn't migrated")]
    [Description("Lists every old table and column nothing reads, with what the old code does with each, and the decisions already made.")]
    public static string FindDropped(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null)
    {
        var r = new Recipe("Find everything in the old database that isn't migrated, and say what's decided and what isn't.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step("Old tables and columns nothing reads, not decided.", "get_coverage", new { section = "unread" });
        r.Step("What's dropped, and why.", "get_coverage", new { section = "dropped" });
        r.Step("What the old code does with one of them.", "get_evidence", new { table = "tblPatient", column = "Fax" });
        return r.Text("For each, say what the old code does with it (nothing, written but never read, read somewhere). Propose drop for the ones the evidence says are unused, with the evidence as the reason; for the rest, say what the person needs to decide. A drop is recorded as yours: the person sees it.");
    }

    [McpServerPrompt(Name = "try_a_change", Title = "Try a change, and go back")]
    [Description("Makes a change to the mapping under a checkpoint, measures it with the dry run, shows what changed, and goes back.")]
    public static string TryAChange(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null)
    {
        var r = new Recipe("Try cutting long visit reasons to fit instead of rejecting those visits, measure it, and go back.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step(null, "checkpoint", new { name = "before", note = "Before trying the change." });
        r.Step("The change: keep the first 200 characters.", "set_column", new { table = "visit", column = "reason", mapping = new { kind = "expression", expression = "left(Reason, 200)" }, reason = "Keep what fits rather than lose the visit." });
        r.Step(null, "try_value", new { table = "visit", column = "reason", value = "A reason" });
        r.Step(null, "dry_run", new { });
        r.Step(null, "diff", new { since = "before" });
        r.Step("Back as it was (one undo step).", "restore_checkpoint", new { name = "before" });
        return r.Text("Say what the change did (rows that go in now, and what's lost: text cut short), and leave the choice to the person; the mapping is back as it was.");
    }

    [McpServerPrompt(Name = "check_before_migrating", Title = "Check a mapping before migrating")]
    [Description("Everything to look at before a migration: errors, required columns nothing fills, unconfirmed suggestions, the dry run, and earlier runs.")]
    public static string CheckBeforeMigrating(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null)
    {
        var r = new Recipe("Say whether this mapping is ready to migrate, and what stands in the way.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step("Errors stop a migration; warnings reject rows.", "get_checks", new { });
        r.Step(null, "get_coverage", new { section = "required" });
        r.Step(null, "get_coverage", new { section = "suggested" });
        r.Step(null, "dry_run", new { });
        r.Step("Earlier runs in the new database.", "list_runs", new { });
        return r.Text("Answer ready or not ready, and list what's in the way, worst first. A migration needs no errors; the person decides whether the rejected rows are acceptable. The person runs it (Migrate tab).");
    }

    [McpServerPrompt(Name = "signoff_report", Title = "The sign-off report")]
    [Description("Runs the dry run and writes the sign-off report for the people who approve the migration.")]
    public static string SignoffReport(
        [Description("The mapping (default \"clinic.dmap\").")] string? map = null,
        [Description("The report to write (default \"migration-report.html\"; .xlsx for a workbook).")] string? output = null)
    {
        var o = Or(output, "migration-report.html");
        var r = new Recipe($"Write the sign-off report, {o}, and summarize it.");
        r.Step(null, "open_map", new { file = Or(map, "clinic.dmap") });
        r.Step(null, "dry_run", new { });
        r.Step(null, "get_overview", new { });
        r.Step(null, "export_report", new { output = o });
        return r.Text("Summarize for the approvers: rows that go in, rows rejected and why (by kind), what's not migrated and who decided, and the checks' warnings. Name the report file; it lists rejects by old key, with values masked.");
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on. File and table names in these calls are the samples': use the person's own.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }
}
