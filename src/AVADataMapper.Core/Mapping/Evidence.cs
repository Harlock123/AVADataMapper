using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVADataMapper.Core.Mapping;

/// <summary>What the old system's code says about one of its columns or tables, from an AVALegacyLens scan.</summary>
public sealed class ColumnEvidence
{
    /// <summary>Methods (Type.Method) and procedures that write it.</summary>
    public List<string> Writers { get; } = [];
    public List<string> Readers { get; } = [];

    /// <summary>Rules found in the code about it, in the original's own words.</summary>
    public List<string> Rules { get; } = [];

    public bool Unused => Writers.Count == 0 && Readers.Count == 0;
}

/// <summary>
/// Evidence from an AVALegacyLens scan (.lens) of the old system: which code writes and reads each table and column, the
/// business rules found in it (tied to the columns their words name), and the tables it found nothing uses. It's
/// read as plain JSON; the mapper doesn't need AVALegacyLens to read it.
/// </summary>
public sealed class Evidence
{
    private readonly Dictionary<string, ColumnEvidence> _items = new(StringComparer.OrdinalIgnoreCase);

    public string Source { get; private init; } = "";

    /// <summary>Tables the scan found nothing reads or writes, with its words.</summary>
    public Dictionary<string, string> DeadTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A table's evidence (by its name, without schema).</summary>
    public ColumnEvidence? Table(string table) => _items.GetValueOrDefault(Bare(table));

    /// <summary>A column's evidence; null when the scan doesn't have the table.</summary>
    public ColumnEvidence? Column(string table, string column) => _items.GetValueOrDefault(Bare(table) + "." + column);

    private static string Bare(string table) => table.Contains('.') ? table[(table.LastIndexOf('.') + 1)..] : table;

    public static Evidence Load(string path)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("It isn't an AVALegacyLens scan.");
        }
        catch (JsonException e)
        {
            throw new FormatException($"It isn't an AVALegacyLens scan: {e.Message}");
        }
        if (root["nodes"] is not JsonArray nodes || root["edges"] is not JsonArray edges) throw new FormatException("It isn't an AVALegacyLens scan (no nodes and edges).");
        var ev = new Evidence { Source = Path.GetFileName(path) };
        // Node ids → names: db:dbo.tblpatient → tblPatient; db:dbo.tblpatient.dob → tblPatient.DOB; methods by Type.Method.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes.OfType<JsonObject>())
        {
            var id = (string?)n["id"] ?? "";
            var kind = (string?)n["kind"] ?? "";
            kinds[id] = kind;
            names[id] = (string?)n["name"] ?? id;
        }
        foreach (var (id, kind) in kinds.Where(k => k.Value == "Column").ToList())
        {
            var parent = nodes.OfType<JsonObject>().First(n => (string?)n["id"] == id)["parent"]?.GetValue<string>();
            if (parent != null && names.TryGetValue(parent, out var t)) names[id] = $"{t}.{names[id]}";
        }
        foreach (var (id, kind) in kinds.Where(k => k.Value is "Table" or "Column")) ev._items[names[id]] = new ColumnEvidence();
        string Who(string id) => kinds.GetValueOrDefault(id) == "Method" && id.Contains(":M:", StringComparison.Ordinal)
            ? string.Join(".", id[(id.IndexOf(":M:", StringComparison.Ordinal) + 3)..].Split('(')[0].Split('.').TakeLast(2))
            : names.GetValueOrDefault(id, id);
        var writes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in edges.OfType<JsonObject>())
        {
            var kind = (string?)e["kind"];
            var from = (string?)e["from"] ?? "";
            var to = (string?)e["to"] ?? "";
            if (kind is not ("Reads" or "Writes") || !names.TryGetValue(to, out var target) || !ev._items.TryGetValue(target, out var item)) continue;
            var who = Who(from);
            var list = kind == "Writes" ? item.Writers : item.Readers;
            if (!list.Contains(who)) list.Add(who);
            if (kind == "Writes" && kinds.GetValueOrDefault(to) == "Column") (writes.TryGetValue(from, out var w) ? w : writes[from] = []).Add(target);
        }
        foreach (var f in (root["findings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kind = (string?)f["kind"];
            var node = (string?)f["nodeId"] ?? "";
            var title = (string?)f["title"] ?? "";
            if (kind == "DeadCode" && kinds.GetValueOrDefault(node) == "Table") ev.DeadTables[names[node]] = title;
            if (kind != "Rule") continue;
            // A rule belongs to the columns its method writes whose words it names (DOB: "birth date"); else to the table written.
            var ruleWords = Names.TextWords(title.Replace('/', ' '));
            var written = writes.GetValueOrDefault(node) ?? [];
            var hits = written.Where(c =>
            {
                var parts = c.Split('.');
                var cw = Names.ColumnWords(parts[1], parts[0]).Where(w => !w.StartsWith('@') && w is not "id" and not "code").ToList();
                return cw.Count > 0 && cw.All(ruleWords.Contains);
            }).ToList();
            if (hits.Count == 0) hits = [.. written.Select(c => c.Split('.')[0]).Distinct()];
            if (hits.Count == 0 && kinds.GetValueOrDefault(node) is "Table" or "Column") hits = [names[node]];
            foreach (var h in hits)
                if (ev._items.TryGetValue(h, out var item) && !item.Rules.Contains(title)) item.Rules.Add(title);
        }
        return ev;
    }
}
