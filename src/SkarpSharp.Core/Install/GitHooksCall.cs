namespace SkarpSharp.Install;

/// <summary>
/// What a <c>skarp install git-hooks</c> line asks for, without how it runs (<c>--dry-run</c>, <c>--print</c>,
/// <c>--yes</c>): install or remove, and the spec. The setup screen builds one; the printed command is <see cref="Args"/>
/// and parses back to it (<see cref="Parse"/>).
/// </summary>
public sealed record GitHooksCall(bool Remove, GitHookSpec Spec)
{
    public const string Target = "git-hooks";

    /// <summary>The parsed line, with the run flags.</summary>
    internal sealed class Request
    {
        public bool Remove, Check, DryRun, Print, Yes, Help, Shared, Semantic, Strict;
        public bool? PrePush;
        public string? Command;
        /// <summary>The spec, built once (no record copies per option: Native AOT size).</summary>
        public GitHookSpec Spec => new() { Command = Command ?? "skarp", CommandGiven = Command is not null, PrePush = PrePush, Semantic = Semantic, Strict = Strict, Shared = Shared };
        public readonly List<string> InstallOnly = [];
        public int Options;
    }

    /// <summary>What an install line asks for (<paramref name="args"/> start with <c>install git-hooks</c>).</summary>
    public static (GitHooksCall? Call, string? Error) Parse(IReadOnlyList<string> args)
    {
        var (r, error) = ParseRequest(args);
        if (error is not null) return (null, error);
        if (r.Help || r.Check) return (null, "not an install or remove");
        return (new GitHooksCall(r.Remove, r.Spec), null);
    }

    internal static (Request, string?) ParseRequest(IReadOnlyList<string> args)
    {
        var r = new Request();
        for (int i = 2; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") { r.Help = true; return (r, null); }
            if (!a.StartsWith('-')) return (r, $"unexpected argument '{a}'");
            r.Options++;
            var error = a == "--command" ? Command(r, i + 1 < args.Count ? args[++i] : null) : Switch(r, a) ? null : $"unknown option {a}";
            if (error is not null) return (r, error);
        }
        return (r, Combination(r) ?? Exclusive(r));
    }

    /// <summary><c>--command &lt;program&gt;</c>; the error, or null.</summary>
    static string? Command(Request r, string? program)
    {
        if (program is null) return "--command needs a program";
        if (GitHooks.CommandError(program) is { } why) return why;
        r.Command = program;
        return null;
    }

    static bool Switch(Request r, string a)
    {
        switch (a)
        {
            case "--remove": r.Remove = true; return true;
            case "--check": r.Check = true; return true;
            case "--dry-run": r.DryRun = true; return true;
            case "--yes" or "-y": r.Yes = true; return true;
            case "--shared": r.Shared = true; return true;
            case "--print": r.Print = true; break;
            case "--pre-push": r.PrePush = true; break;
            case "--no-pre-push": r.PrePush = false; break;
            case "--semantic": r.Semantic = true; break;
            case "--strict": r.Strict = true; break;
            default: return false;
        }
        r.InstallOnly.Add(a);
        return true;
    }

    /// <summary>What <c>--check</c> and <c>--remove</c> take.</summary>
    static string? Combination(Request r)
    {
        if (r.Check && r.Options > 1) return "--check takes no other options";
        if (r.Remove && r.InstallOnly.Count > 0) return $"--remove takes --shared, --dry-run and --yes only (not {r.InstallOnly[0]})";
        return r.Remove && r.Command is not null ? "--remove takes --shared, --dry-run and --yes only (not --command): Skarp's entries are found by name" : null;
    }

    /// <summary>Options that exclude or need each other.</summary>
    static string? Exclusive(Request r)
    {
        if (r.Print && r.DryRun) return "--print and --dry-run: choose one (both write nothing)";
        if (r.InstallOnly.Contains("--pre-push") && r.InstallOnly.Contains("--no-pre-push")) return "--pre-push and --no-pre-push: choose one";
        if (r.Semantic && r.PrePush != true) return "--semantic applies to the push hook; add --pre-push";
        return r.Shared && r.PrePush == false ? "--no-pre-push applies to the local git config; with --shared, remove the block from .githooks/pre-push by hand or with --remove --shared" : null;
    }

    /// <summary>The arguments after <c>skarp</c>, non-default options only, in usage order; never <c>--yes</c>.</summary>
    public List<string> Args()
    {
        List<string> args = ["install", Target];
        if (Remove) args.Add("--remove");
        if (Spec.Shared) args.Add("--shared");
        if (Remove) return args;
        if (Spec.PrePush is { } push) args.Add(push ? "--pre-push" : "--no-pre-push");
        if (Spec.Semantic) args.Add("--semantic");
        if (Spec.Strict) args.Add("--strict");
        if (Spec.CommandGiven) args.AddRange(["--command", Spec.Command]);
        return args;
    }
}
