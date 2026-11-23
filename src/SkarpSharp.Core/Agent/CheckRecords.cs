using System.Text;
using System.Text.Json;

namespace SkarpSharp.Agent;

/// <summary>
/// <c>&lt;root&gt;/.skarp/checked/</c>: the last check of each file, so every handle and <c>skarp show</c>
/// command a check prints can be answered without a new full run. A full <c>analyze</c> that saves its run removes the folder.
/// </summary>
public static class CheckRecords
{
    public const int Version = 1;

    static string Folder(string root) => Path.Combine(root, Paths.SkarpFolder, LastRun.ChecksFolder);

    static string FileFor(string root, string relativePath) =>
        Path.Combine(Folder(root), Paths.Sha256(Encoding.UTF8.GetBytes(relativePath))[..16] + ".json");

    public static void Write(string root, CheckRecord record)
    {
        Paths.EnsureSkarpFolder(root);
        Directory.CreateDirectory(Folder(root));
        Paths.WriteAtomic(FileFor(root, record.Path), JsonSerializer.Serialize(record, AgentJsonContext.Default.CheckRecord));
    }

    /// <summary>Every readable record of a root (damaged or other-version files are skipped).</summary>
    public static List<CheckRecord> ReadAll(string root)
    {
        var list = new List<CheckRecord>();
        var folder = Folder(root);
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(f), AgentJsonContext.Default.CheckRecord) is { Version: Version } r) list.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return list;
    }
}
