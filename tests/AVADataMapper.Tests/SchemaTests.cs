using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

/// <summary>Schemas read from DDL (T-SQL through ScriptDom, PostgreSQL and SQLite by our parser) and from SQLite's catalog.</summary>
public sealed class SchemaTests
{
    /// <summary>A schema as text, one line a table: columns with types, keys, foreign keys, uniques, checks.</summary>
    internal static List<string> Shape(DatabaseSchema s) =>
    [
        .. s.Tables.Select(t =>
            $"{t.Name}: " + string.Join(", ", t.Columns.Select(c => $"{c.Name} {c.Type.Kind}{(c.Type.Length is { } l ? $"({l})" : c.Type.Precision is { } p ? $"({p},{c.Type.Scale})" : "")}{(c.Nullable ? "" : "!")}{(c.Identity ? "+" : "")}")) +
            (t.PrimaryKey.Count > 0 ? $" | pk {string.Join(",", t.PrimaryKey)}" : "") +
            string.Concat(t.ForeignKeys.Select(f => $" | fk {string.Join(",", f.Columns)}→{f.RefTable.Split('.')[^1]}({string.Join(",", f.RefColumns)})")) +
            string.Concat(t.Uniques.Select(u => $" | unique {string.Join(",", u)}")) +
            string.Concat(t.Checks.Select(c => $" | check {c}"))),
    ];

    [Fact]
    public void The_old_schema_reads_the_same_from_tsql_ddl_and_from_the_sqlite_catalog()
    {
        var tsql = SchemaFiles.Load(Samples.Path("old/clinic-old.tsql.sql"));
        Assert.Equal(Dialect.SqlServer, tsql.Dialect);
        Assert.All(tsql.Tables, t => Assert.Equal("dbo", t.Schema));
        var patient = tsql.Table("tblPatient")!;
        Assert.Equal("dbo.tblPatient", patient.FullName);
        Assert.True(patient.Column("PatID")!.Identity);
        Assert.Equal("'Y'", patient.Column("Active")!.Default);
        Assert.Equal(new DataType(TypeKind.Text, "varchar(70)", 70), patient.Column("PatName")!.Type);
        Assert.Equal(TypeKind.Money, tsql.Table("tblCharge")!.Column("Amt")!.Type.Kind);
        Assert.Equal(-1, tsql.Table("FaxQueue")!.Column("Document")!.Type.Length);
        // The SQLite copy has SQLite's types for keys and blobs; otherwise the same tables, columns, nulls and keys.
        var lite = Samples.Old;
        Assert.Equal(Dialect.Sqlite, lite.Dialect);
        Assert.Equal(tsql.Tables.Select(t => t.Name), lite.Tables.Select(t => t.Name));
        foreach (var t in tsql.Tables)
        {
            var l = lite.Table(t.Name)!;
            Assert.Equal(t.Columns.Select(c => (c.Name, c.Nullable, c.Identity)), l.Columns.Select(c => (c.Name, c.Nullable, c.Identity)));
            Assert.Equal(t.PrimaryKey, l.PrimaryKey);
            Assert.Equal(t.ForeignKeys.Select(f => (string.Join(",", f.Columns), f.RefTable.Split('.')[^1], string.Join(",", f.RefColumns))), l.ForeignKeys.Select(f => (string.Join(",", f.Columns), f.RefTable, string.Join(",", f.RefColumns))));
        }
        Assert.Empty(tsql.Table("tblCharge")!.ForeignKeys.Where(f => f.Columns.Contains("PatID")));
    }

    [Fact]
    public void The_new_schema_reads_the_same_from_postgres_ddl_sqlite_ddl_and_the_sqlite_catalog()
    {
        var pg = SchemaFiles.Load(Samples.Path("new/clinic-new.pg.sql"));
        Assert.Equal(Dialect.PostgreSql, pg.Dialect);
        Assert.Equal(
        [
            "payer: payer_id Integer!+, payer_code Text(12)!, name Text(60)!, copay_percent Decimal(5,2)!, phone Text(20) | pk payer_id | unique payer_code",
            "sex_codes: code FixedText(1)!, meaning Text(20)! | pk code",
            "claim_status: code Text(10)!, meaning Text(40)! | pk code",
            "patient: patient_id Integer!+, family_name Text(40)!, given_name Text(30)!, birth_date Date, sex_code FixedText(1)!, payer_id Integer, phone Text(20), is_active Boolean!, created_by Text(30)! | pk patient_id | fk sex_code→sex_codes(code) | fk payer_id→payer(payer_id)",
            "visit: visit_id Integer!+, patient_id Integer!, visit_at DateTime!, clinician Text(60), reason Text(200) | pk visit_id | fk patient_id→patient(patient_id)",
            "charge: charge_id Integer!+, patient_id Integer!, visit_id Integer, procedure_code FixedText(5)!, amount Decimal(9,2)!, copay Decimal(9,2)!, posted_at DateTime! | pk charge_id | fk patient_id→patient(patient_id) | fk visit_id→visit(visit_id) | check amount > 0",
            "claim: claim_id Integer!+, claim_number Text(15)!, patient_id Integer!, amount Decimal(9,2)!, status Text(10)!, entered_at DateTime!, source_system Text(20)! | pk claim_id | fk patient_id→patient(patient_id) | fk status→claim_status(code) | unique claim_number",
        ], Shape(pg));
        Assert.All(pg.Tables, t => Assert.Equal("public", t.Schema));
        var liteDdl = SchemaFiles.Load(Samples.Path("new/clinic-new.sqlite.sql"), Dialect.Sqlite);
        Assert.Equal(Shape(pg), Shape(liteDdl));
        Assert.Equal(Shape(pg), Shape(Samples.New));
        Assert.Equal(["payer", "sex_codes", "claim_status", "patient", "visit", "charge", "claim"], pg.LoadOrder().Select(t => t.Name));
    }

    [Theory]
    [InlineData("varchar(40)", TypeKind.Text, 40, null, null)]
    [InlineData("nvarchar(max)", TypeKind.Text, -1, null, null)]
    [InlineData("character varying(20)", TypeKind.Text, 20, null, null)]
    [InlineData("numeric(9,2)", TypeKind.Decimal, null, 9, 2)]
    [InlineData("decimal(5)", TypeKind.Decimal, null, 5, 0)]
    [InlineData("money", TypeKind.Money, null, 19, 4)]
    [InlineData("timestamp(3) without time zone", TypeKind.DateTime, null, null, null)]
    [InlineData("double precision", TypeKind.Float, null, null, null)]
    [InlineData("bit", TypeKind.Bit, null, null, null)]
    [InlineData("char", TypeKind.FixedText, 1, null, null)]
    [InlineData("uuid", TypeKind.Guid, null, null, null)]
    [InlineData("geography", TypeKind.Other, null, null, null)]
    public void Types_parse_by_kind(string declared, TypeKind kind, int? length, int? precision, int? scale)
    {
        var t = DataType.Parse(declared);
        Assert.Equal((kind, length, precision, scale), (t.Kind, t.Length, t.Precision, t.Scale));
    }

    [Fact]
    public void Dialects_are_guessed_from_a_scripts_words_and_bad_scripts_say_so()
    {
        Assert.Equal(Dialect.SqlServer, DdlReader.Guess(File.ReadAllText(Samples.Path("old/clinic-old.tsql.sql"))));
        Assert.Equal(Dialect.PostgreSql, DdlReader.Guess(File.ReadAllText(Samples.Path("new/clinic-new.pg.sql"))));
        Assert.Equal(Dialect.Sqlite, DdlReader.Guess("CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, name text);"));
        var serial = DdlReader.Read("CREATE TABLE IF NOT EXISTS app.person (id serial PRIMARY KEY, email text UNIQUE NOT NULL, team_id int REFERENCES app.team(id), born date CHECK (born > '1900-01-01'));", Dialect.PostgreSql, "x");
        Assert.Equal(["person: id Integer!+, email Text!, team_id Integer, born Date | pk id | fk team_id→team(id) | unique email | check born > '1900-01-01'"], Shape(serial));
        var altered = DdlReader.Read("CREATE TABLE a (id int NOT NULL); CREATE TABLE b (id int NOT NULL, a_id int); ALTER TABLE ONLY b ADD CONSTRAINT b_a FOREIGN KEY (a_id) REFERENCES a(id); ALTER TABLE a ADD CONSTRAINT a_pk PRIMARY KEY (id);", Dialect.PostgreSql, "x");
        Assert.Equal(["a: id Integer! | pk id", "b: id Integer!, a_id Integer | fk a_id→a(id)"], Shape(altered));
        Assert.Throws<FormatException>(() => SchemaFiles.Load(Samples.Path("clinic.dmap")));
    }
}
