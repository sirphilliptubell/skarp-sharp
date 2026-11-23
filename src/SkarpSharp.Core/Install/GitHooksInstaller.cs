namespace SkarpSharp.Install;

/// <summary>How one <c>skarp install git-hooks</c> run writes.</summary>
public sealed class GitHooksRun
{
    public required GitHookSpec Spec { get; init; }
    public bool DryRun { get; init; }
    public bool Print { get; init; }
    public bool Yes { get; init; }
}

/// <summary>The repository a git-hooks command works on: its work tree root and git's own facts about it.</summary>
public sealed class GitRepo
{
    public required string Top { get; init; }
    /// <summary>The local config file (the common one in a linked worktree).</summary>
    public required string ConfigPath { get; init; }
    /// <summary><c>core.hooksPath</c>, or null.</summary>
    public string? HooksPath { get; init; }
    /// <summary>Skarp's entries in the local config.</summary>
    public required GitHookEntry CommitEntry { get; init; }
    public required GitHookEntry PushEntry { get; init; }

    public GitHookEntry Entry(GitHookKind kind) => kind == GitHooks.Commit ? CommitEntry : PushEntry;

    /// <summary>The repository of <paramref name="cwd"/>: the git version (2.54 or newer) and the work tree; the error
    /// otherwise.</summary>
    internal static (GitRepo?, string?) Open(string cwd, InstallSeams seams)
    {
        var version = seams.Git(cwd, "--version");
        if (!version.Ok) return (null, $"git could not be run ({version.Error.Trim()}); Skarp's git hooks need git {GitHooks.MinMajor}.{GitHooks.MinMinor} or newer");
        if (!GitHooks.Supported(GitHooks.Version(version.Out)))
            return (null, $"Skarp's git hooks need git {GitHooks.MinMajor}.{GitHooks.MinMinor} or newer (hooks in git config); this is {version.Out.Trim()}. Update git, or add Skarp to a hook script by hand (docs/git-hooks.md)");
        var top = seams.Git(cwd, "rev-parse", "--show-toplevel", "--git-common-dir");
        if (!top.Ok) return (null, "not in a git work tree (run it in the repository)");
        var lines = top.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var root = Path.GetFullPath(lines[0]);
        var common = Path.GetFullPath(Path.Combine(cwd, lines.Length > 1 ? lines[1] : ".git"));
        var hooksPath = seams.Git(root, "config", "--get", "core.hooksPath");
        var list = seams.Git(root, "config", "--local", "--list", "-z").Out;
        return (new GitRepo
        {
            Top = root, ConfigPath = Path.Combine(common, "config"), HooksPath = hooksPath.Ok ? hooksPath.Out.Trim() : null,
            CommitEntry = GitHooks.Read(list, GitHooks.Commit), PushEntry = GitHooks.Read(list, GitHooks.Push),
        }, null);
    }

    /// <summary>The baseline Skarp's hooks compare with is on disk at the work tree root.</summary>
    public bool HasBaseline => File.Exists(Path.Combine(Top, Baselines.Baseline.FileName));
}

/// <summary>
/// <c>skarp install git-hooks</c> and <c>--remove</c>. Local: Skarp's
/// entries in the repository's git config, written with <c>git config</c> in the safe order. Shared: a hook manager's
/// snippet (nothing written), or Skarp's block in <c>.githooks/</c>. Preview, notes, the question (or <c>--yes</c>),
/// <c>--dry-run</c> and <c>--print</c> as for <c>install claude</c>. Exit codes as <see cref="Commands.InstallCommand"/>.
/// </summary>
public sealed class GitHooksInstaller(GitHooksRun run, TextWriter output, TextWriter error, InstallSeams seams, string cwd)
{
    const int Refused = 2;
    readonly GitHookSpec _spec = run.Spec;

    /// <summary>The note on a missing baseline (spec item 7), also used by <c>--check</c> and the setup screen.</summary>
    public static readonly string[] BaselineNote =
    [
        "note: no .skarp-baseline.json yet. The hooks block only findings that are new or worse than in the baseline, so until",
        "      one is committed they let everything through with a note. Record and commit it:",
        "        skarp baseline .",
        "        git add .skarp-baseline.json",
    ];

    GitRepo? OpenRepo()
    {
        var (repo, why) = GitRepo.Open(cwd, seams);
        if (why is not null) error.WriteLine("skarp install: " + why + "; nothing written");
        return repo;
    }

    // ---- install ----------------------------------------------------------------------------------------------------

    public int Install()
    {
        if (OpenRepo() is not { } repo) return Refused;
        return _spec.Shared ? InstallShared(repo) : InstallLocal(repo);
    }

    int InstallLocal(GitRepo repo)
    {
        var preview = run.Print ? error : output;
        preview.WriteLine($"Skarp git hooks, local git config: {repo.ConfigPath}");
        var ops = new List<string[]>();
        foreach (var kind in GitHooks.Kinds) ops.AddRange(Plan(repo.Entry(kind), kind, preview));
        Notes(repo, preview);
        if (run.Print)
        {
            foreach (var line in GitHooks.ConfigLines(_spec)) output.WriteLine(line);
            return 0;
        }
        if (ops.Count == 0) { preview.WriteLine("Nothing to change: Skarp's git hooks are already installed."); return 0; }
        if (!Apply(repo, ops)) return Refused;
        if (!run.DryRun) output.WriteLine("git commit runs the check from now on; git commit --no-verify skips it once. skarp install git-hooks --check tests the setup.");
        return 0;
    }

    /// <summary>The changes for one hook, with its preview line: written when asked for, removed with
    /// <c>--no-pre-push</c>, otherwise left as it is.</summary>
    List<string[]> Plan(GitHookEntry entry, GitHookKind kind, TextWriter preview)
    {
        if (kind == GitHooks.Push && _spec.PrePush != true) return Unwritten(entry, preview);
        var command = GitHooks.Command(_spec, kind);
        var ops = GitHooks.Install(entry, command);
        if (ops.Count == 0) preview.WriteLine($"  {kind.Event}: already installed: hook.{kind.Name}: {command}");
        else if (entry.Command is { } old && old != command) { preview.WriteLine($"  {kind.Event}: update hook.{kind.Name}"); preview.WriteLine($"    old: {old}"); preview.WriteLine($"    new: {command}"); }
        else preview.WriteLine($"  {kind.Event}: {(entry.Present ? "repair" : "add")} hook.{kind.Name}: {command}");
        return ops;
    }

    /// <summary>An installed push hook without <c>--pre-push</c>: removed with <c>--no-pre-push</c>, else kept.</summary>
    List<string[]> Unwritten(GitHookEntry entry, TextWriter preview)
    {
        if (!entry.Present) return [];
        bool remove = _spec.PrePush == false;
        preview.WriteLine(remove ? $"  pre-push: remove hook.{entry.Kind.Name}: {entry.Command}" : $"  pre-push: kept as installed (--no-pre-push removes it): {entry.Command}");
        return remove ? GitHooks.Remove(entry) : [];
    }

    /// <summary>The notes and warnings of an install: hook managers, <c>core.hooksPath</c>, the program, the baseline.</summary>
    void Notes(GitRepo repo, TextWriter preview)
    {
        foreach (var m in HookManagers.Detect(repo.Top))
            preview.WriteLine($"note: {HookManagers.Name(m)} also runs hooks here ({HookManagers.Evidence(m)}); git runs both, so if it runs Skarp too, each commit is checked twice");
        if (repo.HooksPath is { Length: > 0 } hp) preview.WriteLine($"note: core.hooksPath is {hp}; hooks in git config run anyway, before the scripts there");
        ProgramNote(repo, preview);
    }

    /// <summary>A program that is not found, and the baseline note (spec item 7).</summary>
    void ProgramNote(GitRepo repo, TextWriter preview)
    {
        if (seams.Locate(_spec.Command.Replace('\\', '/'), repo.Top) is null)
            preview.WriteLine($"warning: {(_spec.CommandGiven ? _spec.Command + " not found" : "skarp is not on PATH (dotnet tool install -g Skarp, or --command <path>)")}: " +
                (_spec.Strict ? "with --strict every commit is blocked until it is" : "the hooks let everything through with a note until it is"));
        if (!repo.HasBaseline) foreach (var line in BaselineNote) preview.WriteLine(line);
    }

    bool Apply(GitRepo repo, List<string[]> ops)
    {
        if (run.DryRun) { output.WriteLine("dry run: nothing written"); return true; }
        if (!seams.Confirm($"Write {repo.ConfigPath}?", run.Yes, output, error)) return false;
        foreach (var op in ops)
        {
            var r = seams.Git(repo.Top, op);
            if (r.Ok || Gone(op, r)) continue;
            error.WriteLine($"skarp install: git config {GitConfigOp.Key(op)} failed: {r.Error.Trim()}");
            return false;
        }
        output.WriteLine($"Wrote {repo.ConfigPath}");
        return true;
    }

    /// <summary>An unset of a key that was already gone (git config exits 5).</summary>
    static bool Gone(string[] op, GitOutput r) => GitConfigOp.IsUnset(op) && r.ExitCode == NoSuchKey;

    const int NoSuchKey = 5;

    // ---- shared -----------------------------------------------------------------------------------------------------

    int InstallShared(GitRepo repo)
    {
        if (Snippets(repo)) return 0;
        var preview = run.Print ? error : output;
        preview.WriteLine($"Skarp git hooks, shared scripts in {GitHookScripts.Folder}/ (committed; each clone opts in)");
        List<string> paths = [], texts = [];
        foreach (var kind in GitHooks.Written(_spec))
            if (Script(repo, kind, preview) is { } text) { paths.Add(Path.Combine(repo.Top, GitHookScripts.Folder, kind.Event)); texts.Add(text); }
        ProgramNote(repo, preview);
        if (run.Print)
        {
            for (int i = 0; i < paths.Count; i++) { output.WriteLine($"==> {Relative(repo, paths[i])} <=="); output.Write(texts[i]); }
            return 0;
        }
        if (paths.Count == 0) preview.WriteLine("Nothing to change: Skarp's block is already in the scripts.");
        else if (!WriteScripts(repo, paths, texts)) return Refused;
        output.WriteLine($"Each clone uses them after: git config core.hooksPath {GitHookScripts.Folder}" +
            (repo.HooksPath == GitHookScripts.Folder ? $" (this clone already does)" : ""));
        return 0;
    }

    /// <summary>With a hook manager, its snippet and nothing written; false when there is none.</summary>
    bool Snippets(GitRepo repo)
    {
        var managers = HookManagers.Detect(repo.Top);
        foreach (var m in managers)
        {
            output.WriteLine($"{HookManagers.Name(m)} manages this repository's hooks ({HookManagers.Evidence(m)}): add Skarp to {HookManagers.File(m)} yourself; Skarp writes nothing there.");
            output.Write(HookManagers.Snippet(m, _spec));
        }
        if (managers.Count > 0 && !repo.HasBaseline) foreach (var line in BaselineNote) output.WriteLine(line);
        return managers.Count > 0;
    }

    static string Relative(GitRepo repo, string path) => Path.GetRelativePath(repo.Top, path).Replace('\\', '/');

    /// <summary>The new text of one script (null: nothing to change, or a script Skarp does not edit).</summary>
    string? Script(GitRepo repo, GitHookKind kind, TextWriter preview)
    {
        var path = Path.Combine(repo.Top, GitHookScripts.Folder, kind.Event);
        var rel = Relative(repo, path);
        string? text = File.Exists(path) ? File.ReadAllText(path) : null;
        if (text is not null && !GitHookScripts.IsShell(text))
        {
            preview.WriteLine($"  {rel}: not a shell script; add this block yourself:");
            preview.Write(GitHookScripts.Block(_spec, kind));
            return null;
        }
        var next = GitHookScripts.Install(text, _spec, kind);
        if (next == text) { preview.WriteLine($"  {rel}: already holds Skarp's block"); return null; }
        preview.WriteLine($"  {rel}: {(text is null ? "new file with" : GitHookScripts.Find(text) is null ? "append" : "update")} Skarp's block: {GitHooks.Call(_spec, kind)}");
        return next;
    }

    bool WriteScripts(GitRepo repo, List<string> paths, List<string> texts)
    {
        if (run.DryRun) { output.WriteLine("dry run: nothing written"); return true; }
        if (!seams.Confirm($"Write {GitHookScripts.Folder}/ ({paths.Count} script{(paths.Count == 1 ? "" : "s")})?", run.Yes, output, error)) return false;
        for (int i = 0; i < paths.Count; i++)
        {
            var (path, text) = (paths[i], texts[i]);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Agent.Paths.WriteAtomic(path, text);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"skarp install: cannot write {path}: {ex.Message}; --print writes the new content to standard output instead");
                return false;
            }
            output.WriteLine($"Wrote {path}");
            Executable(repo, path);
        }
        return true;
    }

    /// <summary>Marks a script executable for git: in the index when tracked, else the command to run after adding it.</summary>
    void Executable(GitRepo repo, string path)
    {
        var rel = Relative(repo, path);
        if (seams.Git(repo.Top, "ls-files", "--error-unmatch", "--", rel).Ok && seams.Git(repo.Top, "update-index", "--chmod=+x", "--", rel).Ok)
            output.WriteLine($"Marked {rel} executable in git's index (commit it)");
        else output.WriteLine($"Add it as executable: git add --chmod=+x {rel}");
    }

    // ---- remove -----------------------------------------------------------------------------------------------------

    public int Remove()
    {
        if (OpenRepo() is not { } repo) return Refused;
        return _spec.Shared ? RemoveShared(repo) : RemoveLocal(repo);
    }

    int RemoveLocal(GitRepo repo)
    {
        output.WriteLine($"Skarp git hooks, local git config: {repo.ConfigPath}");
        var ops = new List<string[]>();
        foreach (var kind in GitHooks.Kinds)
        {
            var entry = repo.Entry(kind);
            if (!entry.Present) continue;
            output.WriteLine($"  {entry.Kind.Event}: remove hook.{entry.Kind.Name}: {entry.Command ?? "(event without a command)"}");
            ops.AddRange(GitHooks.Remove(entry));
        }
        if (ops.Count == 0) { output.WriteLine("Nothing to remove: no Skarp git hooks in the local git config."); return 0; }
        return Apply(repo, ops) ? 0 : Refused;
    }

    int RemoveShared(GitRepo repo)
    {
        output.WriteLine($"Skarp git hooks, shared scripts in {GitHookScripts.Folder}/");
        List<string> paths = [];
        List<string?> texts = [];
        foreach (var kind in GitHooks.Kinds) Removal(repo, kind, paths, texts);
        if (paths.Count == 0) { output.WriteLine("Nothing to remove: no Skarp block in the scripts."); return 0; }
        if (run.DryRun) { output.WriteLine("dry run: nothing written"); return 0; }
        if (!seams.Confirm("Change the scripts?", run.Yes, output, error)) return Refused;
        for (int i = 0; i < paths.Count; i++)
        {
            if (texts[i] is { } text) WriteKeepingMode(paths[i], text);
            else File.Delete(paths[i]);
            output.WriteLine((texts[i] is null ? "Deleted " : "Wrote ") + paths[i]);
        }
        var folder = Path.Combine(repo.Top, GitHookScripts.Folder);
        if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
        return 0;
    }

    /// <summary>One script's removal (text null: delete the file), when it holds Skarp's block.</summary>
    void Removal(GitRepo repo, GitHookKind kind, List<string> paths, List<string?> texts)
    {
        var path = Path.Combine(repo.Top, GitHookScripts.Folder, kind.Event);
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        if (text is null || GitHookScripts.Find(text) is null) return;
        var rest = GitHookScripts.Remove(text);
        output.WriteLine($"  {Relative(repo, path)}: {(rest is null ? "delete (only Skarp's block was in it)" : "remove Skarp's block")}");
        paths.Add(path);
        texts.Add(rest);
    }

    /// <summary>Writes a script atomically, keeping its file mode (the executable bits) where the system has one.</summary>
    static void WriteKeepingMode(string path, string text)
    {
        if (OperatingSystem.IsWindows()) { Agent.Paths.WriteAtomic(path, text); return; }
        var mode = File.GetUnixFileMode(path);
        Agent.Paths.WriteAtomic(path, text);
        File.SetUnixFileMode(path, mode);
    }
}
