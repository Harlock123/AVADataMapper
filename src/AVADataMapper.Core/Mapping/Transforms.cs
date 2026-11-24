using System.Text.RegularExpressions;

namespace AVADataMapper.Core.Mapping;

/// <summary>The transforms: what a column map does to a row's values. Filled in in a later milestone.</summary>
public static partial class Transforms
{
    /// <summary>The column names an expression reads: its bare words that aren't keywords, functions or literals.</summary>
    public static IEnumerable<string> ExpressionColumns(string expression) =>
        Words().Matches(Strings().Replace(expression, "''")).Select(m => m.Value)
            .Where(w => !Reserved.Contains(w) && !char.IsDigit(w[0]) && !expression.Contains(w + "(", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "null", "is", "in", "true", "false", "like" };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Words();

    [GeneratedRegex(@"'([^']|'')*'")]
    private static partial Regex Strings();
}
