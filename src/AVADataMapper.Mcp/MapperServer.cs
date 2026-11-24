using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVADataMapper.Mcp;

/// <summary>
/// The MCP server: <see cref="MapperTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection), with
/// instructions that tell an agent AVA Data Mapper's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open window, over the network with a token, and in-process for the AI Chat.
/// </summary>
public static class MapperServer
{
    public const string Name = "AVADataMapper";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Data Mapper moves a legacy system's data into its rebuild and proves it arrived. It keeps a mapping (.dmap) between an old database and a new one (SQL Server, PostgreSQL or SQLite, live or from DDL scripts): for each new table, the old table its rows come from and which of them; for each new column, how it's filled from the old row. You read the schemas, propose mappings, try them, and explain what the dry run finds; a person confirms what's used.

        Values (it matters): the old database holds people's data. Unless the person lets agents see values, the values you see in profiles and rejects are masked to their shape (H******, 99/99/9999); codes stay readable (columns with few values used again and again). Every answer says "values_masked". Don't ask for values: shapes, counts and codes are enough to map.

        The mapping (it matters):
        - Column kinds: direct (copied), convert (to date with formats, number, integer, bool with a map, text), lookup (codes through a map, into a code table), key (the new key of the row an old key pointed at: through the key map of a table loaded before), split (a part of a column at a separator: "LAST, FIRST" part 1 or 2), merge (columns joined), constant, default (the new column's), expression (the mapping's own small expression language, not SQL), generated (identity). Options: trim, case, blankIsNull, ifNull (what a null becomes), unknown (what a value that doesn't convert or isn't in the map does: reject the row, null, or default).
        - Every value then has to fit its new column (type, length, precision, required); a row that doesn't is rejected with every reason, and rows pointing at it are rejected too, naming the first cause.
        - What you map is a proposal (certainty likely) until the person confirms it; suggestions are sure, likely or guessed with a reason. Every change is one undo step the person can take back.
        - An AVALegacyLens scan of the old system, when there is one, says which code writes each old column and the business rules in the original's words: use it as evidence, and quote it.

        A good way to work: get_overview; get_coverage (what's required and empty, what's suggested, what's old and unread) and get_checks (what can't run, what will reject rows); get_table_map for a table; describe_table and get_evidence for the old side; profile an old table before converting text (date formats, codes, lengths); set_column with try_value to test it; dry_run to see every reject (get_rejects for a table); suggest fills gaps from names, types, keys and the scan; use_formats and use_codes take a profile's findings into the mapping. export_report writes the sign-off report. render_view shows the person a table's mapping and show_in_view selects it in their window. batch runs several calls at once.

        The migration writes to the person's new database: migrate and roll_back run only in the open window when the person says yes, into tables that are empty or that this mapping filled, each row recorded so a run can be rolled back. The script and the parity suite (the proof, for AVAParityCheck) hold the data, so they need values allowed. Never claim a mapping is used, a migration done or a check passed unless a tool said so.

        The server's prompts are saved recipes (map a table, review suggestions, explain the rejects, turn rejects into rules, find what's dropped, try a change, check before migrating, the sign-off report): step-by-step plans with the exact tool calls.
        """;




    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IExplorerHost host) => Tools(new MapperTools(host));

    /// <summary>The MCP tools of one set of handlers (one session's: its checkpoints and its recording).</summary>
    public static IEnumerable<McpServerTool> Tools(MapperTools target)
    {
        foreach (var method in typeof(MapperTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Data Mapper",
        Version = typeof(MapperServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IExplorerHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IExplorerHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        var target = new MapperTools(host);
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                var result = await next(request, cancel).ConfigureAwait(false);
                // The session's calls, as steps save_steps can keep: every call that worked.
                if (tool != null && result.IsError != true)
                    target.Record(tool.Name, request.Params!.Arguments);
                return result;
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(target)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IExplorerHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            // A required argument that's missing: say which, and what it is (the SDK's own words are about dictionaries).
            if (tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                var missing = required.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && args?.ContainsKey(r) != true).ToList();
                if (missing.Count > 0)
                    return $"{tool.Name} needs {string.Join(" and ", missing)}: " + string.Join(" ", missing.Select(m =>
                        props.TryGetProperty(m, out var p) && p.TryGetProperty("description", out var d) ? $"{m} is {d.GetString()?.TrimEnd('.')}." : $"{m}."));
            }
            if (args == null) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IExplorerHost
    {
        public string Root => Path.GetTempPath();
        public IViewPainter Painter => throw new InvalidOperationException("Not connected.");
        public bool Live => false;
        public Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected.");
    }
}
