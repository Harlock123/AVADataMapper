# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers what the program does; this file covers
how the code is put together and the rules that keep it working.

## What this is

A data migration mapper in C# on .NET 10 and Avalonia 11.3: it reads an old database's schema and a new one's (live
catalogs of SQL Server, PostgreSQL and SQLite, or DDL scripts), and keeps a mapping (`.dmap`) of how each new table and
column is filled from the old. It is the step between rebuilding a system (AVALegacyLens, AVAUIDesigner) and proving
the rebuild matches (AVAParityCheck).

## Commands

```bash
dotnet build AVADataMapper.slnx
dotnet test AVADataMapper.slnx
dotnet run --project src/AVADataMapper [map.dmap]
cd samples && python3 make_samples.py        # remakes the sample databases (deterministic: tests count their rows)
MAPPER_SHOTS=/tmp/shots dotnet test --filter Screenshots                 # the README's pictures, headless
MAPPER_TEST_MSSQL="Server=localhost,11453;User Id=sa;Password=…;TrustServerCertificate=True" \
MAPPER_TEST_PG="Host=localhost;Port=15452;Username=postgres;Password=…;Database=postgres" dotnet test   # live servers
```

`dotnet test | head` stalls: write the output to a file and grep it. The live tests make a database of their own
(`mapper_test_…`) on each server and drop it after; throwaway containers for them:

```bash
docker run -d --name dm-postgres -e POSTGRES_PASSWORD=… -p 15452:5432 postgres:18
docker run -d --name dm-mssql -e ACCEPT_EULA=1 -e MSSQL_SA_PASSWORD='…' -p 11453:1433 mcr.microsoft.com/azure-sql-edge:latest
```

## Layout

- `src/AVADataMapper.Core/`: no Avalonia.
  - `Schema/`: `SchemaModel` (`Dialect`, `DataType` with its `TypeKind`, `Column`, `ForeignKey`, `Table`,
    `DatabaseSchema` with `LoadOrder`), `DdlReader` (T-SQL through ScriptDom; PostgreSQL and SQLite through a small
    parser of CREATE TABLE and ALTER TABLE … ADD CONSTRAINT; `Guess` says which dialect a script is in), `Catalogs`
    (live catalogs: SQL Server's sys views, PostgreSQL's pg_catalog, SQLite's pragmas; never a row), `SchemaFiles`
    (a SQLite file or a DDL script) and `SavedConnections` (connection strings by name, in the settings folder).
  - `Mapping/`: `MapFile` (the `.dmap`: `Side`, `TableMap`, `ColumnMap` with its `ColumnKind` and `Certainty`,
    `Decision`), `Transforms` (what a column map does to an old row's values, then `Fit` to the new column: type, length,
    precision, nulls; a value or a reason the row is rejected), `Expression` (the mapping's own small expression language
    for filters and computed columns: not SQL, checked when written), `MapChecks` (M rules: the mapping against both
    schemas before any row is read), `Names` (names as words: prefixes dropped,
    abbreviations spelled out, synonyms read the same), `Evidence` (an AVALegacyLens `.lens` read as plain JSON: who
    writes and reads each column, its rules tied to the columns their words name, dead tables), `Suggester` (fills a
    mapping's gaps: tables by name and columns, columns by name and type, keys through foreign keys, lookups into code
    tables, splits of a name column; each sure, likely or guessed, with its reason), `Coverage` (what's filled, what's
    required and empty, what's suggested, what's old and not read or dropped).
- `src/AVADataMapper/`: the window, built in code (`MainWindow.cs`: the trees, the mapping tab, undo;
  `MainWindow.Suggest.cs`: suggesting, confirming, the scan as evidence, the Coverage tab; `MainWindow.Checks.cs`: the
  Checks tab and the Try box; `MainWindow.Parts.cs`: the
  hooks later parts fill; `ConnectDialog.cs`).
- `tests/AVADataMapper.Tests/`: xUnit v3 on Avalonia.Headless. `LiveDatabases.cs` has the opt-in live servers.
- `samples/`: an invented legacy clinic database (`old/`: SQL Server DDL and rows, and a SQLite copy) and its rebuild
  (`new/`: PostgreSQL DDL, a SQLite copy), from `make_samples.py`; `clinic.dmap` maps one to the other.
  `old/code/` is the legacy program's (invented) VB.NET that uses the old database, and `old/clinic-old.lens` is
  AVALegacyLens's real scan of it with the old schema (its `root` set to ClinicOld): scan a folder holding `code/` and
  the T-SQL schema, without the rows script, whose INSERTs would make every column look written.

## Rules that keep it working

- **Core stays free of Avalonia.**
- **Schemas are read from catalogs and DDL, never from rows.** Reading rows is for profiling, dry runs and migrations.
- **A mapping never holds a connection string.** Sides name a file or a saved connection ("old", "new");
  `SavedConnections` keeps the strings in the settings folder, readable by the owner only.
- **A `.dmap` writes the same way every time** (`MapFile.ToJson`), so it diffs well in source control.
- **Every change to the mapping is one undo step** (`MainWindow.Change`), suggestions included.
- **Suggestions never change what's mapped.** `Suggester` only fills gaps, marks everything it adds sure, likely or
  guessed with a reason, and a person confirms it. Evidence makes a suggestion likelier; it never confirms one.
- **A window test that fails leaves its window open, and Avalonia's reset then fails with `fonts:SystemFonts`,** which
  hides the real failure and breaks the tests after it: close windows in `finally`.
- **One transform, everywhere.** The Try box, the dry run, the migration and the parity plan all go through
  `Transforms.Apply`, so what's tried is what's done. Expressions are the mapping's own language, never SQL passed to a
  database.
- **No real data in the repository.** Every person, payer and number in the samples is invented.

## Making a change

- Run `dotnet test` after any change.
- Pushes to `main` publish builds to the rolling `latest` pre-release; a `v*` tag makes a proper release.
