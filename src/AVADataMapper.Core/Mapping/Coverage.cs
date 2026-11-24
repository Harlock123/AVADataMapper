using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Mapping;

/// <summary>One line of the coverage report: what it's about, what it says, and the evidence for it.</summary>
public sealed record CoverageItem(string Section, string About, string Text, string? Evidence = null);

/// <summary>
/// The coverage report: every new table and column filled or not (a required one with nothing to fill it is a problem),
/// the suggestions not yet confirmed, and every old table and column the mapping doesn't read, with what the old code
/// says about it and whether a person has decided to drop it.
/// </summary>
public static class Coverage
{
    public const string Required = "required, not filled";
    public const string Suggested = "suggested, not confirmed";
    public const string Unread = "old, not read";
    public const string Dropped = "old, dropped";
    public const string Unmapped = "new table, not mapped";

    public static List<CoverageItem> Of(MapFile map, DatabaseSchema? old, DatabaseSchema? neu, Evidence? evidence = null)
    {
        var items = new List<CoverageItem>();
        if (neu != null)
            foreach (var t in neu.Tables)
            {
                var tm = map.Table(t.Name);
                if (tm == null) { items.Add(new CoverageItem(Unmapped, t.Name, "Nothing fills it.")); continue; }
                if (tm.Certainty != Certainty.Confirmed) items.Add(new CoverageItem(Suggested, t.Name, $"{(tm.From != null ? $"Rows from {tm.From}" : "Rows given")} ({tm.Certainty.ToString().ToLowerInvariant()}): {tm.Reason}"));
                foreach (var c in t.Columns)
                {
                    var cm = tm.Column(c.Name);
                    if (cm == null && !c.Identity && !c.Nullable && c.Default == null)
                        items.Add(new CoverageItem(Required, $"{t.Name}.{c.Name}", $"{c.Type.Declared}, required, and nothing fills it."));
                    else if (cm != null && cm.Certainty != Certainty.Confirmed)
                        items.Add(new CoverageItem(Suggested, $"{t.Name}.{c.Name}", $"{cm.Kind.ToString().ToLowerInvariant()} ({cm.Certainty.ToString().ToLowerInvariant()}): {cm.Reason}"));
                }
            }
        if (old != null)
        {
            var decided = map.Decisions.ToDictionary(d => d.About, d => d, StringComparer.OrdinalIgnoreCase);
            var fromTables = map.Tables.Where(t => t.From != null).Select(t => old.Table(t.From!)?.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in old.Tables)
            {
                var ev = evidence?.Table(t.Name);
                if (!fromTables.Contains(t.Name))
                {
                    if (decided.TryGetValue(t.Name, out var d)) items.Add(new CoverageItem(Dropped, t.Name, d.Said));
                    else items.Add(new CoverageItem(Unread, t.Name, $"No new table's rows come from it ({t.Columns.Count} columns).", Said(ev, evidence?.DeadTables.GetValueOrDefault(t.Name))));
                    continue;
                }
                var read = map.Tables.Where(m => m.From != null && old.Table(m.From) == t)
                    .SelectMany(m => m.Columns.SelectMany(c => c.Reads).Concat(m.Where != null ? Transforms.ExpressionColumns(m.Where) : []).Concat(m.OldKey ?? t.PrimaryKey))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var c in t.Columns.Where(c => !read.Contains(c.Name)))
                {
                    var about = $"{t.Name}.{c.Name}";
                    if (decided.TryGetValue(about, out var d)) items.Add(new CoverageItem(Dropped, about, d.Said));
                    else items.Add(new CoverageItem(Unread, about, $"{c.Type.Declared}: no new column reads it.", Said(evidence?.Column(t.Name, c.Name), null)));
                }
            }
        }
        return items;
    }

    private static string? Said(ColumnEvidence? e, string? dead) =>
        dead != null ? dead
        : e == null ? null
        : e.Unused ? "No code in the old system reads or writes it."
        : $"Written by {(e.Writers.Count > 0 ? string.Join(", ", e.Writers) : "nothing")}; read by {(e.Readers.Count > 0 ? string.Join(", ", e.Readers) : "nothing")}.";
}
