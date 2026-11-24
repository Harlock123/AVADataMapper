"""Makes the invented sample databases in this folder. Every patient, payer, clinician and number here is made up.

old/  The legacy clinic database as it was deployed: SQL Server, grown over twenty years. Names are kept as
      "LAST, FIRST", birth dates as text in whatever format the screen of the day used, flags as Y/N, codes with
      stray spaces; charges have no foreign key, so some point at patients that are gone; a fax table nothing uses.
        clinic-old.tsql.sql   the schema, as SQL Server DDL
        clinic-old.data.sql   its rows, as T-SQL INSERTs (for the opt-in SQL Server tests)
        clinic-old.db         the same schema and rows in SQLite (for the in-process tests)
new/  The rebuild's schema: PostgreSQL, with lookup tables, real dates and booleans, and new surrogate keys.
        clinic-new.pg.sql     the schema, as PostgreSQL DDL
        clinic-new.sqlite.sql the same in SQLite's dialect
        clinic-new.db         an empty SQLite database with that schema (the migration tests copy it)

The rows are made the same way every time: the tests count what they find in them.

Run: python3 make_samples.py
"""
import os
import random
import sqlite3

# ── the old schema ─────────────────────────────────────────────────────────────────────────────────────────────
# (table, [(column, sql server type, sqlite type, null, extra)], primary key, foreign keys [(column, table, column)])
OLD = [
    ("tblPayer", [
        ("PayerCode", "varchar(12)", "varchar(12)", False, ""),
        ("PayerName", "varchar(60)", "varchar(60)", False, ""),
        ("CopayPct", "decimal(5,2)", "decimal(5,2)", True, ""),
        ("Phone", "varchar(20)", "varchar(20)", True, ""),
    ], "PayerCode", []),
    ("tblPatient", [
        ("PatID", "int IDENTITY(1,1)", "integer", False, ""),
        ("PatName", "varchar(70)", "varchar(70)", False, ""),
        ("DOB", "varchar(10)", "varchar(10)", True, ""),
        ("Sex", "char(1)", "char(1)", True, ""),
        ("PayerCode", "varchar(12)", "varchar(12)", True, ""),
        ("Active", "char(1)", "char(1)", False, "DEFAULT 'Y'"),
        ("Phone", "varchar(20)", "varchar(20)", True, ""),
        ("Fax", "varchar(20)", "varchar(20)", True, ""),
        ("Deleted", "bit", "bit", False, "DEFAULT 0"),
        ("EnteredBy", "varchar(20)", "varchar(20)", True, ""),
    ], "PatID", [("PayerCode", "tblPayer", "PayerCode")]),
    ("tblVisit", [
        ("VisitID", "int IDENTITY(1,1)", "integer", False, ""),
        ("PatID", "int", "int", False, ""),
        ("VisitDt", "datetime", "datetime", False, ""),
        ("Clinician", "varchar(60)", "varchar(60)", True, ""),
        ("Reason", "varchar(255)", "varchar(255)", True, ""),
    ], "VisitID", [("PatID", "tblPatient", "PatID")]),
    # No foreign key on PatID: the 2009 billing import wrote charges for patients later purged.
    ("tblCharge", [
        ("ChgID", "int IDENTITY(1,1)", "integer", False, ""),
        ("PatID", "int", "int", False, ""),
        ("VisitID", "int", "int", True, ""),
        ("ProcCode", "varchar(10)", "varchar(10)", False, ""),
        ("Amt", "money", "money", False, ""),
        ("Copay", "money", "money", True, ""),
        ("PostedOn", "datetime", "datetime", False, ""),
    ], "ChgID", [("VisitID", "tblVisit", "VisitID")]),
    ("tblClaim", [
        ("ClaimNo", "varchar(15)", "varchar(15)", False, ""),
        ("PatID", "int", "int", False, ""),
        ("Amt", "money", "money", False, ""),
        ("Status", "varchar(8)", "varchar(8)", False, ""),
        ("EnteredOn", "datetime", "datetime", False, ""),
    ], "ClaimNo", [("PatID", "tblPatient", "PatID")]),
    ("FaxQueue", [
        ("FaxId", "int IDENTITY(1,1)", "integer", False, ""),
        ("Number", "varchar(20)", "varchar(20)", False, ""),
        ("Document", "varbinary(max)", "blob", True, ""),
    ], "FaxId", []),
]

# ── the new schema ─────────────────────────────────────────────────────────────────────────────────────────────
# (table, [(column, postgres type, sqlite type, null, extra)], primary key, foreign keys, unique, checks)
NEW = [
    ("payer", [
        ("payer_id", "integer GENERATED ALWAYS AS IDENTITY", "integer", False, ""),
        ("payer_code", "varchar(12)", "varchar(12)", False, ""),
        ("name", "varchar(60)", "varchar(60)", False, ""),
        ("copay_percent", "numeric(5,2)", "numeric(5,2)", False, "DEFAULT 0"),
        ("phone", "varchar(20)", "varchar(20)", True, ""),
    ], "payer_id", [], ["payer_code"], []),
    ("sex_codes", [
        ("code", "char(1)", "char(1)", False, ""),
        ("meaning", "varchar(20)", "varchar(20)", False, ""),
    ], "code", [], [], []),
    ("claim_status", [
        ("code", "varchar(10)", "varchar(10)", False, ""),
        ("meaning", "varchar(40)", "varchar(40)", False, ""),
    ], "code", [], [], []),
    ("patient", [
        ("patient_id", "integer GENERATED ALWAYS AS IDENTITY", "integer", False, ""),
        ("family_name", "varchar(40)", "varchar(40)", False, ""),
        ("given_name", "varchar(30)", "varchar(30)", False, ""),
        ("birth_date", "date", "date", True, ""),
        ("sex_code", "char(1)", "char(1)", False, ""),
        ("payer_id", "integer", "integer", True, ""),
        ("phone", "varchar(20)", "varchar(20)", True, ""),
        ("is_active", "boolean", "boolean", False, ""),
        ("created_by", "varchar(30)", "varchar(30)", False, ""),
    ], "patient_id", [("sex_code", "sex_codes", "code"), ("payer_id", "payer", "payer_id")], [], []),
    ("visit", [
        ("visit_id", "integer GENERATED ALWAYS AS IDENTITY", "integer", False, ""),
        ("patient_id", "integer", "integer", False, ""),
        ("visit_at", "timestamp", "timestamp", False, ""),
        ("clinician", "varchar(60)", "varchar(60)", True, ""),
        ("reason", "varchar(200)", "varchar(200)", True, ""),
    ], "visit_id", [("patient_id", "patient", "patient_id")], [], []),
    ("charge", [
        ("charge_id", "integer GENERATED ALWAYS AS IDENTITY", "integer", False, ""),
        ("patient_id", "integer", "integer", False, ""),
        ("visit_id", "integer", "integer", True, ""),
        ("procedure_code", "char(5)", "char(5)", False, ""),
        ("amount", "numeric(9,2)", "numeric(9,2)", False, ""),
        ("copay", "numeric(9,2)", "numeric(9,2)", False, "DEFAULT 0"),
        ("posted_at", "timestamp", "timestamp", False, ""),
    ], "charge_id", [("patient_id", "patient", "patient_id"), ("visit_id", "visit", "visit_id")], [], ["amount > 0"]),
    ("claim", [
        ("claim_id", "integer GENERATED ALWAYS AS IDENTITY", "integer", False, ""),
        ("claim_number", "varchar(15)", "varchar(15)", False, ""),
        ("patient_id", "integer", "integer", False, ""),
        ("amount", "numeric(9,2)", "numeric(9,2)", False, ""),
        ("status", "varchar(10)", "varchar(10)", False, ""),
        ("entered_at", "timestamp", "timestamp", False, ""),
        ("source_system", "varchar(20)", "varchar(20)", False, ""),
    ], "claim_id", [("patient_id", "patient", "patient_id"), ("status", "claim_status", "code")], ["claim_number"], []),
]


def tsql_schema():
    out = ["-- The legacy clinic database, as deployed (SQL Server). Invented sample data model.", ""]
    for table, cols, pk, fks in OLD:
        lines = []
        for name, t, _, null, extra in cols:
            lines.append(f"    {name} {t} {'NULL' if null else 'NOT NULL'}{' ' + extra if extra else ''}")
        lines.append(f"    CONSTRAINT PK_{table} PRIMARY KEY ({pk})")
        for col, ref, refcol in fks:
            lines.append(f"    CONSTRAINT FK_{table}_{ref} FOREIGN KEY ({col}) REFERENCES dbo.{ref} ({refcol})")
        out.append(f"CREATE TABLE dbo.{table} (\n" + ",\n".join(lines) + "\n);\nGO")
    return "\n".join(out) + "\n"


def sqlite_schema(tables, new):
    out = []
    for t in tables:
        table, cols, pk = t[0], t[1], t[2]
        fks = t[3]
        unique = t[4] if new else []
        checks = t[5] if new else []
        lines = []
        for name, _, st, null, extra in cols:
            if name == pk and st == "integer":
                lines.append(f"    {name} integer PRIMARY KEY")
                continue
            lines.append(f"    {name} {st} {'NULL' if null else 'NOT NULL'}{' ' + extra if extra else ''}")
        if not any(c[0] == pk and c[2] == "integer" for c in cols):
            lines.append(f"    PRIMARY KEY ({pk})")
        for u in unique:
            lines.append(f"    UNIQUE ({u})")
        for c in checks:
            lines.append(f"    CHECK ({c})")
        for col, ref, refcol in fks:
            lines.append(f"    FOREIGN KEY ({col}) REFERENCES {ref} ({refcol})")
        out.append(f"CREATE TABLE {table} (\n" + ",\n".join(lines) + "\n);")
    return "\n".join(out) + "\n"


def pg_schema():
    out = ["-- The rebuilt clinic database (PostgreSQL). Invented sample data model.", ""]
    for table, cols, pk, fks, unique, checks in NEW:
        lines = []
        for name, t, _, null, extra in cols:
            lines.append(f"    {name} {t} {'NULL' if null else 'NOT NULL'}{' ' + extra if extra else ''}")
        lines.append(f"    CONSTRAINT {table}_pkey PRIMARY KEY ({pk})")
        for u in unique:
            lines.append(f"    CONSTRAINT {table}_{u}_key UNIQUE ({u})")
        for i, c in enumerate(checks, 1):
            lines.append(f"    CONSTRAINT {table}_check{i} CHECK ({c})")
        for col, ref, refcol in fks:
            lines.append(f"    CONSTRAINT {table}_{col}_fkey FOREIGN KEY ({col}) REFERENCES {ref} ({refcol})")
        out.append(f"CREATE TABLE {table} (\n" + ",\n".join(lines) + "\n);")
    return "\n".join(out) + "\n"


# ── the old rows ───────────────────────────────────────────────────────────────────────────────────────────────
LAST = ["ABERNATHY", "BALDWIN", "CASTILLO", "DUNMORE", "ELLSWORTH", "FAIRCHILD", "GALLOWAY", "HOLLOWAY", "INGRAM",
        "JESSUP", "KINCAID", "LOCKHART", "MERRIWEATHER", "NAKAMURA", "OYELARAN", "PRENTICE", "QUAYLE", "RASMUSSEN",
        "SINCLAIR", "THORNBURY", "UNDERHILL", "VASQUEZ", "WHITCOMB", "YARBROUGH"]
FIRST = ["ADA", "BENNETT", "CORA", "DESMOND", "EVELYN", "FLETCHER", "GRETA", "HOLLIS", "IRIS", "JASPER", "KEZIAH",
         "LEOPOLD", "MAEVE", "NOLAN", "OPAL", "PERCIVAL", "ROSALIE", "SILAS", "THEA", "WALLACE"]
CLINICIANS = ["DR. ORTEGA", "DR. HALVORSEN", "NP PEMBROKE", "DR. ADEYEMI", None]
REASONS = ["Annual physical", "Follow-up, blood pressure", "Sore throat", "Medication review", "Knee pain", None]
PROCS = ["99213", "99214", "36415", "82947", "90837", "99396"]


def rows():
    rng = random.Random(20261124)
    payers = [
        ("SUN01", "SUN PLAN", 20.00, "5555550199"),
        ("MDCR", "MEDICARE PART B", 20.00, None),
        ("BLUE7", "BLUE VALLEY HEALTH", None, "5555550142"),   # no copay on file: the new column is required
        ("SELF", "SELF PAY", 100.00, None),
    ]
    patients, visits, charges, claims = [], [], [], []
    formats = [
        lambda y, m, d: f"{m:02d}/{d:02d}/{y}",      # the 2004 screen
        lambda y, m, d: f"{y}{m:02d}{d:02d}",        # the 2011 import
        lambda y, m, d: f"{y}-{m:02d}-{d:02d}",      # the web form
        lambda y, m, d: f"{m}/{d}/{y}",              # typed by hand
    ]
    for i in range(1, 41):
        last, first = LAST[(i * 7) % len(LAST)], FIRST[(i * 11) % len(FIRST)]
        y, m, d = 1940 + (i * 13) % 60, 1 + (i * 5) % 12, 1 + (i * 3) % 28
        dob = formats[i % 4](y, m, d)
        name = f"{last}, {first}"
        sex = "MF"[i % 2]
        payer = payers[i % 4][0]
        active = "Y" if i % 9 else "N"
        deleted = 0
        # The planted problems, one a row:
        if i == 5: dob = "13/45/1980"            # not a date in any format
        if i == 6: dob = None                   # unknown
        if i == 7: dob = ""                     # blank, not null
        if i == 8: name = "CHER"                # one name, no comma
        if i == 9: sex = "X"                    # a code the new table doesn't have
        if i == 10: sex = None                  # unknown sex: the new column is required
        if i == 11: name = "VAN DER BERG-ELLINGTON-WORTHINGTONSMYTHE, ARABELLA"   # family name over 40
        if i == 12: payer = None                # self-referred: no payer
        if i == 13: active = "y"                # lower case
        if i == 14: deleted = 1                 # deleted: not migrated
        if i == 15: name = "  LOCKHART ,  NOLAN  "   # stray spaces
        phone = f"55555501{i:02d}" if i % 3 else None
        fax = f"55555502{i:02d}" if i % 10 == 0 else None
        entered = ["FRONTDESK", "IMPORT2011", "WEBFORM", None][i % 4]
        patients.append((i, name, dob, sex, payer, active, phone, fax, deleted, entered))
    vid = 0
    for p in range(1, 41):
        for k in range(1 + p % 3):
            vid += 1
            when = f"2026-{1 + (p + k) % 10:02d}-{1 + (p * 7 + k) % 27:02d} {8 + (p + k) % 9:02d}:{(p * 13) % 60:02d}:00"
            reason = REASONS[(p + k) % len(REASONS)]
            if vid == 17:
                reason = ("Patient reports intermittent dizziness on standing, worse in the mornings, with occasional "
                          "blurred vision and a history of falls; family asks about medication interactions and whether "
                          "the new prescription from the cardiology referral should be continued.")   # over 200
            visits.append((vid, p, when, CLINICIANS[(p + k) % len(CLINICIANS)], reason))
    cid = 0
    for v in visits:
        vid_, pat, when = v[0], v[1], v[2]
        for k in range(1 + vid_ % 2):
            cid += 1
            proc = PROCS[(vid_ + k) % len(PROCS)]
            amt = [125.00, 175.00, 25.00, 50.00, 180.00, 210.00][(vid_ + k) % 6]
            copay = round(amt * 0.2, 2) if (vid_ + k) % 4 else None
            if cid == 21: proc = "99213-25"           # a modifier pushed into the code: over 5 characters
            if cid == 22: amt = 0.00                  # breaks the new check (amount > 0)
            if cid == 23: amt = -25.00                # a reversal: breaks it too
            charges.append((cid, pat, vid_, proc, amt, copay, when))
    # Charges for patients purged long ago (no foreign key let them stay), and one with no visit.
    charges.append((cid + 1, 977, None, "99213", 125.00, 25.00, "2026-02-03 10:00:00"))
    charges.append((cid + 2, 981, None, "36415", 25.00, None, "2026-02-03 10:05:00"))
    charges.append((cid + 3, 3, None, "82947", 50.00, 10.00, "2026-03-04 09:30:00"))
    statuses = ["OPEN", "SENT", "PAID", "DENIED", "VOID "]   # VOID was keyed with a trailing space
    for n, p in enumerate(range(1, 41), 1):
        if p % 2 == 0:
            continue
        st = statuses[n % 5]
        if p == 9: st = "PEND"                  # a status the new table doesn't have
        if p == 13: st = "paid"                 # lower case
        claims.append((f"CLM{2026000 + n:07d}", p, 125.00 + n, st, f"2026-{1 + n % 10:02d}-15 12:00:00"))
    faxes = [(1, "5555550210", None), (2, "5555550220", None)]
    return {"tblPayer": payers, "tblPatient": patients, "tblVisit": visits, "tblCharge": charges, "tblClaim": claims,
            "FaxQueue": faxes}


def sql_literal(v):
    if v is None:
        return "NULL"
    if isinstance(v, (int, float)):
        return f"{v:.2f}" if isinstance(v, float) else str(v)
    return "'" + str(v).replace("'", "''") + "'"


def tsql_data(data):
    out = ["-- The legacy clinic database's rows (SQL Server). Every person and number is invented.", ""]
    for table, cols, pk, _ in OLD:
        names = [c[0] for c in cols]
        identity = any("IDENTITY" in c[1] for c in cols)
        if identity:
            out.append(f"SET IDENTITY_INSERT dbo.{table} ON;")
        for r in data[table]:
            out.append(f"INSERT INTO dbo.{table} ({', '.join(names)}) VALUES ({', '.join(sql_literal(v) for v in r)});")
        if identity:
            out.append(f"SET IDENTITY_INSERT dbo.{table} OFF;")
        out.append("GO")
    return "\n".join(out) + "\n"


def write_db(path, schema, data=None, lookups=False):
    if os.path.exists(path):
        os.remove(path)
    db = sqlite3.connect(path)
    db.executescript(schema)
    if data:
        for table, cols, _, _ in OLD:
            ph = ", ".join("?" for _ in cols)
            db.executemany(f"INSERT INTO {table} VALUES ({ph})", data[table])
    db.commit()
    db.close()


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    data = rows()
    with open(os.path.join(here, "old", "clinic-old.tsql.sql"), "w", newline="") as f:
        f.write(tsql_schema())
    with open(os.path.join(here, "old", "clinic-old.data.sql"), "w", newline="") as f:
        f.write(tsql_data(data))
    write_db(os.path.join(here, "old", "clinic-old.db"), sqlite_schema(OLD, False), data)
    with open(os.path.join(here, "new", "clinic-new.pg.sql"), "w", newline="") as f:
        f.write(pg_schema())
    lite = sqlite_schema(NEW, True)
    with open(os.path.join(here, "new", "clinic-new.sqlite.sql"), "w", newline="") as f:
        f.write("-- The rebuilt clinic database in SQLite's dialect (for the in-process tests).\n\n" + lite)
    write_db(os.path.join(here, "new", "clinic-new.db"), lite)
    print({t: len(r) for t, r in data.items()})
