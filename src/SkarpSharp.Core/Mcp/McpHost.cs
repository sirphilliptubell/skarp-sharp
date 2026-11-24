using System.Diagnostics;
using SkarpSharp.Configuration;

namespace SkarpSharp.Mcp;

/// <summary>
/// What the MCP server reads from the machine, replaceable in tests: the working folder (the default
/// <c>path</c>), the settings files and environment, git's variables, the clock, and where log lines go (standard error;
/// never standard output, which carries only protocol messages).
/// </summary>
public sealed class McpHost
{
    readonly Stopwatch _since = Stopwatch.StartNew();

    public string? WorkingDirectory { get; init; }
    public SettingsLocations? SettingsLocations { get; init; }
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }
    /// <summary>Log lines (default standard error); null writes none.</summary>
    public TextWriter? Log { get; init; }

    internal string Cwd => WorkingDirectory ?? Environment.CurrentDirectory;
    internal DateTimeOffset Now => Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    internal void Write(string message)
    {
        if (Log is not { } log) return;
        lock (log) log.WriteLine($"[skarp mcp +{_since.ElapsedMilliseconds} ms] {message}");
    }
}
