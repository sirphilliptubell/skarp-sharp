namespace SkarpSharp.Commands;

/// <summary>The dispatch of the commands that parse their own arguments, kept apart from <see cref="SkarpCommand"/>.</summary>
static class OwnParsers
{
    /// <summary>The commands that parse their own arguments, or null: the agent-loop commands show and
    /// check, the settings editor, install and the push hook
    /// (<c>analyze --push-check</c>).</summary>
    internal static Task<int>? Run(IReadOnlyList<string> args, CommandIO io) => args.Count == 0 ? null : args[0] switch
    {
        "--version" or "-V" when args.Count == 1 => Task.FromResult(PrintVersion(io)),
        "show" => Task.FromResult(ShowCommand.Run(args, io)),
        "check" => CheckCommand.RunAsync(args, io),
        "setup" => Task.FromResult(SetupCommand.Run(args, io)),
        "install" => Task.FromResult(InstallCommand.Run(args, io)),
        "analyze" when args.Contains(PushCheck.Flag) => PushCheck.RunAsync(args, io),
        _ => null,
    };

    static int PrintVersion(CommandIO io)
    {
        io.Out.WriteLine($"skarp {SkarpCommand.ToolVersion}");
        return 0;
    }
}
