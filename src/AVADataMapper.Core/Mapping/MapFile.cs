using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Core.Mapping;

/// <summary>How a new column gets its value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColumnKind>))]
public enum ColumnKind
{
    /// <summary>An old column's value as it is (types that fit).</summary>
    [JsonStringEnumMemberName("direct")] Direct,

    /// <summary>An old column's value converted: to a date (from text in given formats), number, boolean (Y/N, 1/0…) or text.</summary>
    [JsonStringEnumMemberName("convert")] Convert,

    /// <summary>An old code turned into a new one through a table of codes (Map), with what to do with one that isn't there.</summary>
    [JsonStringEnumMemberName("lookup")] Lookup,

    /// <summary>The new key of the row an old key pointed at: an old foreign key through the key map of its table.</summary>
    [JsonStringEnumMemberName("key")] Key,

    /// <summary>A part of an old column split at a separator ("LAST, FIRST": part 1 or 2).</summary>
    [JsonStringEnumMemberName("split")] Split,

    /// <summary>Old columns joined with a separator.</summary>
    [JsonStringEnumMemberName("merge")] Merge,

    /// <summary>The same value on every row.</summary>
    [JsonStringEnumMemberName("constant")] Constant,

    /// <summary>Left to the new column's default.</summary>
    [JsonStringEnumMemberName("default")] Default,

    /// <summary>A small, checked expression over old columns (not SQL).</summary>
    [JsonStringEnumMemberName("expression")] Expression,

    /// <summary>Filled by the new database (an identity key).</summary>
    [JsonStringEnumMemberName("generated")] Generated,
}

/// <summary>How sure a mapping is: confirmed by a person, or suggested sure, likely or guessed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Certainty>))]
public enum Certainty
{
    [JsonStringEnumMemberName("confirmed")] Confirmed,
    [JsonStringEnumMemberName("sure")] Sure,
    [JsonStringEnumMemberName("likely")] Likely,
    [JsonStringEnumMemberName("guessed")] Guessed,
}

/// <summary>One side of a mapping: the old or new database, by dialect and where its schema comes from. Never a connection string.</summary>
public sealed class Side
{
    /// <summary>"old" or "new": the name the person's saved connection goes by.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dialect")] public Dialect Dialect { get; set; }

    /// <summary>A SQLite database or a DDL script, relative to the map file; or null for a live database (a saved connection).</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    /// <summary>What the schema was read from, for people (server/database, or a file's name).</summary>
    [JsonPropertyName("described")] public string? Described { get; set; }
}

/// <summary>How one new column is filled.</summary>
public sealed class ColumnMap
{
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("kind")] public ColumnKind Kind { get; set; }

    /// <summary>The old column (direct, convert, lookup, key, split, expression's main column).</summary>
    [JsonPropertyName("from")] public string? From { get; set; }

    /// <summary>The old columns joined (merge).</summary>
    [JsonPropertyName("froms")] public List<string>? Froms { get; set; }

    /// <summary>The type converted to (convert): date, datetime, number, integer, bool or text.</summary>
    [JsonPropertyName("as")] public string? As { get; set; }

    /// <summary>The text formats a date is read in (convert to date or datetime), tried in order.</summary>
    [JsonPropertyName("formats")] public List<string>? Formats { get; set; }

    /// <summary>Old value → new value (lookup, or convert to bool): matched after trimming, any case.</summary>
    [JsonPropertyName("map")] public Dictionary<string, JsonNode?>? Map { get; set; }

    /// <summary>The new table whose key map a key column goes through (key), or the code table a lookup's values must be in.</summary>
    [JsonPropertyName("table")] public string? Table { get; set; }

    /// <summary>What to do with a value that isn't in the map, or doesn't convert: reject (the row), null, or default (the column's).</summary>
    [JsonPropertyName("unknown")] public string? Unknown { get; set; }

    /// <summary>The separator (split, merge).</summary>
    [JsonPropertyName("separator")] public string? Separator { get; set; }

    /// <summary>Which part, from 1 (split); a negative number counts from the end.</summary>
    [JsonPropertyName("part")] public int? Part { get; set; }

    /// <summary>Trim spaces from the value first.</summary>
    [JsonPropertyName("trim")] public bool? Trim { get; set; }

    /// <summary>upper, lower or title: change the case of a text value.</summary>
    [JsonPropertyName("case")] public string? Case { get; set; }

    /// <summary>The value (constant).</summary>
    [JsonPropertyName("value")] public JsonNode? Value { get; set; }

    /// <summary>The expression (expression).</summary>
    [JsonPropertyName("expression")] public string? Expression { get; set; }

    /// <summary>A blank text value counts as null (before converting).</summary>
    [JsonPropertyName("blankIsNull")] public bool? BlankIsNull { get; set; }

    /// <summary>When the value is null after everything, use this instead.</summary>
    [JsonPropertyName("ifNull")] public JsonNode? IfNull { get; set; }

    [JsonPropertyName("certainty")] public Certainty Certainty { get; set; } = Certainty.Confirmed;

    /// <summary>Why: the suggestion's reason, or the person's note.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    /// <summary>The old columns this reads (for checks and the parity plan).</summary>
    [JsonIgnore]
    public IEnumerable<string> Reads => Kind switch
    {
        ColumnKind.Merge => Froms ?? [],
        ColumnKind.Expression => (From != null ? [From] : Array.Empty<string>()).Concat(Transforms.ExpressionColumns(Expression ?? "")).Distinct(StringComparer.OrdinalIgnoreCase),
        ColumnKind.Constant or ColumnKind.Default or ColumnKind.Generated => [],
        _ => From != null ? [From] : [],
    };
}

/// <summary>How one new table is filled: from an old table (with a filter) or from rows given (a code table), and each column.</summary>
public sealed class TableMap
{
    [JsonPropertyName("target")] public string Target { get; set; } = "";

    /// <summary>The old table rows come from (null: the rows are given, for a code table).</summary>
    [JsonPropertyName("from")] public string? From { get; set; }

    /// <summary>Which old rows: a filter over old columns, in the expression language (Deleted = 0).</summary>
    [JsonPropertyName("where")] public string? Where { get; set; }

    /// <summary>The old key (its columns) each new row's key is kept against in the key map; default: the old table's primary key.</summary>
    [JsonPropertyName("oldKey")] public List<string>? OldKey { get; set; }

    [JsonPropertyName("columns")] public List<ColumnMap> Columns { get; set; } = [];

    /// <summary>Rows given (a code table): column → value.</summary>
    [JsonPropertyName("rows")] public List<Dictionary<string, JsonNode?>>? Rows { get; set; }

    [JsonPropertyName("certainty")] public Certainty Certainty { get; set; } = Certainty.Confirmed;
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    public ColumnMap? Column(string target) => Columns.FirstOrDefault(c => c.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A decision about the mapping, kept with it: what was decided about what, by whom and when.</summary>
public sealed class Decision
{
    /// <summary>What it's about: an old column ("tblPatient.Fax"), a new one, or a table.</summary>
    [JsonPropertyName("about")] public string About { get; set; } = "";
    [JsonPropertyName("said")] public string Said { get; set; } = "";

    /// <summary>"person", or an agent's name (an agent's decisions are proposals until a person confirms them).</summary>
    [JsonPropertyName("by")] public string By { get; set; } = "person";
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

/// <summary>
/// A mapping (.dmap): the old and new databases (named, never their connection strings), how each new table and column
/// is filled, and the decisions made along the way. JSON, written the same way every time so it diffs well.
/// </summary>
public sealed class MapFile
{
    public const string FormatName = "avadatamapper.map";
    public const string Extension = ".dmap";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("source")] public Side Source { get; set; } = new() { Name = "old" };
    [JsonPropertyName("target")] public Side Target { get; set; } = new() { Name = "new" };

    /// <summary>An AVALegacyLens scan of the old system (.lens), relative to the map file: evidence for suggestions and drops.</summary>
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }
    [JsonPropertyName("tables")] public List<TableMap> Tables { get; set; } = [];
    [JsonPropertyName("decisions")] public List<Decision> Decisions { get; set; } = [];

    public TableMap? Table(string target) => Tables.FirstOrDefault(t => t.Target.Equals(target, StringComparison.OrdinalIgnoreCase) || t.Target.EndsWith("." + target, StringComparison.OrdinalIgnoreCase));

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";

    /// <summary>Reads a map; one that isn't right throws <see cref="FormatException"/> saying what's wrong.</summary>
    public static MapFile Read(string json)
    {
        MapFile? map;
        try
        {
            map = JsonSerializer.Deserialize<MapFile>(json, Json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"It isn't a mapping file: {e.Message}");
        }
        if (map == null || map.Format != FormatName) throw new FormatException($"It isn't a mapping file (its format isn't \"{FormatName}\").");
        foreach (var t in map.Tables)
        {
            if (string.IsNullOrWhiteSpace(t.Target)) throw new FormatException("A table in it has no target.");
            foreach (var c in t.Columns)
                if (string.IsNullOrWhiteSpace(c.Target)) throw new FormatException($"A column of {t.Target} has no target.");
        }
        return map;
    }

    public static MapFile Load(string path) => Read(File.ReadAllText(path));

    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>A side's file, as a full path (it's kept relative to the map file).</summary>
    public static string? FullPath(Side side, string? mapPath) =>
        side.File == null ? null : Path.IsPathRooted(side.File) || mapPath == null ? side.File : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath))!, side.File));
}
