using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Mapping;

/// <summary>What a column map made of a row: a value, the new column's default, or a reason the row can't go in.</summary>
public readonly record struct Outcome(object? Value, string? Rejected = null, bool UseDefault = false)
{
    public static Outcome Reject(string why) => new(null, why);
    public bool Ok => Rejected == null;
}

/// <summary>What transforms need beyond the row: the key map (old key → new key, by new table) and the code tables' codes.</summary>
public sealed class TransformContext
{
    /// <summary>New table → old key (as text) → the new key its row got.</summary>
    public Dictionary<string, Dictionary<string, object>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>New code table → its codes (when its rows are given).</summary>
    public Dictionary<string, HashSet<string>> Codes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string KeyText(object? v) => Expression.Str(v)?.Trim() ?? "";
}

/// <summary>
/// The transforms: what a column map does to an old row's values to make a new column's value, and the checks every
/// value then meets (the new column's type, length, precision and nulls). The same code runs in the dry run, the
/// migration and the parity plan, so what's tried is what's done.
/// </summary>
public static partial class Transforms
{
    /// <summary>The column names an expression reads (when it doesn't parse: its bare words that aren't keywords or functions).</summary>
    public static IEnumerable<string> ExpressionColumns(string expression)
    {
        try
        {
            return Expression.Parse(expression).Columns;
        }
        catch (FormatException)
        {
            return Words().Matches(Strings().Replace(expression, "''")).Select(m => m.Value)
                .Where(w => !Reserved.Contains(w) && !char.IsDigit(w[0]) && !Expression.Functions.Contains(w.ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "null", "is", "in", "true", "false", "like" };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Words();

    [GeneratedRegex(@"'([^']|'')*'")]
    private static partial Regex Strings();

    /// <summary>A date from text in the formats given (or ISO dates when none are), or null.</summary>
    public static DateTime? ParseDate(string text, IReadOnlyList<string>? formats)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        string[] tries = formats is { Count: > 0 } ? [.. formats] : ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fff"];
        return DateTime.TryParseExact(t, tries, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
    }

    private static readonly Dictionary<string, bool> Truths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["y"] = true, ["yes"] = true, ["t"] = true, ["true"] = true, ["1"] = true, ["n"] = false, ["no"] = false, ["f"] = false, ["false"] = false, ["0"] = false,
    };

    /// <summary>A JSON value from the mapping as a value (numbers as decimal, true and false, text).</summary>
    public static object? Clr(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// A new column's value from an old row: the column map applied, then made the new column's type and checked
    /// against it. <paramref name="row"/> gives an old column's value by name.
    /// </summary>
    public static Outcome Apply(ColumnMap cm, Func<string, object?> row, Column target, TransformContext context)
    {
        var raw = Raw(cm, row, context);
        if (!raw.Ok || raw.UseDefault) return raw;
        var v = raw.Value;
        if (v is string s)
        {
            if (cm.Trim == true) s = s.Trim();
            s = cm.Case?.ToLowerInvariant() switch
            {
                "upper" => s.ToUpperInvariant(),
                "lower" => s.ToLowerInvariant(),
                "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()),
                _ => s,
            };
            v = s;
        }
        if (v == null && cm.IfNull != null) v = Clr(cm.IfNull);
        return Fit(v, target, cm.Kind == ColumnKind.Key);
    }

    private static Outcome Raw(ColumnMap cm, Func<string, object?> row, TransformContext context)
    {
        object? From() => cm.From == null ? null : row(cm.From);
        object? Blank(object? v) => cm.BlankIsNull == true && v is string s && s.Trim().Length == 0 ? null : v;
        Outcome Unknown(string what) => cm.Unknown?.ToLowerInvariant() switch
        {
            "null" => new Outcome(null),
            "default" => new Outcome(null, UseDefault: true),
            _ => Outcome.Reject(what),
        };
        switch (cm.Kind)
        {
            case ColumnKind.Direct:
                return new Outcome(Blank(From()));
            case ColumnKind.Convert:
            {
                var v = Blank(From());
                if (v == null) return new Outcome(null);
                var text = Expression.Str(v)!;
                if (cm.Trim == true) text = text.Trim();
                switch (cm.As?.ToLowerInvariant())
                {
                    case "date" or "datetime":
                        if (v is DateTime dt) return new Outcome(cm.As == "date" ? dt.Date : dt);
                        return ParseDate(text, cm.Formats) is { } d ? new Outcome(cm.As == "date" ? d.Date : d)
                            : Unknown($"{cm.From} \"{text}\" isn't a date{(cm.Formats is { Count: > 0 } f ? $" in {string.Join(", ", f)}" : " (yyyy-MM-dd)")}");
                    case "bool" or "boolean":
                        if (v is bool b) return new Outcome(b);
                        if (cm.Map != null)
                            return cm.Map.FirstOrDefault(kv => kv.Key.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit
                                ? new Outcome(Clr(hit.Value)) : Unknown($"{cm.From} \"{text}\" isn't one of {string.Join(", ", cm.Map.Keys)}");
                        return Truths.TryGetValue(text.Trim(), out var t) ? new Outcome(t) : Unknown($"{cm.From} \"{text}\" isn't yes or no");
                    case "number" or "integer" or "decimal":
                        if (Expression.Number(v) is not { } n) return Unknown($"{cm.From} \"{text}\" isn't a number");
                        return cm.As == "integer" && n != Math.Truncate(n) ? Unknown($"{cm.From} {n.ToString(CultureInfo.InvariantCulture)} isn't a whole number") : new Outcome(n);
                    default:
                        return new Outcome(text);
                }
            }
            case ColumnKind.Lookup:
            {
                var v = From();
                if (v == null) return new Outcome(null);
                var code = Expression.Str(v)!;
                if (cm.Trim == true) code = code.Trim();
                if (cm.Map is { Count: > 0 })
                    return cm.Map.FirstOrDefault(kv => kv.Key.Equals(code, StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit
                        ? new Outcome(Clr(hit.Value)) : Unknown($"{cm.From} \"{code}\" isn't in the lookup ({string.Join(", ", cm.Map.Keys)})");
                if (cm.Table != null && context.Codes.TryGetValue(cm.Table, out var codes))
                    return codes.FirstOrDefault(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) is { } c2 ? new Outcome(c2) : Unknown($"{cm.From} \"{code}\" isn't a code in {cm.Table}");
                return new Outcome(code);
            }
            case ColumnKind.Key:
            {
                var v = From();
                if (v == null || v is string bs && bs.Trim().Length == 0) return new Outcome(null);
                if (cm.Table == null || !context.Keys.TryGetValue(cm.Table, out var keys)) return Outcome.Reject($"{cm.Table ?? "?"} has no key map (its rows aren't loaded first)");
                return keys.TryGetValue(TransformContext.KeyText(v), out var key) ? new Outcome(key)
                    : Unknown($"{cm.From} {TransformContext.KeyText(v)} has no {cm.Table} row (the old row it points at isn't migrated)");
            }
            case ColumnKind.Split:
            {
                if (Expression.Str(From()) is not { } text) return new Outcome(null);
                var parts = text.Split(cm.Separator ?? ",");
                var p = cm.Part ?? 1;
                var i = p > 0 ? p - 1 : parts.Length + p;
                return new Outcome(i >= 0 && i < parts.Length ? Blank(cm.Trim == true ? parts[i].Trim() : parts[i]) is var x && x is string sx && sx.Length == 0 ? null : x : null);
            }
            case ColumnKind.Merge:
            {
                var parts = (cm.Froms ?? []).Select(f => Expression.Str(row(f))).Where(x => !string.IsNullOrEmpty(x)).Select(x => cm.Trim == true ? x!.Trim() : x!).ToList();
                return new Outcome(parts.Count == 0 ? null : string.Join(cm.Separator ?? " ", parts));
            }
            case ColumnKind.Constant:
                return new Outcome(Clr(cm.Value));
            case ColumnKind.Default:
            case ColumnKind.Generated:
                return new Outcome(null, UseDefault: true);
            case ColumnKind.Expression:
                try
                {
                    return new Outcome(Expression.Parse(cm.Expression ?? "null").Evaluate(row));
                }
                catch (FormatException e)
                {
                    return Outcome.Reject(e.Message);
                }
        }
        return Outcome.Reject($"{cm.Kind} isn't a kind of column mapping");
    }

    /// <summary>A value made the new column's type, and checked against it: nulls, length, precision, range.</summary>
    public static Outcome Fit(object? v, Column target, bool isKey = false)
    {
        var t = target.Type;
        if (v == null)
            return target.Nullable ? new Outcome(null) : target.Default != null ? new Outcome(null, UseDefault: true) : Outcome.Reject($"{target.Name} is required, and the value is null");
        if (isKey) return new Outcome(v);
        switch (t.Kind)
        {
            case TypeKind.Text or TypeKind.FixedText:
            {
                var s = Expression.Str(v)!;
                if (t.Kind == TypeKind.FixedText) s = s.TrimEnd();
                if (t.Length is > 0 and var n && s.Length > n) return Outcome.Reject($"{target.Name} takes {n} characters; \"{Short(s)}\" has {s.Length}");
                return new Outcome(s);
            }
            case TypeKind.SmallInteger or TypeKind.Integer or TypeKind.BigInteger:
            {
                if (Expression.Number(v) is not { } n) return Outcome.Reject($"{target.Name} takes a whole number; \"{Short(Expression.Str(v)!)}\" isn't one");
                if (n != Math.Truncate(n)) return Outcome.Reject($"{target.Name} takes a whole number; {n.ToString(CultureInfo.InvariantCulture)} isn't one");
                var (lo, hi) = t.Kind switch { TypeKind.SmallInteger => (short.MinValue, (decimal)short.MaxValue), TypeKind.Integer => (int.MinValue, (decimal)int.MaxValue), _ => (long.MinValue, (decimal)long.MaxValue) };
                if (n < lo || n > hi) return Outcome.Reject($"{target.Name} ({t.Declared}) can't hold {n.ToString(CultureInfo.InvariantCulture)}");
                return new Outcome(t.Kind == TypeKind.BigInteger ? (object)(long)n : (int)n);
            }
            case TypeKind.Decimal or TypeKind.Money:
            {
                if (Expression.Number(v) is not { } n) return Outcome.Reject($"{target.Name} takes a number; \"{Short(Expression.Str(v)!)}\" isn't one");
                var scale = t.Scale ?? 0;
                var rounded = Math.Round(n, scale, MidpointRounding.AwayFromZero);
                if (rounded != n) return Outcome.Reject($"{target.Name} keeps {scale} decimal place{(scale == 1 ? "" : "s")}; {n.ToString(CultureInfo.InvariantCulture)} would be rounded");
                if (t.Precision is { } p && Math.Abs(Math.Truncate(rounded)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length > p - scale)
                    return Outcome.Reject($"{target.Name} ({t.Declared}) can't hold {n.ToString(CultureInfo.InvariantCulture)}");
                return new Outcome(rounded);
            }
            case TypeKind.Float:
                return Expression.Number(v) is { } f ? new Outcome((double)f) : Outcome.Reject($"{target.Name} takes a number; \"{Short(Expression.Str(v)!)}\" isn't one");
            case TypeKind.Boolean or TypeKind.Bit:
                if (v is bool b) return new Outcome(b);
                if (Expression.Number(v) is { } bn && (bn == 0 || bn == 1)) return new Outcome(bn == 1);
                return Expression.Str(v) is { } bs && Truths.TryGetValue(bs.Trim(), out var tv) ? new Outcome(tv) : Outcome.Reject($"{target.Name} takes true or false; \"{Short(Expression.Str(v)!)}\" isn't one");
            case TypeKind.Date or TypeKind.DateTime:
            {
                var d = v as DateTime? ?? (v is DateOnly od ? od.ToDateTime(TimeOnly.MinValue) : v is DateTimeOffset dto ? dto.DateTime : ParseDate(Expression.Str(v)!, null));
                if (d is not { } dt) return Outcome.Reject($"{target.Name} takes a date; \"{Short(Expression.Str(v)!)}\" isn't one (convert it)");
                if (t.Kind == TypeKind.Date && dt.TimeOfDay != TimeSpan.Zero) return Outcome.Reject($"{target.Name} takes a date; {dt:yyyy-MM-dd HH:mm} has a time (convert it as date)");
                return new Outcome(dt);
            }
            default:
                return new Outcome(v);
        }
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..37] + "…";
}
