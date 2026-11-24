using System.Globalization;
using System.Text;

namespace AVADataMapper.Core.Mapping;

/// <summary>
/// The mapping's own small expression language, for filters (Deleted = 0) and computed columns (upper(trim(Code))).
/// It isn't SQL, so it runs the same whichever database the rows come from, and it's checked when written: an unknown
/// column or function is an error before anything runs. Values: null, text, numbers (decimal), true and false, dates.
/// Operators: = &lt;&gt; != &lt; &gt; &lt;= &gt;= + - * / || and or not, is [not] null, in (…), like ('%' and '_').
/// Functions: upper lower trim ltrim rtrim len left right substr replace coalesce nullif iif concat round abs
/// date(text, format…) year month day today.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    public string Text { get; }

    /// <summary>The columns it reads.</summary>
    public IReadOnlyList<string> Columns { get; }

    private Expression(string text, Node root, List<string> columns)
    {
        Text = text;
        _root = root;
        Columns = columns;
    }

    public static readonly string[] Functions =
        ["upper", "lower", "trim", "ltrim", "rtrim", "len", "left", "right", "substr", "replace", "coalesce", "nullif", "iif", "concat", "round", "abs", "date", "year", "month", "day", "today"];

    /// <summary>Parses an expression; throws <see cref="FormatException"/> saying where it's wrong.</summary>
    public static Expression Parse(string text)
    {
        var p = new Parser(text);
        var node = p.Or();
        if (!p.AtEnd) throw new FormatException($"Unexpected \"{p.Rest}\" at {p.Position + 1} in: {text}");
        var columns = new List<string>();
        Collect(node, columns);
        return new Expression(text, node, columns);
    }

    /// <summary>Checks it reads only these columns; returns what's wrong, or null.</summary>
    public string? Check(IEnumerable<string> columns)
    {
        var known = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = Columns.Where(c => !known.Contains(c)).ToList();
        return unknown.Count == 0 ? null : $"{string.Join(", ", unknown)} {(unknown.Count == 1 ? "isn't a column" : "aren't columns")} of the old table.";
    }

    public object? Evaluate(Func<string, object?> column) => _root.Eval(column);

    /// <summary>Whether a filter lets a row through (null counts as no).</summary>
    public bool Test(Func<string, object?> column) => Evaluate(column) is true;

    private static void Collect(Node n, List<string> into)
    {
        switch (n)
        {
            case ColumnNode c when !into.Contains(c.Name, StringComparer.OrdinalIgnoreCase): into.Add(c.Name); break;
            case UnaryNode u: Collect(u.Operand, into); break;
            case BinaryNode b: Collect(b.Left, into); Collect(b.Right, into); break;
            case CallNode f: foreach (var a in f.Args) Collect(a, into); break;
            case InNode i: Collect(i.Value, into); foreach (var a in i.List) Collect(a, into); break;
            case IsNullNode z: Collect(z.Value, into); break;
        }
    }

    // ───────────────────────────── values ─────────────────────────────

    internal static decimal? Number(object? v) => v switch
    {
        null => null,
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        double f => (decimal)f,
        float f => (decimal)f,
        bool b => b ? 1 : 0,
        string s when decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    internal static string? Str(object? v) => v switch
    {
        null => null,
        string s => s,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    internal static int Compare(object? a, object? b)
    {
        if (a is DateTime da && b is DateTime db) return da.CompareTo(db);
        // Numbers compare as numbers unless both sides are text ('10' < '9' as text, as SQL would).
        if (Number(a) is { } x && Number(b) is { } y && !(a is string && b is string)) return x.CompareTo(y);
        return string.Compare(Str(a), Str(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Equal(object? a, object? b) =>
        a is bool ba && b is bool bb ? ba == bb
        : Number(a) is { } x && Number(b) is { } y && (a is not string || b is not string) ? x == y
        : string.Equals(Str(a)?.TrimEnd(), Str(b)?.TrimEnd(), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── the tree ─────────────────────────────

    private abstract class Node
    {
        public abstract object? Eval(Func<string, object?> column);
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Eval(Func<string, object?> column) => value;
    }

    private sealed class ColumnNode(string name) : Node
    {
        public string Name { get; } = name;
        public override object? Eval(Func<string, object?> column) => column(Name);
    }

    private sealed class UnaryNode(string op, Node operand) : Node
    {
        public Node Operand { get; } = operand;
        public override object? Eval(Func<string, object?> column)
        {
            var v = Operand.Eval(column);
            return op == "not" ? v is bool b ? !b : null : Number(v) is { } n ? -n : null;
        }
    }

    private sealed class IsNullNode(Node value, bool not) : Node
    {
        public Node Value { get; } = value;
        public override object? Eval(Func<string, object?> column) => (Value.Eval(column) == null) != not;
    }

    private sealed class InNode(Node value, List<Node> list, bool not) : Node
    {
        public Node Value { get; } = value;
        public List<Node> List { get; } = list;
        public override object? Eval(Func<string, object?> column)
        {
            var v = Value.Eval(column);
            if (v == null) return null;
            return List.Any(x => Equal(v, x.Eval(column))) != not;
        }
    }

    private sealed class BinaryNode(string op, Node left, Node right) : Node
    {
        public Node Left { get; } = left;
        public Node Right { get; } = right;

        public override object? Eval(Func<string, object?> column)
        {
            if (op == "and")
            {
                var l = Left.Eval(column);
                if (l is false) return false;
                var r = Right.Eval(column);
                return r is false ? false : l is true && r is true ? true : null;
            }
            if (op == "or")
            {
                var l = Left.Eval(column);
                if (l is true) return true;
                var r = Right.Eval(column);
                return r is true ? true : l is false && r is false ? false : null;
            }
            var a = Left.Eval(column);
            var b = Right.Eval(column);
            if (op == "||") return a == null && b == null ? null : Str(a) + Str(b);
            if (a == null || b == null) return null;
            switch (op)
            {
                case "=": return Equal(a, b);
                case "<>": return !Equal(a, b);
                case "<": return Compare(a, b) < 0;
                case ">": return Compare(a, b) > 0;
                case "<=": return Compare(a, b) <= 0;
                case ">=": return Compare(a, b) >= 0;
                case "like": return Like(Str(a)!, Str(b)!);
            }
            if (Number(a) is not { } x || Number(b) is not { } y)
                return op == "+" ? Str(a) + Str(b) : null;
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? null : x / y,
                _ => null,
            };
        }

        private static bool Like(string s, string pattern)
        {
            var re = "^" + string.Concat(pattern.Select(c => c switch { '%' => ".*", '_' => ".", _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()) })) + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(s, re, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        }
    }

    private sealed class CallNode(string name, List<Node> args) : Node
    {
        public List<Node> Args { get; } = args;

        public override object? Eval(Func<string, object?> column)
        {
            var v = Args.Select(a => a.Eval(column)).ToList();
            object? A(int i) => i < v.Count ? v[i] : null;
            string? S(int i) => Str(A(i));
            int I(int i, int fallback) => Number(A(i)) is { } n ? (int)n : fallback;
            switch (name)
            {
                case "upper": return S(0)?.ToUpperInvariant();
                case "lower": return S(0)?.ToLowerInvariant();
                case "trim": return S(0)?.Trim();
                case "ltrim": return S(0)?.TrimStart();
                case "rtrim": return S(0)?.TrimEnd();
                case "len": return S(0) is { } s ? (decimal)s.Length : null;
                case "left": return S(0) is { } l ? l[..Math.Min(l.Length, Math.Max(0, I(1, 0)))] : null;
                case "right": return S(0) is { } r ? r[Math.Max(0, r.Length - Math.Max(0, I(1, 0)))..] : null;
                case "substr":
                    if (S(0) is not { } sub) return null;
                    var start = Math.Clamp(I(1, 1) - 1, 0, sub.Length);
                    var len = Math.Clamp(I(2, sub.Length - start), 0, sub.Length - start);
                    return sub.Substring(start, len);
                case "replace": return S(0)?.Replace(S(1) ?? "", S(2) ?? "", StringComparison.Ordinal);
                case "coalesce": return v.FirstOrDefault(x => x != null);
                case "nullif": return Equal(A(0), A(1)) ? null : A(0);
                case "iif": return A(0) is true ? A(1) : A(2);
                case "concat": return string.Concat(v.Select(Str));
                case "round": return Number(A(0)) is { } n ? Math.Round(n, I(1, 0), MidpointRounding.AwayFromZero) : null;
                case "abs": return Number(A(0)) is { } m ? Math.Abs(m) : null;
                case "date":
                    if (A(0) is DateTime dt) return dt.Date;
                    if (S(0) is not { } text) return null;
                    var formats = v.Skip(1).Select(Str).OfType<string>().ToArray();
                    return Transforms.ParseDate(text, formats) is { } d ? d : null;
                case "year": return A(0) is DateTime y ? (decimal)y.Year : null;
                case "month": return A(0) is DateTime mo ? (decimal)mo.Month : null;
                case "day": return A(0) is DateTime da ? (decimal)da.Day : null;
                case "today": return DateTime.Today;
            }
            return null;
        }
    }

    // ───────────────────────────── parsing ─────────────────────────────

    private sealed class Parser(string text)
    {
        private readonly List<(string Kind, string Text, int At)> _tokens = Lex(text);
        private int _i;

        public bool AtEnd => _i >= _tokens.Count;
        public int Position => AtEnd ? text.Length : _tokens[_i].At;
        public string Rest => AtEnd ? "" : text[_tokens[_i].At..];

        private (string Kind, string Text, int At) Peek => AtEnd ? ("end", "", text.Length) : _tokens[_i];

        private bool Word(string w)
        {
            if (!AtEnd && Peek.Kind == "word" && Peek.Text.Equals(w, StringComparison.OrdinalIgnoreCase)) { _i++; return true; }
            return false;
        }

        private bool Sym(string s)
        {
            if (!AtEnd && Peek.Kind == "sym" && Peek.Text == s) { _i++; return true; }
            return false;
        }

        private FormatException Error(string what) =>
            new(AtEnd ? $"{what} expected at the end of: {text}" : $"{what} expected at {Peek.At + 1} (\"{Peek.Text}\") in: {text}");

        public Node Or()
        {
            var n = And();
            while (Word("or")) n = new BinaryNode("or", n, And());
            return n;
        }

        private Node And()
        {
            var n = Not();
            while (Word("and")) n = new BinaryNode("and", n, Not());
            return n;
        }

        private Node Not() => Word("not") ? new UnaryNode("not", Not()) : Comparison();

        private Node Comparison()
        {
            var left = Additive();
            if (Word("is"))
            {
                var not = Word("not");
                if (!Word("null")) throw Error("null");
                return new IsNullNode(left, not);
            }
            var negate = false;
            var at = _i;
            if (Word("not"))
            {
                if (Peek.Text.Equals("in", StringComparison.OrdinalIgnoreCase) || Peek.Text.Equals("like", StringComparison.OrdinalIgnoreCase)) negate = true;
                else _i = at;
            }
            if (Word("in"))
            {
                if (!Sym("(")) throw Error("(");
                var list = new List<Node> { Additive() };
                while (Sym(",")) list.Add(Additive());
                if (!Sym(")")) throw Error(")");
                return new InNode(left, list, negate);
            }
            if (Word("like"))
            {
                var like = new BinaryNode("like", left, Additive());
                return negate ? new UnaryNode("not", like) : like;
            }
            foreach (var op in new[] { "<=", ">=", "<>", "!=", "=", "<", ">" })
                if (Sym(op)) return new BinaryNode(op == "!=" ? "<>" : op, left, Additive());
            return left;
        }

        private Node Additive()
        {
            var n = Term();
            while (true)
            {
                if (Sym("+")) n = new BinaryNode("+", n, Term());
                else if (Sym("-")) n = new BinaryNode("-", n, Term());
                else if (Sym("||")) n = new BinaryNode("||", n, Term());
                else return n;
            }
        }

        private Node Term()
        {
            var n = Unary();
            while (true)
            {
                if (Sym("*")) n = new BinaryNode("*", n, Unary());
                else if (Sym("/")) n = new BinaryNode("/", n, Unary());
                else return n;
            }
        }

        private Node Unary() => Sym("-") ? new UnaryNode("-", Unary()) : Primary();

        private Node Primary()
        {
            if (AtEnd) throw Error("A value");
            var t = Peek;
            if (Sym("("))
            {
                var inner = Or();
                if (!Sym(")")) throw Error(")");
                return inner;
            }
            _i++;
            switch (t.Kind)
            {
                case "num": return new Literal(decimal.Parse(t.Text, CultureInfo.InvariantCulture));
                case "str": return new Literal(t.Text);
                case "col": return new ColumnNode(t.Text);
                case "word":
                    if (t.Text.Equals("null", StringComparison.OrdinalIgnoreCase)) return new Literal(null);
                    if (t.Text.Equals("true", StringComparison.OrdinalIgnoreCase)) return new Literal(true);
                    if (t.Text.Equals("false", StringComparison.OrdinalIgnoreCase)) return new Literal(false);
                    if (Sym("("))
                    {
                        var name = t.Text.ToLowerInvariant();
                        if (!Functions.Contains(name)) throw new FormatException($"There's no function {t.Text}(): the functions are {string.Join(", ", Functions)}.");
                        var args = new List<Node>();
                        if (!Sym(")"))
                        {
                            args.Add(Or());
                            while (Sym(",")) args.Add(Or());
                            if (!Sym(")")) throw Error(")");
                        }
                        return new CallNode(name, args);
                    }
                    return new ColumnNode(t.Text);
            }
            _i--;
            throw Error("A value");
        }

        private static List<(string, string, int)> Lex(string s)
        {
            var list = new List<(string, string, int)>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                var start = i;
                if (c == '\'')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= s.Length) throw new FormatException($"A text value started at {start + 1} isn't closed with ' in: {s}");
                        if (s[i] == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        if (s[i] == '\'') { i++; break; }
                        sb.Append(s[i++]);
                    }
                    list.Add(("str", sb.ToString(), start));
                    continue;
                }
                if (c is '"' or '[')
                {
                    var close = c == '[' ? ']' : '"';
                    var end = s.IndexOf(close, i + 1);
                    if (end < 0) throw new FormatException($"A column name started at {start + 1} isn't closed in: {s}");
                    list.Add(("col", s[(i + 1)..end], start));
                    i = end + 1;
                    continue;
                }
                if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                {
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    list.Add(("num", s[start..i], start));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    list.Add(("word", s[start..i], start));
                    continue;
                }
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "<=" or ">=" or "<>" or "!=" or "||") { list.Add(("sym", two, start)); i += 2; continue; }
                if ("=<>+-*/(),".Contains(c)) { list.Add(("sym", c.ToString(), start)); i++; continue; }
                throw new FormatException($"Unexpected \"{c}\" at {i + 1} in: {s}");
            }
            return list;
        }
    }
}
