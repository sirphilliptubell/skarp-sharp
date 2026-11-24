using System.Diagnostics;

namespace SkarpSharp.Install;

/// <summary>What a git run printed: exit code (-1 when git could not start), standard output and error.</summary>
internal sealed class GitOutput(int exitCode, string output, string error)
{
    public int ExitCode { get; } = exitCode;
    public string Out { get; } = output;
    public string Error { get; } = error;
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// What both install targets do through <see cref="InstallSeams"/>: the question before a write, and running
/// git (no shell, nothing on standard input, <see cref="InstallSeams.GitEnvironment"/> added).
/// </summary>
internal static class InstallActions
{
    /// <summary>True with <paramref name="yes"/>; without a terminal false with the hint; else <paramref name="question"/>
    /// with <c>(y/n)</c> and the answer.</summary>
    internal static bool Confirm(this InstallSeams seams, string question, bool yes, TextWriter output, TextWriter error)
    {
        if (yes) return true;
        if (!seams.IsInteractive)
        {
            error.WriteLine("skarp install: no terminal to ask on: add --yes to write, or --dry-run to preview; nothing written");
            return false;
        }
        output.Write($"{question} (y/n) ");
        output.Flush();
        var answer = seams.AnswerReader.ReadLine()?.Trim().ToLowerInvariant();
        if (answer is "y" or "yes") return true;
        output.WriteLine("Nothing written.");
        return false;
    }

    /// <summary>Runs git in <paramref name="dir"/>.</summary>
    internal static GitOutput Git(this InstallSeams seams, string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = dir, CreateNoWindow = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (seams.GitEnvironment is { } env) foreach (var (k, v) in env) psi.Environment[k] = v;
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("not started"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new GitOutput(-1, "", ex.Message); }
        using (process)
        {
            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new GitOutput(process.ExitCode, output, error.Result);
        }
    }
}
