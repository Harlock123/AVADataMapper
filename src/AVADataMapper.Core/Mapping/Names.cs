using System.Text.RegularExpressions;

namespace AVADataMapper.Core.Mapping;

/// <summary>
/// Names as words, so that PatID, patient_id and PATIENT_ID are the same, DOB is a birth date, VisitDt and visit_at both
/// say when, and ClaimNo is a claim number. Used to suggest which old table and column fill a new one.
/// </summary>
public static partial class Names
{
    /// <summary>Abbreviations seen in old schemas, and what they stand for.</summary>
    private static readonly Dictionary<string, string[]> Expand = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pat"] = ["patient"], ["pt"] = ["patient"], ["dob"] = ["birth", "date"], ["bdate"] = ["birth", "date"], ["amt"] = ["amount"],
        ["chg"] = ["charge"], ["proc"] = ["procedure"], ["no"] = ["number"], ["num"] = ["number"], ["nbr"] = ["number"], ["nr"] = ["number"],
        ["pct"] = ["percent"], ["perc"] = ["percent"], ["cd"] = ["code"], ["qty"] = ["quantity"], ["desc"] = ["description"], ["descr"] = ["description"],
        ["addr"] = ["address"], ["ph"] = ["phone"], ["tel"] = ["phone"], ["fname"] = ["first", "name"], ["lname"] = ["last", "name"], ["mi"] = ["middle", "initial"],
        ["dx"] = ["diagnosis"], ["rx"] = ["prescription"], ["prov"] = ["provider"], ["doc"] = ["document"], ["emp"] = ["employee"], ["cust"] = ["customer"],
        ["acct"] = ["account"], ["dept"] = ["department"], ["mgr"] = ["manager"], ["svc"] = ["service"], ["ins"] = ["insurance"], ["zip"] = ["postal", "code"],
        ["st"] = ["state"], ["yr"] = ["year"], ["mo"] = ["month"], ["seq"] = ["sequence"], ["stat"] = ["status"], ["sts"] = ["status"],
        ["fax"] = ["fax"], ["sex"] = ["sex"], ["gender"] = ["sex"],
    };

    /// <summary>Words that mean the same in column names, and the one they're read as.</summary>
    private static readonly Dictionary<string, string> Same = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dt"] = "@when", ["date"] = "@when", ["at"] = "@when", ["on"] = "@when", ["time"] = "@when", ["ts"] = "@when", ["timestamp"] = "@when", ["datetime"] = "@when",
        ["entered"] = "@made", ["created"] = "@made", ["added"] = "@made", ["inserted"] = "@made",
        ["surname"] = "last", ["family"] = "last", ["forename"] = "first", ["given"] = "first",
        ["identifier"] = "id", ["key"] = "id", ["flag"] = "is",
    };

    /// <summary>Words that say little on their own: they count for less.</summary>
    private static readonly HashSet<string> Light = new(StringComparer.OrdinalIgnoreCase) { "id", "code", "number", "name", "is", "@when", "by", "type", "value", "text", "flag" };

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex Pieces();

    /// <summary>A name's words: prefixes such as tbl dropped, camel case and underscores split, abbreviations spelled out, plurals made singular.</summary>
    public static List<string> Words(string name, bool synonyms = true)
    {
        var n = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        n = TablePrefix().Replace(n, "");
        var words = Spelled(n);
        // "is" and "has" before a flag's name (is_active) say nothing the name doesn't.
        if (words.Count > 1 && words[0] is "is" or "has") words.RemoveAt(0);
        return [.. words.Select(w => synonyms && Same.TryGetValue(w, out var s) ? s : w)];
    }

    /// <summary>The words of a sentence (a rule's text), spelled out as names are, synonyms read the same.</summary>
    public static HashSet<string> TextWords(string text) => [.. Spelled(text).Select(w => Same.TryGetValue(w, out var s) ? s : w)];

    private static List<string> Spelled(string n)
    {
        var words = new List<string>();
        foreach (Match m in Pieces().Matches(n))
        {
            var w = m.Value.ToLowerInvariant();
            if (Expand.TryGetValue(w, out var e)) words.AddRange(e);
            else words.Add(w);
        }
        return [.. words.Select(Singular)];
    }

    /// <summary>Whether two names are the same once spelled out, without counting synonyms (EnteredBy isn't created_by).</summary>
    public static bool SameName(string a, string b, string tableA, string tableB)
    {
        static List<string> Plain(string c, string t)
        {
            var w = Words(c, synonyms: false);
            var tw = Words(t, synonyms: false).ToHashSet();
            var trimmed = w.Where(x => !tw.Contains(x)).ToList();
            return trimmed.Count > 0 ? trimmed : w;
        }
        return Plain(a, tableA).SequenceEqual(Plain(b, tableB));
    }

    // A prefix only when a capital or an underscore follows (tblPatient, tbl_patient), so visit and tags keep their first letters.
    [GeneratedRegex(@"^(tbl|tb|vw)(?=[A-Z])|^(?i:tbl_|tb_|vw_|t_)")]
    private static partial Regex TablePrefix();

    private static string Singular(string w) =>
        w.Length > 3 && w.EndsWith("ies", StringComparison.Ordinal) ? w[..^3] + "y"
        : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal) ? w[..^1]
        : w;

    /// <summary>
    /// A column's words without its table's (PayerName in tblPayer is a name; payer_code in payer stays payer code if
    /// that's all it has), so columns compare across tables named differently.
    /// </summary>
    public static List<string> ColumnWords(string column, string table)
    {
        var words = Words(column);
        var t = Words(table).ToHashSet();
        var trimmed = words.Where(w => !t.Contains(w)).ToList();
        return trimmed.Count > 0 ? trimmed : words;
    }

    /// <summary>How alike two names' words are, from 0 to 1: shared words over all, light words counting less.</summary>
    public static double Similarity(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        static double W(string w) => Light.Contains(w) ? 0.35 : 1;
        var sa = a.ToHashSet();
        var sb = b.ToHashSet();
        var shared = sa.Intersect(sb).Sum(W);
        var all = sa.Union(sb).Sum(W);
        return all == 0 ? 0 : shared / all;
    }

    public static double Similarity(string a, string b) => Similarity(Words(a), Words(b));
}
