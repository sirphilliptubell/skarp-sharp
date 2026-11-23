using System.Globalization;

namespace SkarpSharp.Install;

/// <summary>
/// The arguments of <c>skarp install</c> (shared so the hooks screen can parse a printed
/// command back without depending on the Commands namespace, <see cref="InstallCall.Parse"/>).
/// </summary>
public static class InstallArgs
{
    internal sealed class Request
    {
        public string? Target;
        public ClaudeScope Scope = ClaudeScope.Local;
        public bool NoStop, NoCompact, Gate, Semantic, Force, DryRun, Print, Yes, Remove, Check, Help;
        /// <summary><c>--hooks</c> and <c>--mcp</c> as given (neither: both parts).</summary>
        public bool HooksGiven, McpGiven;
        public bool Hooks => HooksGiven || !McpGiven;
        public bool Mcp => McpGiven || !HooksGiven;
        /// <summary><c>--include-notices</c> true, <c>--no-include-notices</c> false, neither null.</summary>
        public bool? Notice;
        public int? MaxLines;
        public string? Command;
        /// <summary>The options only an install takes (for the <c>--remove</c> message).</summary>
        public readonly List<string> InstallOnly = [];
        public int Options;

        public HookSpec Spec() => new()
        {
            Command = Command ?? "skarp", CommandGiven = Command is not null, Stop = !NoStop, Compact = !NoCompact,
            Gate = Gate, Semantic = Semantic, MaxLines = MaxLines, Notice = Notice,
        };
    }

    /// <summary>The folder the local and project scopes live in: the git root of <paramref name="cwd"/>, else <paramref name="cwd"/>.</summary>
    public static string ProjectDir(string cwd)
    {
        var full = Path.GetFullPath(cwd);
        return Reporting.ReportLayout.RepositoryRoot(full) ?? full;
    }

    internal static (Request, string?) Parse(IReadOnlyList<string> args)
    {
        var r = new Request();
        for (int i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") { r.Help = true; return (r, null); }
            if (!a.StartsWith('-'))
            {
                if (r.Target is not null) return (r, $"unexpected argument '{a}'");
                r.Target = a;
                continue;
            }
            r.Options++;
            var error = Switch(r, a) ? null : Valued(r, a, i + 1 < args.Count ? args[++i] : null);
            if (error is not null) return (r, error);
        }
        return (r, Combination(r));
    }

    /// <summary>A switch without a value; false when <paramref name="a"/> is not one.</summary>
    static bool Switch(Request r, string a)
    {
        switch (a)
        {
            case "--remove": r.Remove = true; return true;
            case "--check": r.Check = true; return true;
            case "--hooks": r.HooksGiven = true; r.Options--; return true;
            case "--mcp": r.McpGiven = true; r.Options--; return true;
            case "--dry-run": r.DryRun = true; return true;
            case "--yes" or "-y": r.Yes = true; return true;
            case "--print": r.Print = true; break;
            case "--no-stop": r.NoStop = true; break;
            case "--no-compact": r.NoCompact = true; break;
            case "--gate": r.Gate = true; break;
            case "--semantic": r.Semantic = true; break;
            case "--force": r.Force = true; break;
            case "--include-notices": r.Notice = true; break;
            case "--no-include-notices": r.Notice = false; break;
            default: return false;
        }
        r.InstallOnly.Add(a);
        return true;
    }

    /// <summary>An option with a value; the error, or null.</summary>
    static string? Valued(Request r, string a, string? value)
    {
        switch (a)
        {
            case "--scope":
                if (value is null) return "--scope needs a value (local, project or user)";
                if (ClaudeScopes.Parse(value) is not { } scope) return $"unknown scope '{value}' (local, project or user)";
                r.Scope = scope;
                return null;
            case "--command":
                if (value is not { Length: > 0 }) return "--command needs a program";
                r.Command = value;
                return null;
            case "--max-lines":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return "--max-lines needs a whole number of at least 1";
                r.MaxLines = n;
                break;
            default:
                return $"unknown option {a}";
        }
        r.InstallOnly.Add(a);
        return null;
    }

    /// <summary>The target and the combinations of options.</summary>
    static string? Combination(Request r)
    {
        if (r.Target is null) return "name a target: claude (Claude Code's hooks) or git-hooks (a commit hook)";
        if (r.Target != "claude") return $"unknown target '{r.Target}'; targets: claude (Claude Code's hooks), git-hooks (a commit hook)";
        if (r.Check && r.Options > 1) return "--check takes --hooks or --mcp only (it reads all three scopes)";
        if (!r.Hooks && r.InstallOnly.FirstOrDefault(o => o is not ("--force" or "--print")) is { } hookOption)
            return $"{hookOption} applies to the hooks; it cannot be used with --mcp alone";
        if (r.Print && r.Hooks == r.Mcp) return "--print writes one file: add --hooks or --mcp";
        if (r.Remove && r.InstallOnly.Count > 0) return $"--remove takes --scope, --command, --hooks, --mcp, --dry-run and --yes only (not {r.InstallOnly[0]})";
        if (r.Semantic && r.NoStop) return "--semantic applies to the Stop entry; it cannot be used with --no-stop";
        return Exclusive(r);
    }

    /// <summary>Options that exclude each other.</summary>
    static string? Exclusive(Request r)
    {
        if (r.Print && r.DryRun) return "--print and --dry-run: choose one (both write nothing)";
        bool on = r.InstallOnly.Contains("--include-notices"), off = r.InstallOnly.Contains("--no-include-notices");
        return on && off ? "--include-notices and --no-include-notices: choose one" : null;
    }
}
