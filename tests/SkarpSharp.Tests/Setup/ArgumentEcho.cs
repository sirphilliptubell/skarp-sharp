using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// The argument-echo program of the shell round-trip tests is this test executable itself. With
/// <c>SKARP_TEST_ECHO_ARGS=1</c> in its environment it prints the arguments it received as one JSON line
/// (<c>ECHO [...]</c>, non-ASCII escaped) and exits before the test platform starts.
/// </summary>
static class ArgumentEcho
{
    public const string Variable = "SKARP_TEST_ECHO_ARGS";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1") return;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        Console.Out.WriteLine("ECHO " + JsonSerializer.Serialize(args));
        Console.Out.Flush();
        Environment.Exit(0);
    }
}
