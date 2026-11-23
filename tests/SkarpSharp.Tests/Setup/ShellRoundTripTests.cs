using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkarpSharp.Configuration;
using SkarpSharp.Setup;

namespace SkarpSharp.Tests.Setup;

/// <summary>
/// Every printed form goes through the real shell. Each line runs in
/// cmd, Windows PowerShell 5.1, PowerShell 7 (when installed), Git Bash on Windows and sh elsewhere, against this test
/// executable as an argument echo (<see cref="ArgumentEcho"/>); the arguments received must equal the intended ones. The
/// values are a list of awkward arguments (spaces, quotes, metacharacters, non-ASCII text) plus further cases. A value a
/// shell cannot carry must be refused by the quoting (the builder then leaves it out with a note), never printed wrong.
/// A shell that is not installed is skipped with its reason. One shell process runs all lines (cmd: one per line).
/// </summary>
[Category("Shell")]
public sealed class ShellRoundTripTests
{
    /// <summary>The values, each tried as a bare argument and as <c>exclude=&lt;value&gt;</c>.</summary>
    public static readonly string[] Values =
    [
        "a b", "a  b", "a\tb", "*", "?", "a|b", "a&b", "a^b", "a%b", "%PATH%", "a!b", "$HOME", "$(x)", "a\"b", "\"", "\"\"", "a'b", "'",
        "`", "a`b", "a;b", "a,b", "a=b=c", "a<b>c", "(a)", "x)", "über", "año", "中文", "😀", "C:\\x y\\", "C:\\x\\\\", "\\\"", "a\\\"b",
        "\\\\\"", "gen/**,obj/**", "--flag", "@file", "-p:a=1;b=2", "a\u2019b", "\u2018q\u2019", "a\u201Ab\u201B", "C:\\path with space\\file.sln",
        "^ci-bot$", "[bot]", "{a,b}", "~/x", "#x", "a#b", "!x!", "a&&b||c", "a\\b", "x\\", "^", "a \\\"b\\\" c", "/abs/path", "k=/v",
    ];

    static string EchoExe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SkarpSharp.Tests.exe" : "SkarpSharp.Tests");

    /// <summary>The tokens of the values case: each value bare and inside a key=value token.</summary>
    static List<string> Tokens()
    {
        var list = new List<string>();
        foreach (var v in Values) { list.Add(v); list.Add("exclude=" + v); }
        return list;
    }

    // ---- shells -----------------------------------------------------------------------------------------------------

    static string? GitBash()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432"), @"C:\Program Files" })
            if (root is not null && File.Exists(Path.Combine(root, "Git", "bin", "bash.exe"))) return Path.Combine(root, "Git", "bin", "bash.exe");
        return null;
    }

    static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe" } : [""])
            {
                var p = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Runs <paramref name="exe"/> with raw <paramref name="arguments"/> and returns every echoed argument list.</summary>
    static List<string[]> Run(string exe, string arguments, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment[ArgumentEcho.Variable] = "1";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var lines = new List<string[]>();
        foreach (var line in output.Split('\n'))
            if (line.StartsWith("ECHO ", StringComparison.Ordinal)) lines.Add(JsonSerializer.Deserialize<string[]>(line[5..].TrimEnd('\r'))!);
        if (lines.Count == 0) throw new InvalidOperationException($"no echo from {exe} {arguments}: {output} {errTask.Result}");
        return lines;
    }

    /// <summary>The argument lists the shell gives the echo program for each printed line (arguments after <c>skarp</c>).</summary>
    static List<string[]> Through(Shell shell, string exe, IReadOnlyList<string> lines, bool msysOff = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "skarp-rt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            switch (shell)
            {
                case Shell.Cmd:
                {
                    var results = new List<string[]>();
                    foreach (var line in lines)
                        results.AddRange(Run(exe, $"/d /v:off /s /c \"\"{EchoExe}\"{line[5..]}\""));
                    return results;
                }
                case Shell.PowerShell:
                {
                    var script = Path.Combine(dir, "lines.ps1");
                    var text = string.Join("\r\n", lines.Select(l => $"& '{EchoExe}'{l[5..]}")) + "\r\n";
                    File.WriteAllText(script, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    return Run(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"");
                }
                default:
                {
                    var script = Path.Combine(dir, "lines.sh");
                    var echo = EchoExe.Replace('\\', '/');
                    var text = string.Join("\n", lines.Select(l => (msysOff ? "MSYS_NO_PATHCONV=1 " : "") + "'" + echo + "'" + l[5..])) + "\n";
                    File.WriteAllText(script, text, new UTF8Encoding(false));
                    return Run(exe, $"\"{script}\"");
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>The shells of this machine: (name, quoting, executable or null, skip reason).</summary>
    public static IEnumerable<Func<(string Name, Shell Shell)>> Shells()
    {
        yield return () => ("cmd", Shell.Cmd);
        yield return () => ("powershell", Shell.PowerShell);
        yield return () => ("pwsh", Shell.PowerShell);
        yield return () => ("sh", Shell.Sh);
    }

    static (string? Exe, string Reason) Find(string name) => name switch
    {
        "cmd" => OperatingSystem.IsWindows() ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "") : (null, "cmd exists on Windows only"),
        "powershell" => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            ? (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "") : (null, "Windows PowerShell 5.1 exists on Windows only"),
        "pwsh" => OnPath("pwsh") is { } p ? (p, "") : (null, "PowerShell 7 (pwsh) is not installed"),
        _ => OperatingSystem.IsWindows() ? (GitBash() is { } b ? (b, "") : (null, "Git Bash is not installed")) : (File.Exists("/bin/sh") ? ("/bin/sh", "") : (null, "no /bin/sh")),
    };

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Test]
    [MethodDataSource(nameof(Shells))]
    public async Task Every_value_arrives_exactly_or_is_refused(string name, Shell shell)
    {
        var (exe, reason) = Find(name);
        if (exe is null) Skip.Test(reason);
        var tokens = Tokens();
        var sent = new List<string>();
        var quoted = new StringBuilder("skarp");
        var refused = new List<string>();
        foreach (var t in tokens)
        {
            if (ShellQuote.Quote(shell, t) is { } q) { sent.Add(t); quoted.Append(' ').Append(q); }
            else refused.Add(t);
        }
        // Git Bash rewrites arguments that look like POSIX paths unless MSYS_NO_PATHCONV=1 (the builder's advice).
        var received = Through(shell, exe!, [quoted.ToString()], msysOff: shell == Shell.Sh);
        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEquivalentTo(sent, CollectionOrdering.Matching);
        // Only the documented refusals: % and line breaks in cmd; " and a trailing backslash after whitespace in PowerShell.
        foreach (var t in refused)
        {
            bool expected = shell switch
            {
                Shell.Cmd => t.Contains('%'),
                Shell.PowerShell => !ShellQuote.PowerShellPortable(t),
                _ => false,
            };
            await Assert.That(expected).IsTrue().Because($"{name} refused {t}");
        }
        Console.WriteLine($"{name}: {sent.Count} arguments exact, {refused.Count} refused by design");
    }

    [Test]
    [MethodDataSource(nameof(Shells))]
    public async Task Long_value_and_line_break(string name, Shell shell)
    {
        var (exe, reason) = Find(name);
        if (exe is null) Skip.Test(reason);
        var big = "exclude=" + string.Concat(Enumerable.Repeat("gen/**/x y'$`!,", 530)); // about 8,000 characters
        var cmdBig = big[..6000]; // cmd's line limit is 8,191 characters
        var newline = "exclude=a\nb";
        var lines = new List<string>();
        var expected = new List<string[]>();
        foreach (var v in new[] { big, cmdBig, newline })
            if (ShellQuote.Quote(shell, v) is { } q && (shell != Shell.Cmd || q.Length < 7000))
            {
                lines.Add("skarp " + q);
                expected.Add([v]);
            }
        if (lines.Count == 0) { Console.WriteLine($"{name}: both refused (cmd: %, line break; PowerShell: \", line break)"); return; }
        var received = Through(shell, exe!, lines, msysOff: shell == Shell.Sh);
        await Assert.That(received.Count).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++) await Assert.That(received[i]).IsEquivalentTo(expected[i], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Behavior the quoting relies on, checked against the real shell: Windows PowerShell 5.1 (Legacy argument passing) corrupts a value with
    /// spaces and a trailing backslash and a value with <c>"</c>, and treats a typographic quote as a quote; this is why
    /// the quoting refuses the first two and doubles the third.
    /// </summary>
    [Test]
    public async Task Windows_PowerShell_51_quirks_are_real()
    {
        var (exe, reason) = Find("powershell");
        if (exe is null) Skip.Test(reason);
        var naive = new[] { "skarp 'C:\\x y\\' next", "skarp 'a\"b' next", "skarp 'a\u2019\u2019b' next" };
        var received = Through(Shell.PowerShell, exe!, naive);
        await Assert.That(received[0]).IsNotEquivalentTo(new[] { "C:\\x y\\", "next" }, CollectionOrdering.Matching);
        await Assert.That(received[1]).IsNotEquivalentTo(new[] { "a\"b", "next" }, CollectionOrdering.Matching);
        await Assert.That(received[2]).IsEquivalentTo(new[] { "a\u2019b", "next" }, CollectionOrdering.Matching);
        Console.WriteLine($"5.1 trailing backslash: {JsonSerializer.Serialize(received[0])}; quote: {JsonSerializer.Serialize(received[1])}");
    }

    /// <summary>Git Bash rewrites an argument that looks like a POSIX path unless <c>MSYS_NO_PATHCONV=1</c> (the builder's advice).</summary>
    [Test]
    public async Task Git_Bash_path_conversion_needs_the_advice()
    {
        if (!OperatingSystem.IsWindows() || GitBash() is not { } bash) { Skip.Test("Git Bash on Windows only"); return; }
        var line = "skarp /abs/path k=/v";
        var converted = Through(Shell.Sh, bash, [line]);
        var exact = Through(Shell.Sh, bash, [line], msysOff: true);
        await Assert.That(exact[0]).IsEquivalentTo(new[] { "/abs/path", "k=/v" }, CollectionOrdering.Matching);
        await Assert.That(converted[0]).IsNotEquivalentTo(new[] { "/abs/path", "k=/v" }, CollectionOrdering.Matching);
        Console.WriteLine($"Git Bash without MSYS_NO_PATHCONV: {JsonSerializer.Serialize(converted[0])}");
    }

    /// <summary>Built commands: the printed line for each shell must give the builder's arguments for that shell.</summary>
    [Test]
    [MethodDataSource(nameof(Shells))]
    public async Task Built_lines_arrive_as_built(string name, Shell shell)
    {
        var (exe, reason) = Find(name);
        if (exe is null) Skip.Test(reason);
        var repo = Path.Combine(Path.GetTempPath(), "skarp-rtb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "sub dir"));
        try
        {
            var builds = new List<CommandBuilder>();
            foreach (var (command, options, changes) in Cases())
                builds.Add(CommandBuilder.Build(new CommandRequest
                {
                    Command = command, Options = options, Changes = changes, Cwd = repo, Locations = SettingsLocations.None,
                }));
            var lines = new List<string>();
            var expected = new List<List<string>>();
            foreach (var b in builds)
            {
                var (line, _) = b.Line(shell, out _);
                lines.Add(line);
                expected.Add(b.Arguments(shell).Args);
            }
            bool msys = shell == Shell.Sh && lines.Any(l => l.Contains("=/", StringComparison.Ordinal) || l.Contains(" /", StringComparison.Ordinal));
            var received = Through(shell, exe!, lines, msysOff: msys);
            await Assert.That(received.Count).IsEqualTo(expected.Count);
            for (int i = 0; i < expected.Count; i++) await Assert.That(received[i]).IsEquivalentTo(expected[i], CollectionOrdering.Matching);
        }
        finally { try { Directory.Delete(repo, true); } catch (IOException) { } }
    }

    /// <summary>
    /// The printed <c>skarp install claude</c> lines, with <c>${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe</c> and
    /// a path with spaces, arrive as the installer's arguments and parse back to the same request.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Shells))]
    public async Task Install_lines_arrive_as_built(string name, Shell shell)
    {
        var (exe, reason) = Find(name);
        if (exe is null) Skip.Test(reason);
        var calls = new List<SkarpSharp.Install.InstallCall>();
        foreach (var program in new[] { "${CLAUDE_PROJECT_DIR}/.skarp/bin/skarp.exe", @"C:\Program Files\Skarp Tools\skarp.exe", "/opt/skarp tools/skarp", "skarp" })
            foreach (var scope in SkarpSharp.Install.ClaudeScopes.All)
                calls.Add(new(scope, false, new SkarpSharp.Install.HookSpec { Command = program, CommandGiven = program != "skarp", Gate = true, MaxLines = 9, Notice = false }));
        calls.Add(new(SkarpSharp.Install.ClaudeScope.User, true, new SkarpSharp.Install.HookSpec()));
        var lines = new List<string>();
        var expected = new List<List<string>>();
        foreach (var call in calls)
        {
            var args = call.Args();
            var (line, _) = CommandBuilder.FromArgs(args, Path.GetTempPath()).Line(shell, out bool lossy);
            await Assert.That(lossy).IsFalse().Because(line);
            lines.Add(line);
            expected.Add(args);
        }
        var received = Through(shell, exe!, lines, msysOff: shell == Shell.Sh);
        await Assert.That(received.Count).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(received[i]).IsEquivalentTo(expected[i], CollectionOrdering.Matching);
            await Assert.That(SkarpSharp.Install.InstallCall.Parse(received[i]).Call).IsEqualTo(calls[i]);
        }
    }

    static JsonObject Doc(params (string Key, string Value)[] sets)
    {
        var doc = new JsonObject();
        foreach (var (k, v) in sets) SettingText.SetPath(doc, k, SettingText.Parse(k, v));
        return doc;
    }

    static IEnumerable<(string, Dictionary<string, string>, JsonObject)> Cases()
    {
        yield return ("analyze", new() { ["format"] = "sarif", ["output"] = "out/skarp.sarif" },
            Doc(("preset", "pmd"), ("exclude", "gen/**,obj/**"), ("history.window", "6"), ("thresholds.long-method.notice", "60"), ("rules.magic-number", "off")));
        yield return ("analyze", new() { ["directory"] = "src/sub dir", ["gate"] = "on", ["min-severity"] = "warning" },
            Doc(("history.botPatterns", "^ci-bot$,a|b,x&y"), ("exclude", "[]"), ("top", "null"), ("history.since", "2026-01-01")));
        yield return ("history", new() { ["format"] = "json" },
            Doc(("include", "a (1)/**,b'c/**,d\"e/**"), ("includeMode", "append"), ("history.includeBots", "true")));
        yield return ("baseline", new() { ["baseline"] = "base line.json" },
            Doc(("unusedCode.allow", "Foo*,M:N.T.M(System.String)"), ("semantic.targets", "net10.0,net8.0"), ("exclude", "%TEMP%/**,a!b/**")));
        yield return ("hotspots", [], Doc(("history.botPatterns", "über,中文,$HOME,`x`")));
    }
}
