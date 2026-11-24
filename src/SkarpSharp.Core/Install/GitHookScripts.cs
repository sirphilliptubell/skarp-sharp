namespace SkarpSharp.Install;

/// <summary>
/// Skarp's marked block in a shared hook script (<c>.githooks/pre-commit</c>,
/// <c>pre-push</c>), as pure functions on the text: a new file is a POSIX <c>sh</c> script with LF endings; an existing
/// shell script gets the block appended (never overwritten); a block already there is replaced in place; remove takes the
/// block out again, so install then remove gives the file back byte for byte (a file Skarp created is deleted).
/// </summary>
public static class GitHookScripts
{
    public const string Begin = "# >>> skarp >>>", End = "# <<< skarp <<<";
    /// <summary>What a new script starts with.</summary>
    public const string Shebang = "#!/bin/sh\n";
    public const string Folder = ".githooks";

    static readonly string[] Shells = ["sh", "bash", "dash", "ash", "ksh", "zsh"];

    /// <summary>The block, LF endings, ending in a line feed. In a script the call ends with <c>|| exit $?</c> instead
    /// of <c>exec</c>, so anything after the block still runs when Skarp passes; the push call passes on git's arguments
    /// (standard input, the ref lines, is inherited).</summary>
    public static string Block(GitHookSpec spec, GitHookKind kind)
    {
        var call = GitHooks.Call(spec, kind) + (kind == GitHooks.Push ? " \"$@\"" : "");
        var lines = new List<string> { Begin, $"# Skarp's {kind.Noun} check (skarp install git-hooks --shared); --remove --shared takes this block out." };
        if (spec.Strict) lines.Add(call + " || exit $?");
        else
        {
            lines.Add($"if command -v {GitHooks.Program(spec)} >/dev/null 2>&1; then");
            lines.Add($"  {call} || exit $?");
            lines.Add("else");
            lines.Add($"  echo 'Skarp: not found, {kind.Noun} not checked; install Skarp to check {kind.Nouns}' >&2");
            lines.Add("fi");
        }
        lines.Add(End);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A new script holding only the block.</summary>
    public static string NewFile(GitHookSpec spec, GitHookKind kind) => Shebang + Block(spec, kind);

    /// <summary>The script runs in a POSIX-like shell (its <c>#!</c> line names sh, bash, dash, ash, ksh or zsh, directly or
    /// through <c>env</c>); a file without <c>#!</c> runs in <c>sh</c> too.</summary>
    public static bool IsShell(string text)
    {
        if (!text.StartsWith("#!", StringComparison.Ordinal)) return text.Trim().Length == 0 || !text.Contains('\0');
        int nl = text.IndexOf('\n');
        var line = (nl < 0 ? text[2..] : text[2..nl]).Trim();
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var program = words[0][(words[0].LastIndexOf('/') + 1)..];
        if (program == "env" && words.Length > 1) program = words[1];
        return Array.IndexOf(Shells, program) >= 0;
    }

    /// <summary>Where Skarp's block is: the index of its first character and the length up to and including the line
    /// feed after <see cref="End"/>; null when there is none.</summary>
    public static (int Start, int Length)? Find(string text)
    {
        int begin = text.StartsWith(Begin, StringComparison.Ordinal) ? 0 : text.IndexOf("\n" + Begin, StringComparison.Ordinal) is var i and >= 0 ? i + 1 : -1;
        if (begin < 0) return null;
        int end = text.IndexOf(End, begin, StringComparison.Ordinal);
        if (end < 0) return null;
        int stop = end + End.Length;
        if (stop < text.Length && text[stop] == '\r') stop++;
        if (stop < text.Length && text[stop] == '\n') stop++;
        return (begin, stop - begin);
    }

    /// <summary>The script with Skarp's block: new, appended, or replaced in place. <paramref name="text"/> null: no file.</summary>
    public static string Install(string? text, GitHookSpec spec, GitHookKind kind)
    {
        var block = Block(spec, kind);
        if (text is null || text.Length == 0) return Shebang + block;
        if (Find(text) is { } at) return text[..at.Start] + block + text[(at.Start + at.Length)..];
        return text + (text.EndsWith('\n') ? "" : "\n") + block;
    }

    /// <summary>The script without Skarp's block; null when only what Skarp creates is left (delete the file).</summary>
    public static string? Remove(string text)
    {
        if (Find(text) is not { } at) return text;
        var rest = text[..at.Start] + text[(at.Start + at.Length)..];
        return rest == Shebang || rest.Length == 0 ? null : rest;
    }
}

/// <summary>
/// The hook managers <c>--shared</c> recognizes (Husky.Net, husky, lefthook,
/// pre-commit) and the snippet each gets. Skarp writes nothing into a manager's files: they are generated or owned.
/// </summary>
public static class HookManagers
{
    static readonly string[] LefthookFiles =
        ["lefthook.yml", "lefthook.yaml", ".lefthook.yml", ".lefthook.yaml", "lefthook.json", ".lefthook.json", "lefthook.toml", ".lefthook.toml", ".config/lefthook.yml", ".config/lefthook.yaml"];

    /// <summary>The managers of the work tree at <paramref name="top"/>, in the order above, each as its name, what was
    /// found (<c>.husky/</c>, <c>lefthook.yml</c>, ...) and the file the snippet goes to (string arrays: Native AOT size).</summary>
    public static List<string[]> Detect(string top)
    {
        var list = new List<string[]>();
        bool husky = Directory.Exists(Path.Combine(top, ".husky"));
        if (husky && System.IO.File.Exists(Path.Combine(top, ".husky", "task-runner.json"))) list.Add(["Husky.Net", ".husky/task-runner.json", ".husky/task-runner.json"]);
        else if (husky) list.Add(["husky", ".husky/", ".husky/pre-commit"]);
        if (First(top, LefthookFiles) is { } lefthook) list.Add(["lefthook", lefthook, lefthook]);
        if (First(top, PreCommitFiles) is { } preCommit) list.Add(["pre-commit", preCommit, preCommit]);
        return list;
    }

    static readonly string[] PreCommitFiles = [".pre-commit-config.yaml", ".pre-commit-config.yml"];

    static string? First(string top, string[] files)
    {
        foreach (var f in files) if (System.IO.File.Exists(Path.Combine(top, f))) return f;
        return null;
    }

    public static string Name(string[] manager) => manager[0];
    public static string Evidence(string[] manager) => manager[1];
    public static string File(string[] manager) => manager[2];

    /// <summary>The snippet for <paramref name="m"/>, LF endings, ending in a line feed.</summary>
    public static string Snippet(string[] m, GitHookSpec spec) => Name(m) switch
    {
        "Husky.Net" => HuskyNet(spec),
        "husky" => Husky(spec),
        "lefthook" => Lefthook(spec),
        _ => PreCommit(spec),
    };

    static string Husky(GitHookSpec spec)
    {
        var text = "# .husky/pre-commit (add these lines)\n" + GitHookScripts.Block(spec, GitHooks.Commit);
        return spec.PrePush == true ? text + "\n# .husky/pre-push (add these lines)\n" + GitHookScripts.Block(spec, GitHooks.Push) : text;
    }

    static string HuskyNet(GitHookSpec spec)
    {
        var program = spec.CommandGiven ? spec.Command.Replace('\\', '/') : "skarp";
        var tasks = new List<string>();
        foreach (var kind in GitHooks.Written(spec))
        {
            var args = GitHooks.Call(spec, kind)[(GitHooks.Program(spec).Length + 1)..].Replace(" ", "\", \"");
            tasks.Add($"    {{\n      \"name\": \"skarp-{kind.Event}\",\n      \"group\": \"{kind.Event}\",\n      \"command\": \"{program}\",\n      \"args\": [\"{args}\"]\n    }}");
        }
        return "// .husky/task-runner.json: add to \"tasks\"; the hook runs them with dotnet husky run --group <group>\n" + string.Join(",\n", tasks) + "\n";
    }

    static string Lefthook(GitHookSpec spec)
    {
        var text = $"pre-commit:\n  commands:\n    skarp:\n      glob: \"*.cs\"\n      run: {GitHooks.Call(spec, GitHooks.Commit)}\n";
        return spec.PrePush == true ? text + $"pre-push:\n  commands:\n    skarp:\n      use_stdin: true\n      run: {GitHooks.Call(spec, GitHooks.Push)}\n" : text;
    }

    static string PreCommit(GitHookSpec spec)
    {
        var text = "- repo: local\n  hooks:\n" +
            $"    - id: skarp\n      name: Skarp\n      entry: {GitHooks.Call(spec, GitHooks.Commit)}\n      language: system\n      pass_filenames: false\n      types: [c#]\n";
        return spec.PrePush == true
            ? text + $"    - id: skarp-push\n      name: Skarp (push)\n      entry: {GitHooks.Call(spec, GitHooks.Push)}\n      language: system\n      pass_filenames: false\n      always_run: true\n      stages: [pre-push]\n"
            : text;
    }
}
