using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Mapping;

/// <summary>
/// Suggests a mapping: which old table fills each new one, and how each new column is filled, each suggestion marked
/// sure, likely or guessed with its reason. It only fills what the mapping doesn't say yet: nothing a person confirmed
/// (or that was suggested before) is changed. Suggestions come from names (as <see cref="Names"/> reads them), types,
/// foreign keys, and, when there's an AVALegacyLens scan, what the old code says (its rules, in the original's words).
/// </summary>
public static partial class Suggester
{
    public sealed record Result(int Tables, int Columns, List<string> Notes);

    /// <summary>Fills the mapping's gaps with suggestions; says how many tables and columns it suggested.</summary>
    public static Result Suggest(MapFile map, DatabaseSchema old, DatabaseSchema neu, Evidence? evidence = null)
    {
        int tables = 0, columns = 0;
        var notes = new List<string>();
        // Tables: each new table's best old table by name and by how many of its columns match; code tables stay given.
        var taken = map.Tables.Where(t => t.From != null).Select(t => old.Table(t.From!)?.Name).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scored = new List<(Table New, Table Old, double Score)>();
        foreach (var nt in neu.Tables.Where(t => map.Table(t.Name) == null))
            foreach (var ot in old.Tables)
                scored.Add((nt, ot, TableScore(nt, ot)));
        foreach (var (nt, ot, score) in scored.OrderByDescending(s => s.Score))
        {
            if (map.Table(nt.Name) != null || taken.Contains(ot.Name) || score < 0.45) continue;
            map.Tables.Add(new TableMap
            {
                Target = nt.Name, From = ot.Name, Certainty = score >= 0.95 ? Certainty.Sure : score >= 0.7 ? Certainty.Likely : Certainty.Guessed,
                Reason = $"{ot.Name} is named like {nt.Name}" + (score < 0.95 ? $" ({score:P0} alike, names and columns)" : ""),
            });
            taken.Add(ot.Name);
            tables++;
        }
        foreach (var nt in neu.Tables.Where(t => map.Table(t.Name) == null))
        {
            var referrers = neu.Tables.SelectMany(t => t.ForeignKeys.Where(f => neu.Table(f.RefTable) == nt).Select(f => $"{t.Name}.{f.Columns[0]}")).ToList();
            if (referrers.Count > 0 && nt.Columns.Count <= 4)
            {
                map.Tables.Add(new TableMap
                {
                    Target = nt.Name, Rows = [], Certainty = Certainty.Likely,
                    Columns = [.. nt.Columns.Where(c => !c.Identity).Select(c => new ColumnMap { Target = c.Name, Kind = ColumnKind.Direct, From = c.Name, Certainty = Certainty.Likely, Reason = "From the rows given." })],
                    Reason = $"A code table ({string.Join(", ", referrers)} refer to it), with no old table like it: its rows are given. Profile the old columns that fill {string.Join(", ", referrers)} for its codes.",
                });
                tables++;
            }
            else notes.Add($"No old table looks like {nt.Name}: map it by hand.");
        }
        // Columns, table by table, in load order so keys can follow the tables they point at.
        foreach (var nt in neu.LoadOrder())
        {
            var tm = map.Table(nt.Name);
            if (tm == null) continue;
            var ot = tm.From == null ? null : old.Table(tm.From);
            foreach (var c in nt.Columns)
            {
                if (tm.Column(c.Name) != null || tm.From == null && !c.Identity) continue;
                var cm = Column(map, neu, nt, c, ot, old, evidence);
                if (cm == null) continue;
                tm.Columns.Add(cm);
                columns++;
            }
            tm.Columns = [.. tm.Columns.OrderBy(x => nt.Column(x.Target)?.Ordinal ?? int.MaxValue)];
        }
        return new Result(tables, columns, notes);
    }

    private static double TableScore(Table nt, Table ot)
    {
        var name = Names.Similarity(Names.Words(nt.Name), Names.Words(ot.Name));
        var matched = nt.Columns.Count(nc => ot.Columns.Any(oc => Names.Similarity(Names.ColumnWords(nc.Name, nt.Name), Names.ColumnWords(oc.Name, ot.Name)) >= 0.75));
        var cols = nt.Columns.Count == 0 ? 0 : (double)matched / nt.Columns.Count;
        return name >= 0.99 ? Math.Max(0.95, 0.7 + cols * 0.3) : name * 0.7 + cols * 0.3;
    }

    private static ColumnMap? Column(MapFile map, DatabaseSchema neu, Table nt, Column c, Table? ot, DatabaseSchema old, Evidence? evidence)
    {
        if (c.Identity) return new ColumnMap { Target = c.Name, Kind = ColumnKind.Generated, Certainty = Certainty.Sure, Reason = "The new database numbers it (identity)." };
        if (ot == null) return null;
        var nw = Names.ColumnWords(c.Name, nt.Name);

        // A foreign key: through the key map of the table it points at, or a code looked up in a code table.
        var fk = nt.ForeignKeys.FirstOrDefault(f => f.Columns.Count == 1 && f.Columns[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase));
        if (fk != null && neu.Table(fk.RefTable) is { } rt && map.Table(rt.Name) is { } rtm)
        {
            if (rtm.From != null && old.Table(rtm.From) is { } rot)
            {
                var byFk = ot.ForeignKeys.FirstOrDefault(f => f.Columns.Count == 1 && old.Table(f.RefTable) == rot);
                if (byFk != null)
                    return new ColumnMap { Target = c.Name, Kind = ColumnKind.Key, From = byFk.Columns[0], Table = rt.Name, Certainty = Certainty.Sure, Reason = $"{ot.Name}.{byFk.Columns[0]} is a foreign key to {rot.Name}, which fills {rt.Name}." };
                var key = rot.PrimaryKey.Count == 1 ? rot.Column(rot.PrimaryKey[0]) : null;
                var named = ot.Columns.Where(oc => key != null && Fits(oc.Type, key.Type))
                    .Select(oc => (oc, s: Math.Max(Names.Similarity(Names.Words(oc.Name), Names.Words(key!.Name)), Names.Similarity(Names.ColumnWords(oc.Name, ot.Name), nw))))
                    .OrderByDescending(x => x.s).FirstOrDefault();
                if (named.oc != null && named.s >= 0.6)
                    return new ColumnMap { Target = c.Name, Kind = ColumnKind.Key, From = named.oc.Name, Table = rt.Name, Certainty = Certainty.Likely, Reason = $"No foreign key in the old database, but {ot.Name}.{named.oc.Name} is named like {rot.Name}'s key {key!.Name}." };
            }
            else if (rtm.From == null)
            {
                var code = Best(ot, nw, c, ignoreTypes: true);
                if (code.Column != null && code.Score >= 0.5)
                {
                    var cm = new ColumnMap { Target = c.Name, Kind = ColumnKind.Lookup, From = code.Column.Name, Table = rt.Name, Trim = code.Column.Type.IsText ? true : null, Unknown = "reject", Certainty = Certainty.Likely, Reason = $"{rt.Name} is a code table; {ot.Name}.{code.Column.Name} holds the codes." };
                    if (Codes(evidence?.Column(ot.Name, code.Column.Name)) is { Count: > 0 } codes)
                    {
                        cm.Map = codes.ToDictionary(k => k, k => (JsonNode?)JsonValue.Create(k));
                        cm.Reason += $" The old code allows {string.Join(", ", codes)}.";
                    }
                    return cm;
                }
            }
        }

        // A name the old table has: copied, or converted when the types differ.
        var (best, score) = Best(ot, nw, c, ignoreTypes: true);
        if (best != null && score >= 0.75)
        {
            var rules = evidence?.Column(ot.Name, best.Name)?.Rules ?? [];
            var same = score >= 0.99 && Names.SameName(best.Name, c.Name, ot.Name, nt.Name);
            var certainty = same ? Certainty.Sure : Certainty.Likely;
            if (Fits(best.Type, c.Type))
                return new ColumnMap { Target = c.Name, Kind = ColumnKind.Direct, From = best.Name, Certainty = certainty, Reason = Why(ot, best, c, score, same) + Narrower(best, c) };
            var @as = c.Type.Kind switch
            {
                TypeKind.Date => "date", TypeKind.DateTime => "datetime", TypeKind.Boolean or TypeKind.Bit => "bool",
                TypeKind.SmallInteger or TypeKind.Integer or TypeKind.BigInteger => "integer", _ when c.Type.IsNumber => "number", _ => "text",
            };
            var cm = new ColumnMap { Target = c.Name, Kind = ColumnKind.Convert, From = best.Name, As = @as, Certainty = Certainty.Likely, Reason = Why(ot, best, c, score, same) + $" Its type ({best.Type.Declared}) isn't {c.Type.Declared}: converted." };
            if (@as is "date" or "datetime" && best.Type.IsText)
            {
                var formats = rules.SelectMany(DateFormats).Distinct().ToList();
                cm.Formats = formats.Count > 0 ? formats : null;
                cm.BlankIsNull = true;
                cm.Unknown = "reject";
                cm.Reason += formats.Count > 0 ? $" The old code asks for {string.Join(", ", formats)} (\"{rules.First(r => DateFormats(r).Any())}\"); profile it for others." : " Profile it to find the formats it holds.";
            }
            if (@as == "bool" && best.Type.IsText && best.Default is { } d && d.Trim('\'', '(', ')').ToUpperInvariant() is "Y" or "N")
            {
                cm.Map = new Dictionary<string, JsonNode?> { ["Y"] = true, ["N"] = false };
                cm.Unknown = "reject";
                cm.Reason += " It holds Y or N (its default is " + d + ").";
            }
            return cm;
        }

        // A first or last name from an old column holding both.
        var part = nw.Contains("last") ? 1 : nw.Contains("first") ? 2 : 0;
        if (part > 0 && c.Type.IsText)
        {
            var whole = ot.Columns.FirstOrDefault(oc => oc.Type.IsText && Names.ColumnWords(oc.Name, ot.Name) is var w && w.Contains("name") && !w.Contains("last") && !w.Contains("first"));
            if (whole != null)
            {
                var rule = evidence?.Column(ot.Name, whole.Name)?.Rules.FirstOrDefault(r => LastFirst().IsMatch(r));
                return new ColumnMap
                {
                    Target = c.Name, Kind = ColumnKind.Split, From = whole.Name, Separator = ",", Part = part, Trim = true,
                    Certainty = rule != null ? Certainty.Likely : Certainty.Guessed,
                    Reason = rule != null ? $"The old code stores both names in {whole.Name}: \"{rule}\"" : $"{ot.Name} has one name column, {whole.Name}; \"LAST, FIRST\" is a guess: profile it.",
                };
            }
        }
        return null;
    }

    private static (Column? Column, double Score) Best(Table ot, List<string> words, Column target, bool ignoreTypes) =>
        ot.Columns.Where(oc => ignoreTypes || Fits(oc.Type, target.Type))
            .Select(oc => (oc, Names.Similarity(Names.ColumnWords(oc.Name, ot.Name), words)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.oc.Ordinal).FirstOrDefault();

    private static string Why(Table ot, Column oc, Column c, double score, bool same) =>
        same ? $"{ot.Name}.{oc.Name} is the same name." : score >= 0.99 ? $"{ot.Name}.{oc.Name} means the same ({string.Join(" ", Names.Words(oc.Name, false))} ~ {string.Join(" ", Names.Words(c.Name, false))})." : $"{ot.Name}.{oc.Name} is named like it ({score:P0} alike).";

    private static string Narrower(Column oc, Column c) =>
        oc.Type.IsText && c.Type.IsText && c.Type.Length is > 0 and var n && (oc.Type.Length is -1 || oc.Type.Length > n) ? $" The new column is shorter ({oc.Type.Length switch { -1 => "max", var l => l.ToString() }} → {n}): check the longest values." : "";

    /// <summary>Whether an old type's values go into a new type as they are (text into text, numbers into numbers, dates into dates).</summary>
    public static bool Fits(DataType from, DataType to) =>
        from.Kind == to.Kind
        || from.IsText && to.IsText
        || from.IsWholeNumber && to.IsNumber
        || from.IsNumber && to.Kind is TypeKind.Decimal or TypeKind.Money or TypeKind.Float
        || from.Kind is TypeKind.Date or TypeKind.DateTime && to.Kind is TypeKind.Date or TypeKind.DateTime
        || from.Kind is TypeKind.Bit or TypeKind.Boolean && to.Kind is TypeKind.Bit or TypeKind.Boolean
        || to.Kind == TypeKind.Other;

    [GeneratedRegex(@"\bLAST\s*,\s*FIRST\b", RegexOptions.IgnoreCase)]
    private static partial Regex LastFirst();

    /// <summary>Date formats a rule asks for (MM/DD/YYYY → MM/dd/yyyy).</summary>
    public static IEnumerable<string> DateFormats(string rule)
    {
        foreach (Match m in DatePattern().Matches(rule))
            yield return m.Value.Replace("DD", "dd").Replace("YYYY", "yyyy").Replace("YY", "yy");
    }

    [GeneratedRegex(@"\b(MM[/\-.]DD[/\-.]YYYY|DD[/\-.]MM[/\-.]YYYY|YYYY[/\-.]MM[/\-.]DD|YYYYMMDD|MM[/\-.]DD[/\-.]YY)\b")]
    private static partial Regex DatePattern();

    /// <summary>Codes a column's rules list: "Sex is M, F or U"; "decides by status: \"OPEN\" or \"SENT\"…".</summary>
    private static List<string> Codes(ColumnEvidence? e)
    {
        foreach (var r in e?.Rules ?? [])
        {
            var quoted = Quoted().Matches(r).Select(m => m.Groups[1].Value).ToList();
            if (quoted.Count >= 2) return quoted;
            var listed = ListedCodes().Match(r);
            if (listed.Success) return [.. Regex.Split(listed.Groups[1].Value, @"\s*,\s*|\s+or\s+").Where(x => x.Length > 0)];
        }
        return [];
    }

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\bis\s+((?:[A-Z0-9]{1,8}\s*,\s*)+[A-Z0-9]{1,8}\s+or\s+[A-Z0-9]{1,8})\b")]
    private static partial Regex ListedCodes();
}
