using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

/// <summary>The samples folder (found from the test's folder up), and copies of it for tests that write.</summary>
public static class Samples
{
    public static string Folder { get; } = Find();

    public static string Path(string name) => System.IO.Path.Combine(Folder, name);

    public static DatabaseSchema Old => SchemaFiles.Load(Path("old/clinic-old.db"));
    public static DatabaseSchema New => SchemaFiles.Load(Path("new/clinic-new.db"));
    public static MapFile Map => MapFile.Load(Path("clinic.dmap"));

    /// <summary>A copy of the samples in a temporary folder, for a test that writes (migrations, saved maps).</summary>
    public static string Copy()
    {
        var root = Directory.CreateTempSubdirectory("datamapper-samples-").FullName;
        foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            var to = System.IO.Path.Combine(root, System.IO.Path.GetRelativePath(Folder, f));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return root;
    }

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "samples");
            if (File.Exists(System.IO.Path.Combine(candidate, "clinic.dmap"))) return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find the samples folder.");
    }
}
