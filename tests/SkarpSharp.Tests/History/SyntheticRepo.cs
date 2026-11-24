using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SkarpSharp.Tests.History;

/// <summary>
/// Builds a git repository in a temporary folder with fixed author and committer dates and configuration isolated from
/// the machine (no system or global config, no network). Used to reproduce worked examples end to end.
/// </summary>
public sealed class SyntheticRepo : IDisposable
{
    readonly DirectoryInfo _home;

    public SyntheticRepo()
    {
        _home = Directory.CreateTempSubdirectory("skarp-git-");
        Root = Path.Combine(_home.FullName, "repo");
        Directory.CreateDirectory(Root);
        var globalConfig = Path.Combine(_home.FullName, "gitconfig");
        File.WriteAllText(globalConfig, "");
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            ["GIT_CEILING_DIRECTORIES"] = _home.FullName,
            ["HOME"] = _home.FullName,
            ["XDG_CONFIG_HOME"] = _home.FullName,
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        Git("init", "-q", "-b", "main");
        Git("config", "core.autocrlf", "false");
        Git("config", "core.safecrlf", "false");
        Git("config", "commit.gpgsign", "false");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.org");
    }

    public string Root { get; }

    /// <summary>Environment that isolates git from the machine's configuration; pass it to the history reader too.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    public void Write(string path, string content) => WriteBytes(path, Encoding.UTF8.GetBytes(content));

    public void WriteBytes(string path, byte[] content)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public void Delete(string path) => File.Delete(Path.Combine(Root, path));

    /// <summary>Stages everything and commits with the given author and a fixed date (author and committer).</summary>
    public string Commit(string message, DateTimeOffset when, string author = "Alice <alice@x.org>")
    {
        Git("add", "-A");
        var date = "@" + when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000";
        Git(new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "commit", "-q", "--allow-empty", "--no-verify", "--author", author, "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] args) => Git(null, args);

    public string Git(IReadOnlyDictionary<string, string?>? extra, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Environment) psi.Environment[k] = v;
        if (extra is not null)
            foreach (var (k, v) in extra) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var err = errTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return output;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_home.FullName, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only
            _home.Delete(recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
