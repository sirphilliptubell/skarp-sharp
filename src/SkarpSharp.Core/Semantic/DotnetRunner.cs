using System.Diagnostics;
using System.Text;

namespace SkarpSharp.Semantic;

/// <summary>Result of one child process: exit code (-1 when it did not start), output, time and peak working set.</summary>
public sealed record ProcessRun(int ExitCode, string StandardOutput, string StandardError, long Milliseconds, long PeakWorkingSetBytes, string? StartError = null)
{
    public bool Succeeded => StartError is null && ExitCode == 0;

    /// <summary>Up to six lines of the output that look like errors (or the first lines), joined for a one-line message.</summary>
    public string ErrorSummary()
    {
        if (StartError is not null) return StartError;
        var all = (StandardOutput + "\n" + StandardError).Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        var picked = all.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Requested SDK", StringComparison.OrdinalIgnoreCase)).Distinct().Take(6).ToList();
        if (picked.Count == 0) picked = [.. all.Take(6)];
        return string.Join(" | ", picked);
    }
}

/// <summary>
/// Runs <c>dotnet</c> child processes for the semantic tier: design-time MSBuild, <c>dotnet sln list</c>,
/// <c>dotnet restore</c> and the SDK's compiler. Children start in the repository (so global.json picks the SDK), with
/// MSBuild variables that leak from a parent <c>dotnet</c> removed, node reuse off and English messages.
/// </summary>
public static class DotnetRunner
{
    static readonly string[] LeakyVariables =
    [
        "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly", "MSBUILDNOINPROCNODE",
        "MSBuildCacheEnabled", "VSTEST_HOST_DEBUG",
    ];

    /// <summary>
    /// The executable to start: <c>SKARP_DOTNET</c> when set, else <c>DOTNET_HOST_PATH</c> (set by a parent SDK command such
    /// as <c>dotnet test</c>), else <c>dotnet</c> on the path.
    /// </summary>
    public static string Executable =>
        Environment.GetEnvironmentVariable("SKARP_DOTNET") is { Length: > 0 } s ? s
        : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h && File.Exists(h) ? h
        : "dotnet";

    /// <summary>A stand-in for the child processes (tests: a fake runner that records the calls and their token;
    /// never real msbuild in unit tests); null runs <c>dotnet</c>. Async-local, so parallel tests do not see each other's.</summary>
    internal static readonly AsyncLocal<Func<IReadOnlyList<string>, string, CancellationToken, Task<ProcessRun>>?> Replacement = new();

    public static Task<ProcessRun> RunAsync(IEnumerable<string> args, string workingDirectory, CancellationToken ct = default) =>
        Replacement.Value is { } fake ? fake([.. args], workingDirectory, ct) : StartAsync(args, workingDirectory, ct);

    static async Task<ProcessRun> StartAsync(IEnumerable<string> args, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            // Standard input redirected and closed, so a child never reads the MCP server's protocol pipe.
            WorkingDirectory = workingDirectory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var k in LeakyVariables) psi.Environment.Remove(k);
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        var sw = Stopwatch.StartNew();
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ProcessRun(-1, "", "", 0, 0, $"cannot start '{Executable}': {ex.Message}");
        }
        if (p is null) return new ProcessRun(-1, "", "", 0, 0, $"cannot start '{Executable}'");
        p.StandardInput.Close();
        using (p)
        {
            // A cancelled call (the MCP client's notifications/cancelled) stops the child and its tree.
            using var stop = ct.Register(() => Kill(p));
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var error = p.StandardError.ReadToEndAsync(ct);
            long peak = 0;
            var exited = p.WaitForExitAsync(ct);
            while (!exited.IsCompleted)
            {
                peak = Math.Max(peak, PeakOf(p));
                await Task.WhenAny(exited, Task.Delay(50, ct)).ConfigureAwait(false);
            }
            await exited.ConfigureAwait(false);
            return new ProcessRun(p.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false), sw.ElapsedMilliseconds, peak);
        }
    }

    static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException)
        {
            // It exited in the meantime.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No access to it; the token still ends the wait.
        }
    }

    static long PeakOf(Process p)
    {
        try
        {
            p.Refresh();
            return p.PeakWorkingSet64;
        }
        catch (InvalidOperationException) { return 0; } // the process exited between the checks
        catch (System.ComponentModel.Win32Exception) { return 0; } // no access to the counters
    }
}
