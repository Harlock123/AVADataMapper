using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVADataMapper.Core.Schema;

/// <summary>Schemas from files: a SQLite database, or a DDL script (its dialect guessed from its words unless given).</summary>
public static class SchemaFiles
{
    public static bool IsSqlite(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[16];
            return f.Read(head, 0, 16) == 16 && System.Text.Encoding.ASCII.GetString(head, 0, 15) == "SQLite format 3";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static DatabaseSchema Load(string path, Dialect? dialect = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"There's no file {Path.GetFileName(path)}.");
        if (IsSqlite(path)) return Catalogs.ReadSqliteFile(path);
        var text = File.ReadAllText(path);
        var schema = DdlReader.Read(text, dialect ?? DdlReader.Guess(text), Path.GetFileName(path));
        if (schema.Tables.Count == 0) throw new FormatException($"{Path.GetFileName(path)} has no CREATE TABLE in it.");
        return schema;
    }
}

/// <summary>
/// Connections saved by name ("old", "new") in the settings folder, so a mapping file never holds a connection string.
/// The file is readable by its owner only.
/// </summary>
public sealed class SavedConnections(string folder)
{
    public string Path => System.IO.Path.Combine(folder, "connections.json");

    public (Dialect Dialect, string Connection)? Get(string name)
    {
        var all = Read();
        return all[name] is JsonObject o && Enum.TryParse<Dialect>((string?)o["dialect"], true, out var d) && (string?)o["connection"] is { } c ? (d, c) : null;
    }

    public void Set(string name, Dialect dialect, string connection)
    {
        var all = Read();
        all[name] = new JsonObject { ["dialect"] = dialect.ToString(), ["connection"] = connection };
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private JsonObject Read()
    {
        try
        {
            return File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject o ? o : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
