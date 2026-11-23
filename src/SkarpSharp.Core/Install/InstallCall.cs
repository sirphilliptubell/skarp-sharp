using System.Globalization;

namespace SkarpSharp.Install;

/// <summary>
/// What an install command line asks for, without how it runs (<c>--force</c>, <c>--dry-run</c>,
/// <c>--print</c>, <c>--yes</c>): the scope, install or remove, and the spec. The hooks screen builds one; the printed
/// command is <see cref="Args"/> and must parse back to it (<see cref="Parse"/>).
/// </summary>
public sealed record InstallCall(ClaudeScope Scope, bool Remove, HookSpec Spec, bool Hooks = true, bool Mcp = true)
{
    /// <summary>
    /// What an install command line asks for (<paramref name="args"/> start with <c>install</c>); the error is the usage
    /// error, or that the line is <c>--help</c> or <c>--check</c>.
    /// </summary>
    public static (InstallCall? Call, string? Error) Parse(IReadOnlyList<string> args)
    {
        var (r, error) = InstallArgs.Parse(args);
        if (error is not null) return (null, error);
        if (r.Help || r.Check) return (null, "not an install or remove");
        return (new InstallCall(r.Scope, r.Remove, r.Spec(), r.Hooks, r.Mcp), null);
    }

    /// <summary>
    /// The arguments after <c>skarp</c>, non-default options only, in the order of the usage text; never <c>--yes</c> or
    /// <c>--force</c>. <see cref="Parse"/> gives this call back when its spec is in that form
    /// (<see cref="HookSpec.CommandGiven"/> exactly when the program is not <c>skarp</c>; for a remove only the program counts).
    /// </summary>
    public List<string> Args()
    {
        List<string> args = ["install", "claude"];
        if (Remove) args.Add("--remove");
        if (Hooks && !Mcp) args.Add("--hooks");
        if (Mcp && !Hooks) args.Add("--mcp");
        if (Scope != ClaudeScope.Local) args.AddRange(["--scope", Scope.Name()]);
        if (Spec.CommandGiven) args.AddRange(["--command", Spec.Command]);
        if (!Remove) AddOptions(args);
        return args;
    }

    void AddOptions(List<string> args)
    {
        if (!Spec.Stop) args.Add("--no-stop");
        if (!Spec.Compact) args.Add("--no-compact");
        if (Spec.Gate) args.Add("--gate");
        if (Spec.Semantic) args.Add("--semantic");
        if (Spec.MaxLines is { } n) args.AddRange(["--max-lines", n.ToString(CultureInfo.InvariantCulture)]);
        if (Spec.Notice is { } notice) args.Add(notice ? "--include-notices" : "--no-include-notices");
    }
}
