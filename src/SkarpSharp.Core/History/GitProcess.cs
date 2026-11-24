using System.ComponentModel;
using System.Text;
using CliWrap;

namespace SkarpSharp.History;

/// <summary>Outcome of one git process run.</summary>
public sealed class GitRunResult
{
    /// <summary>False when the git executable could not be started (not installed or not on PATH).</summary>
    public bool Started { get; init; }
    public int ExitCode { get; init; } = -1;
    public string StandardError { get; init; } = "";
    public string StandardOutput { get; init; } = "";
    public bool Succeeded => Started && ExitCode == 0;

    /// <summary>First non-empty stderr line, for notices.</summary>
    public string FirstErrorLine =>
        StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

/// <summary>
/// Thin wrapper over the git command line on CliWrap: stdout streamed as raw bytes or captured as text, stderr
/// drained concurrently (bounded), cancellation kills the process, no validation exceptions (callers read the exit code).
/// Every call disables terminal prompts and optional locks so analysis never blocks or writes to the user's repository.
/// </summary>
public static class GitProcess
{
    public const string Executable = "git";
    const int MaxStderrChars = 64 * 1024;

    /// <summary>Runs git and passes stdout as a raw byte stream to <paramref name="stdout"/>.</summary>
    public static async Task<GitRunResult> RunStreamAsync(string workDir, IReadOnlyList<string> args, Func<Stream, CancellationToken, Task> stdout,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        var err = new StringBuilder();
        var cmd = Build(workDir, args, environment)
            .WithStandardOutputPipe(PipeTarget.Create(stdout))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => { if (err.Length < MaxStderrChars) err.AppendLine(line); }));
        try
        {
            var r = await cmd.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new GitRunResult { Started = true, ExitCode = r.ExitCode, StandardError = err.ToString() };
        }
        catch (Win32Exception e)
        {
            return new GitRunResult { Started = false, StandardError = e.Message };
        }
    }

    /// <summary>Runs git and captures stdout as UTF-8 text (for small outputs: probes, counts).</summary>
    public static async Task<GitRunResult> RunTextAsync(string workDir, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var err = new StringBuilder();
        var cmd = Build(workDir, args, environment)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(output, Encoding.UTF8))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => { if (err.Length < MaxStderrChars) err.AppendLine(line); }));
        try
        {
            var r = await cmd.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new GitRunResult { Started = true, ExitCode = r.ExitCode, StandardOutput = output.ToString(), StandardError = err.ToString() };
        }
        catch (Win32Exception e)
        {
            return new GitRunResult { Started = false, StandardError = e.Message };
        }
    }

    static Command Build(string workDir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            // The C# pathspec relies on git's default pathspec rules; these variables would change them.
            ["GIT_LITERAL_PATHSPECS"] = null,
            ["GIT_GLOB_PATHSPECS"] = null,
            ["GIT_NOGLOB_PATHSPECS"] = null,
            ["GIT_ICASE_PATHSPECS"] = null,
        };
        if (environment is not null)
            foreach (var (k, v) in environment) env[k] = v;
        return Cli.Wrap(Executable)
            .WithArguments(args)
            .WithWorkingDirectory(workDir)
            .WithEnvironmentVariables(env)
            .WithValidation(CommandResultValidation.None);
    }
}
