# AVA Data Mapper

Move a legacy system's data into its rebuild, and prove it arrived. AVA Data Mapper reads the old database's schema and
the new one's (SQL Server, PostgreSQL or SQLite, live or from DDL scripts) and keeps a mapping of how every new table
and column is filled from the old: copied, converted, looked up, split, joined, or through the new keys.

*Work in progress: milestone 5 of 7 (the migration, the script, the parity suite and the report).*

## Running it

```bash
dotnet run --project src/AVADataMapper -- samples/clinic.dmap
```

`samples/` has an invented legacy clinic database, its rebuild, and a mapping between them.

## License

MIT
