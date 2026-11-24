using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Mapping;

public enum Severity
{
    Info,
    /// <summary>Some rows may be rejected, or something needs a look.</summary>
    Warning,
    /// <summary>The migration can't run as mapped.</summary>
    Error,
}

/// <summary>A rule the mapping is checked against.</summary>
public sealed record MapRule(string Id, Severity Severity, string Title, string Why);

/// <summary>What a check found: its rule, where (a new table or table.column), what, and the fix when there's one.</summary>
public sealed record MapFinding(MapRule Rule, string About, string Message, string? Fix = null)
{
    public Severity Severity => Rule.Severity;
    public override string ToString() => $"{Rule.Id} {About}: {Message}";
}

/// <summary>
/// The mapping checked against both schemas before any row is read: what can't run (an old column that isn't there, a
/// type that doesn't fit, a key into a table nothing fills), and what will reject rows (text cut short, precision lost,
/// nulls into required columns, keys with no foreign key behind them). The dry run then says which rows.
/// </summary>
public static class MapChecks
{
    public static readonly MapRule M1 = new("M1", Severity.Error, "The old table is there", "A new table's rows come from an old table that the old schema has.");
    public static readonly MapRule M2 = new("M2", Severity.Error, "The old column is there", "A column map reads old columns that the old table has.");
    public static readonly MapRule M3 = new("M3", Severity.Error, "The new column is there", "Every column map fills a column the new table has, once.");
    public static readonly MapRule M4 = new("M4", Severity.Error, "Required columns are filled", "A new column that takes no nulls and has no default must be filled by something.");
    public static readonly MapRule M5 = new("M5", Severity.Error, "Types fit, or are converted", "An old value goes into a new column as it is only when the types fit (text into text, numbers into numbers, dates into dates); otherwise convert it.");
    public static readonly MapRule M6 = new("M6", Severity.Warning, "Text fits its new length", "A new text column shorter than the old one rejects longer values.");
    public static readonly MapRule M7 = new("M7", Severity.Warning, "Numbers keep their precision", "A new number column with fewer digits or decimal places rejects values that don't fit, rather than round them.");
    public static readonly MapRule M8 = new("M8", Severity.Warning, "Nulls have somewhere to go", "An old column that takes nulls, filling a new one that doesn't, rejects the rows with nulls unless it says what a null becomes (ifNull, or the new column's default).");
    public static readonly MapRule M9 = new("M9", Severity.Error, "Keys follow their tables", "A key column goes through the key map of a new table that's filled from an old one, and loaded before it.");
    public static readonly MapRule M10 = new("M10", Severity.Warning, "Keys have a foreign key behind them", "An old column with no foreign key may hold keys of rows that are gone: those rows are rejected.");
    public static readonly MapRule M11 = new("M11", Severity.Error, "Lookups give codes the code table has", "A lookup's new values must be codes in the code table it fills a foreign key to.");
    public static readonly MapRule M12 = new("M12", Severity.Error, "Expressions and filters parse", "An expression or filter is in the mapping's expression language and reads the old table's columns.");
    public static readonly MapRule M13 = new("M13", Severity.Warning, "Unique columns come from unique ones", "A new unique column filled from an old column that isn't unique may reject duplicates.");
    public static readonly MapRule M14 = new("M14", Severity.Info, "Dates from text have formats", "Text converted to a date without formats is read as yyyy-MM-dd only.");
    public static readonly MapRule M15 = new("M15", Severity.Error, "Code tables have their rows", "A code table whose rows are given has rows for its required columns.");

    public static IReadOnlyList<MapRule> Rules => [M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12, M13, M14, M15];

    public static List<MapFinding> Check(MapFile map, DatabaseSchema old, DatabaseSchema neu)
    {
        var f = new List<MapFinding>();
        var order = neu.LoadOrder().Select(t => t.Name).ToList();
        foreach (var tm in map.Tables)
        {
            var nt = neu.Table(tm.Target);
            if (nt == null) { f.Add(new MapFinding(M3, tm.Target, $"The new database has no table {tm.Target}.")); continue; }
            Table? ot = null;
            if (tm.From != null)
            {
                ot = old.Table(tm.From);
                if (ot == null) { f.Add(new MapFinding(M1, nt.Name, $"The old database has no table {tm.From}.")); continue; }
                if (tm.Where != null)
                    try
                    {
                        if (Expression.Parse(tm.Where).Check(ot.Columns.Select(c => c.Name)) is { } bad) f.Add(new MapFinding(M12, nt.Name, $"The filter \"{tm.Where}\": {bad}"));
                    }
                    catch (FormatException e) { f.Add(new MapFinding(M12, nt.Name, $"The filter doesn't parse: {e.Message}")); }
            }
            else if (tm.Rows != null)
            {
                foreach (var c in nt.Columns.Where(c => !c.Identity && !c.Nullable && c.Default == null))
                    if (tm.Rows.Count > 0 && tm.Rows.Any(r => !r.ContainsKey(c.Name) || r[c.Name] == null))
                        f.Add(new MapFinding(M15, $"{nt.Name}.{c.Name}", $"Some of {nt.Name}'s given rows have no {c.Name}, which is required."));
                if (tm.Rows.Count == 0) f.Add(new MapFinding(M15, nt.Name, $"{nt.Name}'s rows are given, and there are none yet.", "Profile the old codes that fill it, and add a row for each."));
            }
            foreach (var g in tm.Columns.GroupBy(c => c.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                f.Add(new MapFinding(M3, $"{nt.Name}.{g.Key}", $"{g.Key} is filled {g.Count()} times."));
            foreach (var cm in tm.Columns)
            {
                var about = $"{nt.Name}.{cm.Target}";
                var nc = nt.Column(cm.Target);
                if (nc == null) { f.Add(new MapFinding(M3, about, $"{nt.Name} has no column {cm.Target}.")); continue; }
                if (tm.From == null || ot == null) continue;
                Column? oc = null;
                foreach (var r in cm.Reads)
                {
                    var c = ot.Column(r);
                    if (c == null && cm.Kind != ColumnKind.Expression) f.Add(new MapFinding(M2, about, $"{ot.Name} has no column {r}."));
                    oc ??= c;
                }
                if (cm.Kind == ColumnKind.Expression)
                {
                    try
                    {
                        if (Expression.Parse(cm.Expression ?? "").Check(ot.Columns.Select(c => c.Name)) is { } bad) f.Add(new MapFinding(M12, about, bad));
                    }
                    catch (FormatException e) { f.Add(new MapFinding(M12, about, e.Message)); }
                }
                if (cm.Kind is ColumnKind.Direct && oc != null && !Suggester.Fits(oc.Type, nc.Type))
                    f.Add(new MapFinding(M5, about, $"{ot.Name}.{oc.Name} is {oc.Type.Declared}; {nc.Name} is {nc.Type.Declared}.", $"Convert it (as {(nc.Type.Kind is TypeKind.Date ? "date" : nc.Type.Kind is TypeKind.DateTime ? "datetime" : nc.Type.Kind is TypeKind.Boolean or TypeKind.Bit ? "bool" : nc.Type.IsNumber ? "number" : "text")})."));
                if (cm.Kind is ColumnKind.Direct or ColumnKind.Split or ColumnKind.Convert && oc != null && oc.Type.IsText && nc.Type.IsText
                    && nc.Type.Length is > 0 and var n && (oc.Type.Length is -1 || oc.Type.Length > n) && cm.Case == null)
                    f.Add(new MapFinding(M6, about, $"{ot.Name}.{oc.Name} holds up to {(oc.Type.Length is -1 ? "any number of" : oc.Type.Length.ToString())} characters; {nc.Name} takes {n}.", "The dry run lists the values that are too long."));
                if (cm.Kind is ColumnKind.Direct or ColumnKind.Convert && oc != null && oc.Type.IsNumber && nc.Type.Kind is TypeKind.Decimal or TypeKind.Money)
                {
                    var (op, os) = (oc.Type.Precision ?? (oc.Type.IsWholeNumber ? 19 : 38), oc.Type.Scale ?? 0);
                    var (np, ns) = (nc.Type.Precision ?? 38, nc.Type.Scale ?? 0);
                    if (os > ns || op - os > np - ns)
                        f.Add(new MapFinding(M7, about, $"{ot.Name}.{oc.Name} is {oc.Type.Declared}; {nc.Name} is {nc.Type.Declared}: {(os > ns ? $"{os} decimal places into {ns}" : $"{op - os} whole digits into {np - ns}")}.", "The dry run lists the values that don't fit."));
                }
                if (oc != null && oc.Nullable && !nc.Nullable && nc.Default == null && cm.IfNull == null && cm.Kind is not (ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated or ColumnKind.Expression or ColumnKind.Merge) && !(cm.Unknown is "default"))
                    f.Add(new MapFinding(M8, about, $"{ot.Name}.{oc.Name} takes nulls; {nc.Name} doesn't.", "Say what a null becomes (ifNull), or let those rows be rejected."));
                if (cm.Kind == ColumnKind.Key)
                {
                    var rt = cm.Table == null ? null : map.Table(cm.Table);
                    if (rt?.From == null) f.Add(new MapFinding(M9, about, $"Its key map is {cm.Table ?? "missing"}'s, and {(rt == null ? "nothing fills that table" : "that table's rows are given, not from an old table")}."));
                    else if (order.IndexOf(neu.Table(rt.Target)?.Name ?? rt.Target) > order.IndexOf(nt.Name)) f.Add(new MapFinding(M9, about, $"{rt.Target} loads after {nt.Name}: its keys aren't known yet."));
                    else if (oc != null && old.Table(rt.From) is { } rot && !ot.ForeignKeys.Any(k => k.Columns.Count == 1 && k.Columns[0].Equals(oc.Name, StringComparison.OrdinalIgnoreCase) && old.Table(k.RefTable) == rot))
                        f.Add(new MapFinding(M10, about, $"{ot.Name}.{oc.Name} has no foreign key to {rot.Name}: it may hold keys of rows that are gone.", "The dry run lists them; they're rejected unless unknown is null."));
                }
                if (cm.Kind == ColumnKind.Lookup && nt.ForeignKeys.FirstOrDefault(k => k.Columns.Count == 1 && k.Columns[0].Equals(nc.Name, StringComparison.OrdinalIgnoreCase)) is { } lfk
                    && map.Table(neu.Table(lfk.RefTable)?.Name ?? lfk.RefTable) is { Rows: { Count: > 0 } rows } code && neu.Table(lfk.RefTable) is { } codeTable)
                {
                    var codes = rows.Select(r => Expression.Str(Transforms.Clr(r.GetValueOrDefault(lfk.RefColumns[0])))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = (cm.Map?.Values.Select(v => Expression.Str(Transforms.Clr(v))).OfType<string>() ?? []).Concat(cm.IfNull != null ? [Expression.Str(Transforms.Clr(cm.IfNull))!] : []).Where(v => !codes.Contains(v)).Distinct().ToList();
                    if (missing.Count > 0) f.Add(new MapFinding(M11, about, $"It gives {string.Join(", ", missing)}, which {codeTable.Name} doesn't have.", $"Add {(missing.Count == 1 ? "it" : "them")} to {codeTable.Name}'s rows, or change the lookup."));
                }
                if (cm.Kind is ColumnKind.Direct or ColumnKind.Convert && oc != null && nt.Uniques.Any(u => u.Count == 1 && u[0].Equals(nc.Name, StringComparison.OrdinalIgnoreCase))
                    && !(ot.PrimaryKey.Count == 1 && ot.PrimaryKey[0].Equals(oc.Name, StringComparison.OrdinalIgnoreCase)) && !ot.Uniques.Any(u => u.Count == 1 && u[0].Equals(oc.Name, StringComparison.OrdinalIgnoreCase)))
                    f.Add(new MapFinding(M13, about, $"{nc.Name} is unique; {ot.Name}.{oc.Name} isn't.", "The dry run lists duplicates."));
                if (cm.Kind == ColumnKind.Convert && cm.As is "date" or "datetime" && oc != null && oc.Type.IsText && cm.Formats is not { Count: > 0 })
                    f.Add(new MapFinding(M14, about, $"{ot.Name}.{oc.Name} is text, read as yyyy-MM-dd.", "Profile it, and list the formats it holds."));
            }
            foreach (var c in nt.Columns.Where(c => !c.Identity && !c.Nullable && c.Default == null && tm.Column(c.Name) == null && (tm.Rows == null || tm.From != null)))
                f.Add(new MapFinding(M4, $"{nt.Name}.{c.Name}", $"{c.Name} ({c.Type.Declared}) is required, and nothing fills it.", "Map it, or give it a constant."));
        }
        return [.. f.OrderByDescending(x => x.Severity).ThenBy(x => order.IndexOf(x.About.Split('.')[0])).ThenBy(x => x.About, StringComparer.Ordinal)];
    }
}
