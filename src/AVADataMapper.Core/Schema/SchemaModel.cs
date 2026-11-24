namespace AVADataMapper.Core.Schema;

/// <summary>The databases the mapper reads and writes.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Dialect>))]
public enum Dialect
{
    [System.Text.Json.Serialization.JsonStringEnumMemberName("sqlserver")] SqlServer,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("postgresql")] PostgreSql,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("sqlite")] Sqlite,
}

/// <summary>What a column's type is, whatever the database calls it: the mapper compares types by kind.</summary>
public enum TypeKind
{
    Text,
    FixedText,
    SmallInteger,
    Integer,
    BigInteger,
    Decimal,
    Money,
    Float,
    Boolean,
    Bit,
    Date,
    DateTime,
    Time,
    Binary,
    Guid,
    Other,
}

/// <summary>A column's type: its kind, as declared, and its length, precision and scale where it has them (Length -1: max).</summary>
public sealed record DataType(TypeKind Kind, string Declared, int? Length = null, int? Precision = null, int? Scale = null)
{
    public bool IsText => Kind is TypeKind.Text or TypeKind.FixedText;
    public bool IsWholeNumber => Kind is TypeKind.SmallInteger or TypeKind.Integer or TypeKind.BigInteger;
    public bool IsNumber => IsWholeNumber || Kind is TypeKind.Decimal or TypeKind.Money or TypeKind.Float;
    public bool IsTemporal => Kind is TypeKind.Date or TypeKind.DateTime or TypeKind.Time;

    public override string ToString() => Declared;

    /// <summary>A declared type (varchar(40), numeric(9,2), integer, timestamp without time zone…) by kind, length, precision and scale.</summary>
    public static DataType Parse(string declared)
    {
        var d = System.Text.RegularExpressions.Regex.Replace(declared.Trim(), @"\s+", " ");
        var open = d.IndexOf('(');
        var name = (open < 0 ? d : d[..open]).Trim().ToLowerInvariant();
        var close = d.IndexOf(')', Math.Max(open, 0));
        var args = open < 0 ? [] : d[(open + 1)..(close > open ? close : d.Length)].Split(',').Select(a => a.Trim()).ToArray();
        int? Arg(int i) => i < args.Length ? args[i].Equals("max", StringComparison.OrdinalIgnoreCase) ? -1 : int.TryParse(args[i], out var n) ? n : null : null;
        // Words after the parentheses (timestamp(3) without time zone) don't change the kind.
        var kind = name switch
        {
            "varchar" or "nvarchar" or "text" or "ntext" or "character varying" or "varchar2" or "nvarchar2" or "clob" or "string" or "citext" or "tinytext" or "mediumtext" or "longtext" or "sysname" or "xml" or "json" or "jsonb" => TypeKind.Text,
            "char" or "nchar" or "character" or "bpchar" => TypeKind.FixedText,
            "smallint" or "int2" or "tinyint" => TypeKind.SmallInteger,
            "int" or "integer" or "int4" or "mediumint" or "serial" or "serial4" => TypeKind.Integer,
            "bigint" or "int8" or "bigserial" or "serial8" => TypeKind.BigInteger,
            "decimal" or "numeric" or "number" or "dec" => TypeKind.Decimal,
            "money" or "smallmoney" => TypeKind.Money,
            "float" or "real" or "double" or "double precision" or "float4" or "float8" => TypeKind.Float,
            "boolean" or "bool" => TypeKind.Boolean,
            "bit" => TypeKind.Bit,
            "date" => TypeKind.Date,
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "timestamp" or "timestamptz" or "timestamp without time zone" or "timestamp with time zone" => TypeKind.DateTime,
            "time" or "timetz" or "time without time zone" or "time with time zone" => TypeKind.Time,
            "binary" or "varbinary" or "image" or "bytea" or "blob" or "longblob" or "mediumblob" => TypeKind.Binary,
            "uniqueidentifier" or "uuid" => TypeKind.Guid,
            _ when name.StartsWith("timestamp", StringComparison.Ordinal) => TypeKind.DateTime,
            _ when name.StartsWith("time", StringComparison.Ordinal) => TypeKind.Time,
            _ => TypeKind.Other,
        };
        return kind switch
        {
            TypeKind.Text or TypeKind.FixedText or TypeKind.Binary => new DataType(kind, d, Arg(0) ?? (kind == TypeKind.FixedText && args.Length == 0 && name is "char" or "nchar" or "character" ? 1 : null)),
            TypeKind.Decimal => new DataType(kind, d, null, Arg(0), Arg(1) ?? (args.Length > 0 ? 0 : null)),
            TypeKind.Money => new DataType(kind, d, null, name == "smallmoney" ? 10 : 19, 4),
            _ => new DataType(kind, d),
        };
    }
}

/// <summary>A column: its name, type, whether it takes nulls, its default, and whether the database fills it (identity).</summary>
public sealed class Column
{
    public required string Name { get; init; }
    public required DataType Type { get; init; }
    public bool Nullable { get; init; } = true;
    public string? Default { get; init; }
    public bool Identity { get; init; }
    public int Ordinal { get; init; }

    public override string ToString() => $"{Name} {Type}{(Nullable ? "" : " not null")}{(Identity ? " identity" : "")}";
}

/// <summary>A foreign key: these columns refer to those of another table.</summary>
public sealed record ForeignKey(string Name, IReadOnlyList<string> Columns, string RefTable, IReadOnlyList<string> RefColumns);

/// <summary>A table: its columns in order, primary key, foreign keys, unique keys and checks.</summary>
public sealed class Table
{
    public string Schema { get; init; } = "";
    public required string Name { get; init; }
    public List<Column> Columns { get; } = [];
    public List<string> PrimaryKey { get; } = [];
    public List<ForeignKey> ForeignKeys { get; } = [];
    public List<List<string>> Uniques { get; } = [];
    public List<string> Checks { get; } = [];

    /// <summary>schema.name, or name when there's no schema.</summary>
    public string FullName => Schema.Length > 0 ? $"{Schema}.{Name}" : Name;

    public Column? Column(string name) => Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => FullName;
}

/// <summary>A database's schema, read from its catalog or from DDL: where from, in which dialect, and its tables.</summary>
public sealed class DatabaseSchema
{
    public required Dialect Dialect { get; init; }

    /// <summary>What it was read from, for people: a file's name, or server and database (never a connection string).</summary>
    public required string Source { get; init; }

    public List<Table> Tables { get; } = [];

    /// <summary>A table by name: schema.name or name, any case; a name alone matches in any schema (dbo, public, main first).</summary>
    public Table? Table(string name)
    {
        var n = name.Trim().Trim('[', ']', '"', '`');
        var exact = Tables.FirstOrDefault(t => t.FullName.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var bare = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
        return Tables.Where(t => t.Name.Equals(bare, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Schema is "dbo" or "public" or "main" or "" ? 0 : 1).FirstOrDefault();
    }

    /// <summary>The tables in an order that loads parents before children (by foreign key); a cycle keeps its tables' order.</summary>
    public List<Table> LoadOrder()
    {
        var done = new List<Table>();
        var left = new List<Table>(Tables);
        while (left.Count > 0)
        {
            var ready = left.FirstOrDefault(t => t.ForeignKeys.All(f => Table(f.RefTable) is not { } p || p == t || done.Contains(p) || !left.Contains(p)));
            ready ??= left[0];
            done.Add(ready);
            left.Remove(ready);
        }
        return done;
    }
}
