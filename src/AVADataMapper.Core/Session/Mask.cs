using System.Text;
using System.Text.RegularExpressions;
using AVADataMapper.Core.Data;

namespace AVADataMapper.Core.Session;

/// <summary>
/// Masks the old database's values for AI agents, unless the person lets them see values: a value keeps its shape and
/// its first letter (HOLLOWAY → H*******, 13/45/1980 → 99/99/9999), so an agent can still reason about lengths and
/// formats. Codes (a column with few distinct values, each repeated) stay readable, so an agent can map them.
/// </summary>
public static partial class Mask
{
    public static string Value(string s)
    {
        var sb = new StringBuilder(s.Length);
        var first = true;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) { sb.Append(first ? ch : '*'); first = false; }
            else if (char.IsDigit(ch)) { sb.Append('9'); first = false; }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A reason or message with every quoted value in it masked ("13/45/1980" → "99/99/9999").</summary>
    public static string Text(string s) => Quoted().Replace(s, m => "\"" + Value(m.Groups[1].Value) + "\"");

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();

    /// <summary>An old key: numbers stay (they're row numbers), text keys are masked.</summary>
    public static string Key(string key) => key.All(ch => char.IsDigit(ch) || ch is '|' or '-') || key.StartsWith("row ", StringComparison.Ordinal) ? key : Value(key);

    /// <summary>Whether a profiled column holds codes: few distinct values, each used again and again.</summary>
    public static bool IsCodes(ColumnProfile c) => c.Distinct <= 30 && c.Values > 0 && c.Distinct * 2 <= c.Values && (c.MaxLength ?? 0) <= 20;
}
