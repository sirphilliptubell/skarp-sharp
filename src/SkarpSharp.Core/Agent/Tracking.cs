using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SkarpSharp.Agent;

/// <summary>
/// The tracking files of <c>skarp check --track</c>: one JSON file per session in
/// <c>&lt;home&gt;/tracked/&lt;yyyy-mm-dd&gt;-&lt;session&gt;.json</c> (home: <c>SKARP_HOME</c>, else <c>~/.skarp</c>; date = the
/// session start; <c>nosession</c> without a session id, one file per day). Updates take a lock file, re-read, merge
/// and write a temporary file that is renamed over the old one, so parallel subagents do not lose each other's entries.
/// Files older than the retention are deleted on each run.
/// </summary>
public sealed class TrackingStore
{
    public const string NoSession = "nosession";
    public const int Version = 1;

    readonly string _folder;
    readonly DateTimeOffset _now;

    public TrackingStore(string home, DateTimeOffset now)
    {
        _folder = Path.Combine(home, "tracked");
        _now = now;
    }

    public string Folder => _folder;

    /// <summary><c>SKARP_HOME</c>, else <c>~/.skarp</c>.</summary>
    public static string DefaultHome() => Configuration.SettingsLocations.Home();

    /// <summary>A session id usable in a file name (letters, digits, '-', '_' and '.'; others become '_').</summary>
    public static string SafeSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session)) return NoSession;
        var sb = new StringBuilder(session.Length);
        foreach (var c in session.Trim()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var s = sb.ToString().Trim('.');
        return s.Length == 0 ? NoSession : s.Length > 100 ? s[..100] : s;
    }

    static string DateText(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The session of a tracking file name (<c>2026-11-16-abc.json</c>: <c>abc</c>), or null.</summary>
    public static string? SessionOf(string fileName) =>
        DateOf(fileName) is not null && fileName.EndsWith(".json", StringComparison.Ordinal) ? fileName[11..^5] : null;

    /// <summary>The date in a tracking file name (<c>2026-11-16-abc.json</c>), or null.</summary>
    static DateTime? DateOf(string fileName) =>
        fileName.Length > 11 && DateTime.TryParseExact(fileName[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) && fileName[10] == '-' ? d : null;

    /// <summary>
    /// The file of a session: the newest existing <c>*-&lt;session&gt;.json</c> (its date is the session start), else a new
    /// one dated today. Without a session: today's shared <c>&lt;date&gt;-nosession.json</c>.
    /// </summary>
    public string SessionFile(string session)
    {
        if (session != NoSession && Directory.Exists(_folder))
        {
            var existing = Directory.GetFiles(_folder, "*-" + session + ".json")
                .Where(f => DateOf(Path.GetFileName(f)) is not null && Path.GetFileName(f)[11..] == session + ".json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
            if (existing is not null) return existing;
        }
        return Path.Combine(_folder, $"{DateText(_now)}-{session}.json");
    }

    public static TrackingState? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            var state = JsonSerializer.Deserialize(text, AgentJsonContext.Default.TrackingState);
            return state is { Version: Version } ? state : null;
        }
        catch (JsonException) { return null; } // a damaged file starts over (fail open)
    }

    /// <summary>
    /// The previous entry of a file for an agent: in this session's file, else the newest one in the session's other
    /// files (for <c>nosession</c>: earlier days) within <paramref name="lookbackDays"/>.
    /// </summary>
    public (TrackedFile? Entry, string? From) Previous(string session, string agent, string fullPath, int lookbackDays)
    {
        var current = SessionFile(session);
        if (Read(current) is { } state && Entry(state, agent, fullPath) is { } e) return (e, Path.GetFileName(current));
        if (!Directory.Exists(_folder) || lookbackDays <= 0) return (null, null);
        var since = _now.UtcDateTime.Date.AddDays(-lookbackDays);
        foreach (var f in Directory.GetFiles(_folder, "*-" + session + ".json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (string.Equals(f, current, StringComparison.OrdinalIgnoreCase) || name.Length <= 11 || name[11..] != session + ".json") continue;
            if (DateOf(name) is not { } d || d < since) continue;
            if (Read(f) is { } old && Entry(old, agent, fullPath) is { } entry) return (entry, name);
        }
        return (null, null);
    }

    static TrackedFile? Entry(TrackingState state, string agent, string fullPath) =>
        state.Agents.TryGetValue(agent, out var a) && a.Files.TryGetValue(Key(fullPath), out var f) ? f : null;

    /// <summary>The key of a file: its absolute path (case kept; Windows paths compare as written by the agent).</summary>
    public static string Key(string fullPath) => Path.GetFullPath(fullPath);

    /// <summary>Reads, changes and writes the session file under a lock.</summary>
    public void Update(string session, Action<TrackingState> change)
    {
        Directory.CreateDirectory(_folder);
        var path = SessionFile(session);
        using (Lock(path))
        {
            var state = Read(path) ?? new TrackingState { Version = Version, Session = session, Started = Paths.UtcText(_now) };
            change(state);
            Paths.WriteAtomic(path, JsonSerializer.Serialize(state, AgentJsonContext.Default.TrackingState));
        }
    }

    /// <summary>A lock file next to the state file (created exclusively, deleted on close); a lock older than 10 s is
    /// taken over (a crashed writer). Gives up after about 3 s and writes anyway (fail open).</summary>
    static IDisposable? Lock(string path)
    {
        var lockPath = path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(lockPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) > TimeSpan.FromSeconds(10)) File.Delete(lockPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Thread.Sleep(10);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>Deletes tracking files whose session started, and that were last written, more than <paramref name="days"/> ago.</summary>
    public int Cleanup(int days)
    {
        if (!Directory.Exists(_folder)) return 0;
        var limit = _now.UtcDateTime.Date.AddDays(-days);
        int n = 0;
        foreach (var f in Directory.GetFiles(_folder, "*.json"))
        {
            if (DateOf(Path.GetFileName(f)) is not { } d || d >= limit) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(f) >= _now.UtcDateTime.AddDays(-days)) continue;
                File.Delete(f);
                n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }
}
