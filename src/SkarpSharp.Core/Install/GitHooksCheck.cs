namespace SkarpSharp.Install;

/// <summary>
/// <c>skarp install git-hooks --check</c>. Lists the git version, Skarp's two local entries (both
/// keys, their order, an orphaned <c>event</c>, duplicates), where the program resolves, Skarp's block in
/// <c>.githooks/</c>, hook managers (two checks per commit) and the baseline. Exit 1 on a problem.
/// </summary>
public sealed class GitHooksCheck(TextWriter output, InstallSeams seams, string cwd)
{
    int _problems;

    void Problem(string text) { output.WriteLine("problem: " + text); _problems++; }

    public int Run()
    {
        var (repo, why) = GitRepo.Open(cwd, seams);
        if (repo is null) { Problem(why!); return 1; }
        output.WriteLine($"git: {seams.Git(cwd, "--version").Out.Trim()}, ok; local git config: {repo.ConfigPath}");
        bool any = Entry(repo, repo.CommitEntry) | Entry(repo, repo.PushEntry) | Shared(repo);
        foreach (var m in HookManagers.Detect(repo.Top))
            output.WriteLine($"note: {HookManagers.Name(m)} also runs hooks here ({HookManagers.Evidence(m)}): if it runs Skarp too, each commit is checked twice");
        if (any && !repo.HasBaseline) foreach (var line in GitHooksInstaller.BaselineNote) output.WriteLine(line);
        output.WriteLine(!any ? "No Skarp git hooks installed (skarp install git-hooks adds them)." : _problems == 0 ? "All good." : $"Problems: {_problems}.");
        return _problems > 0 ? 1 : 0;
    }

    /// <summary>One local entry; false when it is absent.</summary>
    bool Entry(GitRepo repo, GitHookEntry e)
    {
        var name = "hook." + e.Kind.Name;
        output.WriteLine($"  {e.Kind.Event}: {(e.Present ? name + ": " + (e.Command ?? "(no command)") : "not installed")}");
        if (!e.Present) return false;
        if (e.Commands.Count == 0) Problem($"{name}.event without {name}.command stops every {e.Kind.Noun} (exit 128); skarp install git-hooks --remove clears it");
        else if (e.Events.Count == 0) Problem($"{name}.command without {name}.event never runs; skarp install git-hooks repairs it");
        else if (!e.Complete) Problem($"{name} needs one command and one event {e.Kind.Event}; skarp install git-hooks repairs it");
        else Program(repo, e);
        if (e.EventFirst) output.WriteLine($"note: {name}.event comes before .command in the file (Skarp writes command first); harmless while both are set");
        return true;
    }

    void Program(GitRepo repo, GitHookEntry e)
    {
        if (GitHooks.Parse(e.Command!, e.Kind) is not { } spec) { output.WriteLine("note: not the command Skarp writes (edited by hand?); its program is not checked"); return; }
        if (seams.Locate(spec.Command, repo.Top) is { } found) { output.WriteLine($"    program: {spec.Command} -> {found}"); return; }
        Problem($"{spec.Command} not found: " + (spec.Strict ? $"with --strict every {e.Kind.Noun} is blocked (exit 127)" : $"every {e.Kind.Noun} goes through unchecked with a note"));
    }

    /// <summary>Skarp's block in <c>.githooks/</c>; false when there is none.</summary>
    bool Shared(GitRepo repo)
    {
        bool any = false;
        foreach (var kind in GitHooks.Kinds)
        {
            var path = Path.Combine(repo.Top, GitHookScripts.Folder, kind.Event);
            if (!File.Exists(path) || GitHookScripts.Find(File.ReadAllText(path)) is null) continue;
            any = true;
            output.WriteLine($"  shared: {GitHookScripts.Folder}/{kind.Event} holds Skarp's block");
        }
        if (any && repo.HooksPath != GitHookScripts.Folder) output.WriteLine($"note: this clone does not run {GitHookScripts.Folder}/: git config core.hooksPath {GitHookScripts.Folder}");
        return any;
    }
}
