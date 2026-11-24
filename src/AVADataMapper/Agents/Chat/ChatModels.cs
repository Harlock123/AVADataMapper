using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AVADataMapper.Mcp;
using McpTool = ModelContextProtocol.Protocol.Tool;

namespace AVADataMapper.Agents.Chat;

/// <summary>The Claude models the chat offers, by their API ids.</summary>
public static class ChatModels
{
    public const string Opus = "claude-opus-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-4-5-20251001";

    /// <summary>The model a new chat starts with.</summary>
    public const string Default = Opus;

    /// <summary>The choices, most capable first, with a name to show.</summary>
    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (Opus, "Claude Opus 5.5"),
        (Sonnet, "Claude Sonnet 5.5"),
        (Haiku, "Claude Haiku 4.5"),
    ];

    public static string Label(string id) => All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

/// <summary>What the model is told: the MCP server's own instructions, and a few lines about working inside the program.</summary>
public static class ChatPrompt
{
    public const string InApp = """
        You are working inside AVA Data Mapper itself, in its AI Chat panel, with the person watching. Talk to them briefly and plainly: say what you're about to look at, look (get_overview, get_table_map, describe_table, get_evidence, profile, get_checks, dry_run), then answer in plain words, naming the table, column and rule (M6) your answer rests on. Keep it to a few sentences; the tool calls show in the panel on their own. Read before you answer: never guess what a column holds, what the old code does, or why a row is rejected; the tools tell you. To point the person at a place, use show_in_view (it selects it in their window) or render_view. What you map is a proposal the person confirms; say so. Unless your answers say "values_masked": false, values are masked to their shape: reason from shapes and counts.
        """;

    /// <summary>The system prompt: the same instructions every MCP agent gets, then <see cref="InApp"/>.</summary>
    public static string System => MapperServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>Added for a model given only <see cref="ChatTools.Core"/>.</summary>
    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";

    /// <summary>Added for a model that can't see pictures (its tools' pictures become a line of text).</summary>
    public const string NoPicturesNote =
        "You can't see pictures in this chat: render_view still shows the person the table, but read it yourself with get_table_map and get_checks.";
}

/// <summary>Which of the MCP server's tools the chat offers a model.</summary>
public static class ChatTools
{
    /// <summary>
    /// The core set for small local models, which choose better among fewer tools: finding and opening files, the
    /// the mapping and its checks, the old side's schema, evidence and profile, trying values, and the dry run.
    /// </summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "get_overview", "list_tables", "describe_table", "get_table_map", "set_column", "suggest", "get_checks", "get_coverage",
        "get_evidence", "profile", "try_value", "dry_run", "get_rejects", "show_in_view",
    ];

    /// <summary>The core tools among <paramref name="tools"/>, in the server's order.</summary>
    public static IReadOnlyList<McpTool> CoreOf(IReadOnlyList<McpTool> tools) => tools.Where(t => Core.Contains(t.Name)).ToList();
}
