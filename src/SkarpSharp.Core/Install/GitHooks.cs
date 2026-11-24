namespace SkarpSharp.Install;

/// <summary>
/// What <c>skarp install git-hooks</c> asks for. <see cref="PrePush"/>: true writes the push hook, false
/// removes Skarp's push hook (<c>--no-pre-push</c>), null leaves an existing one as it is.
/// </summary>
public sealed record GitHookSpec
{
    /// <summary>The program: plain <c>skarp</c> found on PATH, or the <c>--command</c> path.</summary>
    public string Command { get; init; } = "skarp";
    public bool CommandGiven { get; init; }
    public bool? PrePush { get; init; }
    /// <summary>The push hook keeps the semantic tier (<c>--semantic</c>).</summary>
    public bool Semantic { get; init; }
    /// <summary>No guard: a missing program blocks the commit (<c>--strict</c>).</summary>
    public bool Strict { get; init; }
    /// <summary>A shared script in <c>.githooks/</c> (or a hook manager's snippet) instead of the local git config.</summary>
    public bool Shared { get; init; }
}

/// <summary>One git hook event Skarp installs: its config name, the event and the noun for messages.</summary>
public sealed class GitHookKind(string name, string gitEvent, string noun, string nouns)
{
    /// <summary>The friendly name in <c>hook.&lt;name&gt;.*</c>.</summary>
    public string Name { get; } = name;
    public string Event { get; } = gitEvent;
    public string Noun { get; } = noun;
    public string Nouns { get; } = nouns;
    public string CommandKey => $"hook.{Name}.command";
    public string EventKey => $"hook.{Name}.event";
}

/// <summary>A git config change as the arguments after <c>git</c>: set a key to one value (<c>--replace-all</c>), or unset
/// every value of it. Plain string arrays: a list of them already exists in the binary (Native AOT size).</summary>
public static class GitConfigOp
{
    public static string[] Set(string key, string value) => ["config", "--local", "--replace-all", key, value];
    public static string[] Unset(string key) => ["config", "--local", "--unset-all", key];
    public static bool IsUnset(string[] op) => op.Length == 4;
    public static string Key(string[] op) => op[3];
}

/// <summary>Skarp's entry for one hook in the local git config: every value of its two keys, in file order.</summary>
public sealed class GitHookEntry
{
    public required GitHookKind Kind { get; init; }
    public List<string> Commands { get; } = [];
    public List<string> Events { get; } = [];
    /// <summary>The <c>event</c> key came before the <c>command</c> key in the file (Skarp writes command first).</summary>
    public bool EventFirst { get; set; }
    public bool Present => Commands.Count > 0 || Events.Count > 0;
    /// <summary>One command and the one right event: what Skarp writes.</summary>
    public bool Complete => Commands.Count == 1 && Events.Count == 1 && Events[0] == Kind.Event;
    public string? Command => Commands.Count > 0 ? Commands[0] : null;
}

/// <summary>
/// The git-config hook model as pure functions. Skarp's entries are
/// <c>hook.skarp-pre-commit.*</c> and <c>hook.skarp-pre-push.*</c>; install sets <c>command</c> before <c>event</c> (an
/// event without a command makes every commit fail), remove unsets <c>event</c> first; each key gets exactly one value
/// (never <c>--append</c>). The command runs through git's <c>sh -c</c>, which appends <c>"$@"</c>, so it ends in a simple
/// command.
/// </summary>
public static class GitHooks
{
    public static readonly GitHookKind Commit = new("skarp-pre-commit", "pre-commit", "commit", "commits");
    public static readonly GitHookKind Push = new("skarp-pre-push", "pre-push", "push", "pushes");
    public static readonly GitHookKind[] Kinds = [Commit, Push];

    /// <summary>The oldest git with config-based hooks.</summary>
    public const int MinMajor = 2, MinMinor = 54;

    /// <summary>The program as the shell sees it: <c>skarp</c>, or the path with forward slashes in single quotes.</summary>
    public static string Program(GitHookSpec spec) => spec.CommandGiven ? "'" + spec.Command.Replace('\\', '/') + "'" : "skarp";

    /// <summary>Why <paramref name="command"/> cannot be written (a single quote), or null.</summary>
    public static string? CommandError(string command) =>
        command.Contains('\'') ? "--command cannot contain a single quote (the hook quotes the path with single quotes)"
        : command.Trim().Length == 0 ? "--command needs a program" : null;

    /// <summary>The guard: a missing program lets the commit or push through with one line (left out with <c>--strict</c>).</summary>
    public static string Guard(GitHookSpec spec, GitHookKind kind) =>
        $"command -v {Program(spec)} >/dev/null 2>&1 || {{ echo 'Skarp: not found, {kind.Noun} not checked; install Skarp to check {kind.Nouns}' >&2; exit 0; }}";

    /// <summary>The Skarp call of a hook (no <c>exec</c>).</summary>
    public static string Call(GitHookSpec spec, GitHookKind kind) => kind == Commit
        ? $"{Program(spec)} check --staged --gate"
        : $"{Program(spec)} analyze --gate --no-history{(spec.Semantic ? "" : " --no-semantic")} --push-check";

    /// <summary>The value of <c>hook.&lt;name&gt;.command</c>.</summary>
    public static string Command(GitHookSpec spec, GitHookKind kind) =>
        (spec.Strict ? "" : Guard(spec, kind) + "; ") + "exec " + Call(spec, kind);

    /// <summary>The hooks an install writes: the commit hook, and the push hook with <c>--pre-push</c>.</summary>
    public static GitHookKind[] Written(GitHookSpec spec) => spec.PrePush == true ? Kinds : [Commit];

    /// <summary>Skarp's entry for <paramref name="kind"/> from <c>git config --local --list -z</c> output
    /// (<c>key\nvalue\0</c> records).</summary>
    public static GitHookEntry Read(string list, GitHookKind kind)
    {
        var entry = new GitHookEntry { Kind = kind };
        foreach (var record in list.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int nl = record.IndexOf('\n');
            Add(entry, nl < 0 ? record : record[..nl], nl < 0 ? "" : record[(nl + 1)..]);
        }
        return entry;
    }

    static void Add(GitHookEntry e, string key, string value)
    {
        if (string.Equals(key, e.Kind.CommandKey, StringComparison.OrdinalIgnoreCase)) { e.Commands.Add(value); return; }
        if (!string.Equals(key, e.Kind.EventKey, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Commands.Count == 0) e.EventFirst = true;
        e.Events.Add(value);
    }

    /// <summary>The changes that make <paramref name="entry"/> run <paramref name="command"/>: command first, then
    /// event; nothing when it already does.</summary>
    public static List<string[]> Install(GitHookEntry entry, string command)
    {
        var ops = new List<string[]>();
        if (entry.Commands.Count != 1 || entry.Commands[0] != command) ops.Add(GitConfigOp.Set(entry.Kind.CommandKey, command));
        if (entry.Events.Count != 1 || entry.Events[0] != entry.Kind.Event) ops.Add(GitConfigOp.Set(entry.Kind.EventKey, entry.Kind.Event));
        return ops;
    }

    /// <summary>The changes that remove <paramref name="entry"/>: event first, then command.</summary>
    public static List<string[]> Remove(GitHookEntry entry)
    {
        var ops = new List<string[]>();
        if (entry.Events.Count > 0) ops.Add(GitConfigOp.Unset(entry.Kind.EventKey));
        if (entry.Commands.Count > 0) ops.Add(GitConfigOp.Unset(entry.Kind.CommandKey));
        return ops;
    }

    /// <summary>
    /// Reads an installed command back (for the setup screen): the program, strict (no guard) and, for the push hook,
    /// semantic. Null when it is not in the form Skarp writes.
    /// </summary>
    public static GitHookSpec? Parse(string command, GitHookKind kind)
    {
        int exec = command.LastIndexOf("exec ", StringComparison.Ordinal);
        if (exec < 0) return null;
        var call = command[(exec + 5)..];
        string program;
        if (call.StartsWith('\''))
        {
            int end = call.IndexOf('\'', 1);
            if (end < 0) return null;
            program = call[1..end];
        }
        else program = call.Split(' ')[0];
        var spec = new GitHookSpec
        {
            Command = program, CommandGiven = program != "skarp", Strict = exec == 0, Semantic = kind == Push && !call.Contains("--no-semantic", StringComparison.Ordinal),
        };
        return Command(spec, kind) == command ? spec : null;
    }

    /// <summary>The <c>git config</c> lines a person types (POSIX sh, PowerShell and cmd alike: the value in double
    /// quotes, which the commands Skarp writes allow).</summary>
    public static List<string> ConfigLines(GitHookSpec spec)
    {
        var lines = new List<string>();
        foreach (var kind in Written(spec))
        {
            lines.Add($"git config --local {kind.CommandKey} \"{Command(spec, kind)}\"");
            lines.Add($"git config --local {kind.EventKey} {kind.Event}");
        }
        return lines;
    }

    /// <summary>Parses <c>git version 2.56.1.windows.1</c> into major times 1000 plus minor (2056); -1 when it is not that form.</summary>
    public static int Version(string text)
    {
        var parts = text.Trim().Split(' ');
        if (parts.Length < 3 || parts[0] != "git" || parts[1] != "version") return -1;
        var numbers = parts[2].Split('.');
        if (numbers.Length < 2 || !int.TryParse(numbers[0], out var major) || !int.TryParse(numbers[1], out var minor)) return -1;
        return major * VersionScale + minor;
    }

    /// <summary>Config-based hooks exist (git 2.54 or newer) in a <see cref="Version"/> value.</summary>
    public static bool Supported(int version) => version >= MinMajor * VersionScale + MinMinor;

    const int VersionScale = 1000;
}
