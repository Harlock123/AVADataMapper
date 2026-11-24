using System.Data.Common;
using System.Globalization;
using System.Text;
using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Data;

/// <summary>One old column's profile: counts, lengths, value shapes, the commonest values, and what it suggests.</summary>
public sealed class ColumnProfile
{
    public required string Name { get; init; }
    public required DataType Type { get; init; }
    public long Nulls { get; set; }
    public long Blanks { get; set; }
    public long Values { get; set; }

    /// <summary>Distinct values, counted up to <see cref="Profiler.DistinctCap"/> (more: <see cref="DistinctCapped"/>).</summary>
    public int Distinct => _counts.Count;
    public bool DistinctCapped { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? Longest { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }

    /// <summary>The most decimal places a number has.</summary>
    public int? Scale { get; set; }

    /// <summary>Shapes (digits as 9, letters as A or a) and how many values have each, commonest first.</summary>
    public List<(string Shape, long Count, string Example)> Shapes { get; set; } = [];

    /// <summary>The commonest values and how often each comes.</summary>
    public List<(string Value, long Count)> Top { get; set; } = [];

    /// <summary>Date formats the text's shapes suggest, and the values none of them reads.</summary>
    public List<string> DateFormats { get; set; } = [];
    public List<string> NotDates { get; set; } = [];

    internal readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, (long Count, string Example)> _shapes = new(StringComparer.Ordinal);
}

public sealed class TableProfile
{
    public required string Table { get; init; }
    public long Rows { get; set; }
    public List<ColumnProfile> Columns { get; } = [];
    public ColumnProfile? Column(string name) => Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Profiles an old table's rows: for each column, nulls and blanks, lengths, how many distinct values, their shapes
/// ("99/99/9999"), the commonest values, the range, and, for text that holds dates, the formats it's in and the values
/// that aren't dates in any of them. The mapping uses it: formats for a conversion, codes for a code table, lengths
/// against the new column's.
/// </summary>
public static class Profiler
{
    public const int DistinctCap = 20_000;

    public static TableProfile Profile(DbConnection c, Dialect d, Table t, CancellationToken cancel = default) =>
        Profile(t, Rows.Read(c, d, t, cancel));

    public static TableProfile Profile(Table t, IEnumerable<Row> rows)
    {
        var p = new TableProfile { Table = t.Name };
        foreach (var col in t.Columns) p.Columns.Add(new ColumnProfile { Name = col.Name, Type = col.Type });
        decimal?[] min = new decimal?[t.Columns.Count], max = new decimal?[t.Columns.Count];
        string?[] smin = new string?[t.Columns.Count], smax = new string?[t.Columns.Count];
        foreach (var row in rows)
        {
            p.Rows++;
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var cp = p.Columns[i];
                var v = row[cp.Name];
                if (v == null) { cp.Nulls++; continue; }
                if (v is byte[]) { cp.Values++; continue; }
                var s = Expression.Str(v)!;
                if (v is string && s.Trim().Length == 0) { cp.Blanks++; continue; }
                cp.Values++;
                if (cp._counts.Count < DistinctCap || cp._counts.ContainsKey(s)) cp._counts[s] = cp._counts.GetValueOrDefault(s) + 1;
                else cp.DistinctCapped = true;
                if (v is string)
                {
                    cp.MinLength = Math.Min(cp.MinLength ?? int.MaxValue, s.Length);
                    if (s.Length > (cp.MaxLength ?? -1)) { cp.MaxLength = s.Length; cp.Longest = s; }
                    var shape = Shape(s);
                    cp._shapes[shape] = cp._shapes.TryGetValue(shape, out var x) ? (x.Count + 1, x.Example) : (1, s);
                    if (smin[i] == null || string.CompareOrdinal(s, smin[i]) < 0) smin[i] = s;
                    if (smax[i] == null || string.CompareOrdinal(s, smax[i]) > 0) smax[i] = s;
                }
                else if (v is DateTime dt)
                {
                    if (smin[i] == null || string.CompareOrdinal(s, smin[i]) < 0) smin[i] = s;
                    if (smax[i] == null || string.CompareOrdinal(s, smax[i]) > 0) smax[i] = s;
                    _ = dt;
                }
                else if (Expression.Number(v) is { } n)
                {
                    min[i] = min[i] is { } a ? Math.Min(a, n) : n;
                    max[i] = max[i] is { } b ? Math.Max(b, n) : n;
                    var scale = (decimal.GetBits(n)[3] >> 16) & 0xFF;
                    if (v is double dbl) scale = Places(dbl);
                    cp.Scale = Math.Max(cp.Scale ?? 0, scale);
                }
            }
        }
        for (var i = 0; i < t.Columns.Count; i++)
        {
            var cp = p.Columns[i];
            cp.Min = min[i]?.ToString(CultureInfo.InvariantCulture) ?? smin[i];
            cp.Max = max[i]?.ToString(CultureInfo.InvariantCulture) ?? smax[i];
            cp.Top = [.. cp._counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12).Select(kv => (kv.Key, kv.Value))];
            cp.Shapes = [.. cp._shapes.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value.Count, kv.Value.Example))];
            if (cp.Type.IsText) Dates(cp);
        }
        return p;
    }

    private static int Places(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        var dot = s.IndexOf('.');
        return dot < 0 || s.Contains('E') ? 0 : s.Length - dot - 1;
    }

    /// <summary>A value's shape: digits as 9, capitals as A, small letters as a, runs of spaces as one; anything else as it is.</summary>
    public static string Shape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = char.IsDigit(ch) ? '9' : char.IsUpper(ch) ? 'A' : char.IsLower(ch) ? 'a' : ch;
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }
        return sb.Length > 40 ? sb.ToString(0, 39) + "…" : sb.ToString();
    }

    /// <summary>When most of a text column's shapes look like dates, the formats they're in, and the values no format reads.</summary>
    private static void Dates(ColumnProfile cp)
    {
        var dateLike = cp.Shapes.Where(s => DateShape(s.Shape)).ToList();
        if (cp.Values == 0 || dateLike.Sum(s => s.Count) < cp.Values * 0.8) return;
        // Which of two number groups is the month: a group over 12 anywhere is the day.
        var values = cp._counts.Keys.ToList();
        var formats = new List<string>();
        foreach (var (shape, _, _) in dateLike)
        {
            if (shape == "99999999") { formats.Add("yyyyMMdd"); continue; }
            var sep = shape.First(ch => ch is '/' or '-' or '.');
            var groups = shape.Split(sep);
            if (groups[0].Length == 4) { formats.Add($"yyyy{sep}MM{sep}dd"); continue; }
            var these = values.Where(v => Shape(v) == shape).Select(v => v.Split(sep)).Where(g => g.Length == 3).ToList();
            var firstOver12 = these.Any(g => int.TryParse(g[0], out var a) && a > 12 && !(int.TryParse(g[1], out var b) && b > 12));
            var variable = groups[0].Length == 1 || groups[1].Length == 1;
            var (m, d) = variable ? ("M", "d") : ("MM", "dd");
            formats.Add(firstOver12 ? $"{d}{sep}{m}{sep}yyyy" : $"{m}{sep}{d}{sep}yyyy");
        }
        cp.DateFormats = [.. formats.Distinct().OrderBy(f => f.Length == 10 && f.StartsWith("MM", StringComparison.Ordinal) ? 0 : f.StartsWith("yyyy", StringComparison.Ordinal) ? 1 : 2).ThenBy(f => f, StringComparer.Ordinal)];
        cp.NotDates = [.. values.Where(v => Transforms.ParseDate(v, cp.DateFormats) == null).Order(StringComparer.Ordinal).Take(50)];
    }

    private static bool DateShape(string shape) =>
        shape == "99999999"
        || System.Text.RegularExpressions.Regex.IsMatch(shape, @"^(9{1,2}([/\-.])9{1,2}\2(9999)|9999([/\-.])99\4(99))$");
}
