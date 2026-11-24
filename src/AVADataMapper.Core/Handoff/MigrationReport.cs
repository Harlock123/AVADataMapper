using System.Net;
using System.Text;
using AVADataMapper.Core.Data;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;
using ClosedXML.Excel;

namespace AVADataMapper.Core.Handoff;

/// <summary>
/// The migration's sign-off report, as an HTML page (one file, no scripts) or an Excel workbook: the two databases, how
/// every new column is filled and why, the decisions made, what was dropped, the checks, and the last dry run or
/// migration with every row it rejected and why. Rejected rows show their old keys, not their values: the report can be
/// passed round without carrying the data.
/// </summary>
public static class MigrationReport
{
    private static string H(string s) => WebUtility.HtmlEncode(s);

    public static string Html(MapFile map, DatabaseSchema old, DatabaseSchema neu, IReadOnlyList<TableRun>? runs, string runKind, Evidence? evidence, DateTime made)
    {
        var findings = MapChecks.Check(map, old, neu);
        var coverage = Coverage.Of(map, old, neu, evidence);
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Migration report</title><style>
            body{font:14px/1.45 system-ui,sans-serif;margin:24px;color:#1d2430;background:#fff}h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}h3{font-size:15px;margin:18px 0 6px}
            .dim{color:#5b6575}table{border-collapse:collapse;width:100%;margin:6px 0 14px}th,td{border-bottom:1px solid #dde2ea;padding:5px 8px;text-align:left;vertical-align:top}
            th{background:#f2f4f8;font-weight:600}td.n{text-align:right}code{font:12.5px ui-monospace,monospace}.bad{color:#b3261e;font-weight:600}.warn{color:#a85400}.ok{color:#1d7a3a;font-weight:600}
            .sign td{height:34px}
            </style></head><body>
            """);
        sb.Append($"<h1>Migration report: {H(map.Name ?? "a mapping")}</h1>");
        sb.Append($"<p class=\"dim\">Made {made:yyyy-MM-dd HH:mm} by AVA Data Mapper. From <b>{H(old.Source)}</b> ({old.Dialect}, {old.Tables.Count} tables) to <b>{H(neu.Source)}</b> ({neu.Dialect}, {neu.Tables.Count} tables).</p>");
        if (runs != null)
        {
            sb.Append($"<h2>The {H(runKind)}</h2><table><tr><th>New table</th><th>From</th><th>Read</th><th>Filtered</th><th>Already in</th><th>In</th><th>Rejected</th><th>Their own</th></tr>");
            foreach (var r in runs)
                sb.Append($"<tr><td><code>{H(r.Target)}</code></td><td>{H(r.From ?? "rows given")}</td><td class=\"n\">{r.Read:N0}</td><td class=\"n\">{r.Filtered:N0}</td><td class=\"n\">{r.Skipped:N0}</td><td class=\"n ok\">{r.Inserted:N0}</td><td class=\"n{(r.Rejected > 0 ? " bad" : "")}\">{r.Rejected:N0}</td><td class=\"n\">{r.Rejected - r.Followed:N0}</td></tr>");
            sb.Append($"<tr><th>All</th><th></th><th class=\"n\">{runs.Sum(r => r.Read):N0}</th><th class=\"n\">{runs.Sum(r => r.Filtered):N0}</th><th class=\"n\">{runs.Sum(r => r.Skipped):N0}</th><th class=\"n\">{runs.Sum(r => r.Inserted):N0}</th><th class=\"n\">{runs.Sum(r => r.Rejected):N0}</th><th class=\"n\">{runs.Sum(r => r.Rejected - r.Followed):N0}</th></tr></table>");
            sb.Append("<p class=\"dim\">Every row read is in, filtered out, already in from an earlier run, or rejected: the counts add up.</p>");
            foreach (var r in runs.Where(r => r.Rejected > 0))
            {
                sb.Append($"<h3>Rejected from {H(r.Target)}</h3><table><tr><th>Old key</th><th>Why</th></tr>");
                foreach (var x in r.Rejects.Where(x => Engine.Root(x.Reasons[0]) == null)) sb.Append($"<tr><td><code>{H(r.From ?? "")} {H(x.OldKey)}</code></td><td>{H(string.Join("; ", x.Reasons))}</td></tr>");
                foreach (var g in r.Rejects.Where(x => Engine.Root(x.Reasons[0]) != null).GroupBy(x => Engine.Root(x.Reasons[0])!))
                    sb.Append($"<tr><td><code>{H(string.Join(", ", g.Select(x => x.OldKey)))}</code></td><td class=\"dim\">Follow a row that's {H(g.Key)}</td></tr>");
                sb.Append("</table>");
            }
        }
        sb.Append("<h2>How each new table is filled</h2>");
        foreach (var t in neu.LoadOrder())
        {
            var tm = map.Table(t.Name);
            sb.Append($"<h3><code>{H(t.Name)}</code> ← {H(tm == null ? "nothing" : tm.From ?? $"{tm.Rows?.Count ?? 0} given rows")}{(tm?.Where != null ? $" where <code>{H(tm.Where)}</code>" : "")}</h3>");
            if (tm == null) continue;
            sb.Append("<table><tr><th>New column</th><th>Type</th><th>Filled by</th><th>Why</th></tr>");
            foreach (var c in t.Columns)
            {
                var cm = tm.Column(c.Name);
                sb.Append($"<tr><td><code>{H(c.Name)}</code></td><td>{H(c.Type.Declared)}{(c.Nullable ? "" : ", required")}</td><td>{H(cm == null ? (c.Identity ? "numbered by the new database" : "nothing") : $"{cm.Kind.ToString().ToLowerInvariant()}: {MapText.Describe(cm)}")}</td><td class=\"dim\">{H(cm?.Reason ?? "")}</td></tr>");
            }
            sb.Append("</table>");
        }
        var dropped = coverage.Where(i => i.Section is Coverage.Dropped or Coverage.Unread).ToList();
        if (dropped.Count > 0)
        {
            sb.Append("<h2>Not migrated</h2><table><tr><th>Old table or column</th><th>Decided</th><th>Old code</th></tr>");
            foreach (var i in dropped) sb.Append($"<tr><td><code>{H(i.About)}</code></td><td class=\"{(i.Section == Coverage.Unread ? "warn" : "")}\">{H(i.Section == Coverage.Unread ? "Not decided: nothing reads it" : i.Text)}</td><td class=\"dim\">{H(i.Evidence ?? "")}</td></tr>");
            sb.Append("</table>");
        }
        if (map.Decisions.Count > 0)
        {
            sb.Append("<h2>Decisions</h2><table><tr><th>About</th><th>Decided</th><th>By</th><th>When</th></tr>");
            foreach (var d in map.Decisions) sb.Append($"<tr><td><code>{H(d.About)}</code></td><td>{H(d.Said)}</td><td>{H(d.By)}</td><td>{d.At:yyyy-MM-dd}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append($"<h2>Checks</h2>{(findings.Count == 0 ? "<p class=\"ok\">The mapping fits both schemas.</p>" : "")}");
        if (findings.Count > 0)
        {
            sb.Append("<table><tr><th>Rule</th><th>About</th><th>Finding</th></tr>");
            foreach (var f in findings) sb.Append($"<tr><td class=\"{(f.Severity == Severity.Error ? "bad" : f.Severity == Severity.Warning ? "warn" : "dim")}\">{f.Rule.Id} {f.Severity}</td><td><code>{H(f.About)}</code></td><td>{H(f.Message)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<h2>Signed off</h2><table class=\"sign\"><tr><th>Role</th><th>Name</th><th>Date</th></tr><tr><td>Data owner</td><td></td><td></td></tr><tr><td>Rebuild lead</td><td></td><td></td></tr><tr><td>Database administrator</td><td></td><td></td></tr></table>");
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    public static void Excel(string path, MapFile map, DatabaseSchema old, DatabaseSchema neu, IReadOnlyList<TableRun>? runs, Evidence? evidence)
    {
        using var book = new XLWorkbook();
        if (runs != null)
            Table(book.AddWorksheet("Run"), ["New table", "From", "Read", "Filtered", "Already in", "In", "Rejected", "Their own"],
                runs.Select(r => new object[] { r.Target, r.From ?? "rows given", r.Read, r.Filtered, r.Skipped, r.Inserted, r.Rejected, r.Rejected - r.Followed }));
        if (runs != null)
            Table(book.AddWorksheet("Rejected"), ["New table", "Old table", "Old key", "Why", "Follows"],
                runs.SelectMany(r => r.Rejects.Select(x => new object[] { r.Target, r.From ?? "", x.OldKey, string.Join("; ", x.Reasons), Engine.Root(x.Reasons[0]) != null ? "yes" : "" })));
        Table(book.AddWorksheet("Mapping"), ["New table", "From", "Where", "New column", "Type", "Kind", "How", "Certainty", "Why"],
            neu.LoadOrder().SelectMany(t => t.Columns.Select(c => (t, c, tm: map.Table(t.Name)))).Select(x =>
            {
                var cm = x.tm?.Column(x.c.Name);
                return new object[] { x.t.Name, x.tm?.From ?? (x.tm != null ? "rows given" : ""), x.tm?.Where ?? "", x.c.Name, x.c.Type.Declared, cm?.Kind.ToString().ToLowerInvariant() ?? (x.c.Identity ? "generated" : ""), cm == null ? "" : MapText.Describe(cm), cm?.Certainty.ToString().ToLowerInvariant() ?? "", cm?.Reason ?? "" };
            }));
        Table(book.AddWorksheet("Not migrated"), ["Old", "Decided", "Old code"],
            Coverage.Of(map, old, neu, evidence).Where(i => i.Section is Coverage.Dropped or Coverage.Unread).Select(i => new object[] { i.About, i.Section == Coverage.Unread ? "not decided" : i.Text, i.Evidence ?? "" }));
        Table(book.AddWorksheet("Checks"), ["Rule", "Severity", "About", "Finding", "Fix"],
            MapChecks.Check(map, old, neu).Select(f => new object[] { f.Rule.Id, f.Severity.ToString(), f.About, f.Message, f.Fix ?? "" }));
        book.SaveAs(path);
    }

    private static void Table(IXLWorksheet sheet, string[] heads, IEnumerable<object[]> rows)
    {
        for (var i = 0; i < heads.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = heads[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
            sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F4F8");
        }
        var r = 2;
        foreach (var values in rows)
        {
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(r, i + 1).Value = values[i] switch { long n => n, int n => n, decimal d => d, string s => s, var o => o?.ToString() ?? "" };
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, heads.Length).AdjustToContents(1, Math.Min(r, 300));
        foreach (var c in sheet.Columns(1, heads.Length)) if (c.Width > 70) { c.Width = 70; c.Style.Alignment.WrapText = true; }
    }
}
