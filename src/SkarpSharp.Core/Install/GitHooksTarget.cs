using SkarpSharp.Setup;

namespace SkarpSharp.Install;

/// <summary>
/// The "Git hooks" target of <c>skarp setup</c>, a front end to <c>skarp install git-hooks</c>.
/// It reads Skarp's entries in the local git config (or its block in <c>.githooks/</c>) back into the screen's values,
/// says whether applying them would change anything, and builds the <see cref="GitHooksCall"/> the menu runs after it
/// closes or prints. Nothing is written here.
/// </summary>
public sealed class GitHooksTarget
{
    static readonly GitHookSpec Defaults = new() { PrePush = false };

    readonly string _cwd;
    readonly GitRepo? _repo;

    /// <summary>Why the hooks cannot be installed here (git too old, no repository), or null.</summary>
    public string? Error { get; }
    public GitHookSpec Spec { get; private set; } = Defaults;
    public GitHookSpec Loaded { get; } = Defaults;
    public bool Remove { get; private set; }

    GitHooksTarget(string cwd, InstallSeams seams)
    {
        _cwd = Path.GetFullPath(cwd);
        (_repo, Error) = GitRepo.Open(cwd, seams);
        if (_repo is not null) Loaded = Read(_repo);
        Spec = Loaded;
    }

    public static GitHooksTarget Open(string cwd, InstallSeams seams) => new(cwd, seams);

    /// <summary>The values of what is installed: the local entries, else Skarp's block in <c>.githooks/</c>, else the defaults.</summary>
    static GitHookSpec Read(GitRepo repo)
    {
        var commit = repo.Entry(GitHooks.Commit);
        var push = repo.Entry(GitHooks.Push);
        if (commit.Command is { } c && GitHooks.Parse(c, GitHooks.Commit) is { } spec)
        {
            var p = push.Command is { } pc ? GitHooks.Parse(pc, GitHooks.Push) : null;
            return spec with { PrePush = push.Present, Semantic = p?.Semantic ?? false };
        }
        bool shared = SharedBlock(repo, GitHooks.Commit);
        return shared ? Defaults with { Shared = true, PrePush = SharedBlock(repo, GitHooks.Push) } : Defaults;
    }

    static bool SharedBlock(GitRepo repo, GitHookKind kind)
    {
        var path = Path.Combine(repo.Top, GitHookScripts.Folder, kind.Event);
        return File.Exists(path) && GitHookScripts.Find(File.ReadAllText(path)) is not null;
    }

    public bool Changed => Remove || Spec != Loaded;

    /// <summary>The config file (local) or the scripts' folder (shared), shortened as the menu shows paths.</summary>
    public string Where => _repo is null ? "" : LevelModel.Shorten(Spec.Shared ? Path.Combine(_repo.Top, GitHookScripts.Folder) : _repo.ConfigPath, _cwd);

    /// <summary>"installed", "installed, differs", "not installed", or why it cannot be.</summary>
    public string Status
    {
        get
        {
            if (_repo is null) return "cannot install: " + Error;
            if (Spec.Shared) return SharedBlock(_repo, GitHooks.Commit) ? "shared block present" : "no shared block";
            if (!_repo.CommitEntry.Present && !_repo.PushEntry.Present) return "not installed";
            if (Spec != Loaded || Loaded.Shared) return "installed, differs";
            return Complete() ? "installed" : "installed, differs";
        }
    }

    bool Complete()
    {
        foreach (var kind in GitHooks.Written(Spec))
            if (GitHooks.Install(_repo!.Entry(kind), GitHooks.Command(Spec, kind)).Count > 0) return false;
        return true;
    }

    /// <summary>Whether a <c>.skarp-baseline.json</c> is at the work tree root.</summary>
    public bool HasBaseline => _repo?.HasBaseline ?? true;

    // ---- changes ----------------------------------------------------------------------------------------------------

    /// <summary>The program; empty means plain <c>skarp</c>.</summary>
    /// <exception cref="SetupException">It has a single quote.</exception>
    public void SetProgram(string program)
    {
        var p = program.Trim();
        if (p.Length == 0) p = "skarp";
        if (GitHooks.CommandError(p) is { } why) throw new SetupException(why + ".");
        Spec = Spec with { Command = p, CommandGiven = p != "skarp" };
    }

    public void TogglePush() => Spec = Spec.PrePush == true ? Spec with { PrePush = false, Semantic = false } : Spec with { PrePush = true };

    /// <summary>The semantic tier of the push hook; on also turns the push hook on.</summary>
    public void ToggleSemantic() => Spec = Spec.Semantic ? Spec with { Semantic = false } : Spec with { Semantic = true, PrePush = true };

    public void ToggleShared() => Spec = Spec with { Shared = !Spec.Shared };

    public void ToggleStrict() => Spec = Spec with { Strict = !Spec.Strict };

    public void ToggleRemove() => Remove = !Remove;

    /// <summary>Del: the row back to its default (the push hook off also turns the semantic tier off).</summary>
    public void Reset(GitHookRow row) => Spec = row switch
    {
        GitHookRow.Push => Spec with { PrePush = Defaults.PrePush, Semantic = false },
        GitHookRow.Semantic => Spec with { Semantic = Defaults.Semantic },
        GitHookRow.Shared => Spec with { Shared = Defaults.Shared },
        GitHookRow.Strict => Spec with { Strict = Defaults.Strict },
        GitHookRow.Program => Spec with { Command = Defaults.Command, CommandGiven = false },
        _ => ResetRemove(row),
    };

    GitHookSpec ResetRemove(GitHookRow row)
    {
        if (row == GitHookRow.Remove) Remove = false;
        return Spec;
    }

    // ---- the call ---------------------------------------------------------------------------------------------------

    /// <summary>What the screen asks the installer for. The push hook: <c>--pre-push</c> when on, <c>--no-pre-push</c> only
    /// when off and installed locally.</summary>
    public GitHooksCall Call()
    {
        if (Remove) return new GitHooksCall(true, new GitHookSpec { Shared = Spec.Shared });
        bool? push = Spec.PrePush == true ? true : !Spec.Shared && LocalPushHook ? false : null;
        return new GitHooksCall(false, Spec with { PrePush = push, Semantic = push == true && Spec.Semantic });
    }

    /// <summary>A push hook is installed in the local git config (turning it off writes <c>--no-pre-push</c>).</summary>
    bool LocalPushHook => Loaded.PrePush == true && !Loaded.Shared;

    /// <summary>The arguments of <see cref="Call"/> (after <c>skarp</c>), checked by parsing them back.</summary>
    /// <exception cref="SetupException">Git hooks cannot be installed here, or the arguments do not parse back.</exception>
    public List<string> Args()
    {
        if (_repo is null) throw new SetupException("Git hooks cannot be installed here: " + Error);
        var call = Call();
        var args = call.Args();
        var (back, error) = GitHooksCall.Parse(args);
        if (back != call) throw new SetupException($"internal error: the install command does not parse back ({error ?? "another request"}); please report it");
        return args;
    }

    public CommandBuilder Build() => CommandBuilder.FromArgs(Args(), _cwd);
}
