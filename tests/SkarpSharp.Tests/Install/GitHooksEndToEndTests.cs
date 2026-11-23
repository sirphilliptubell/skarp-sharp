using System.Diagnostics;
using System.Text;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// The hooks installed by the built <c>skarp</c> in a scratch
/// repository, with real <c>git commit</c>s (blocked, passing, <c>--no-verify</c>, <c>commit -a</c>, Skarp missing with
/// and without <c>--strict</c>) and <c>git push</c> to a local bare remote. git sees no system or global config
/// (<see cref="SyntheticRepo"/>); <c>SKARP_HOME</c> points into the temp folder. The CLI project's JIT build is copied with
/// this test run's <c>SkarpSharp.Core.dll</c>, so a stale build cannot pass; skipped when the CLI is not built.
/// </summary>
[NotInParallel(nameof(GitHooksEndToEndTests))]
public sealed class GitHooksEndToEndTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly string _temp = Directory.CreateTempSubdirectory("skarp-e2e-").FullName;
    string _bin = "";

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Copies the built CLI into the temp folder with the current Core; false when it is not built.</summary>
    bool Prepare()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkarpSharp.slnx"))) root = root.Parent;
        var exe = OperatingSystem.IsWindows() ? "skarp.exe" : "skarp";
        var built = root is null ? null : new[] { "Debug", "Release" }.Select(c => Path.Combine(root.FullName, "src", "SkarpSharp.Cli", "bin", c, "net10.0")).FirstOrDefault(d => File.Exists(Path.Combine(d, exe)));
        if (built is null) return false;
        _bin = Path.Combine(_temp, "bin");
        Directory.CreateDirectory(_bin);
        foreach (var f in Directory.GetFiles(built)) File.Copy(f, Path.Combine(_bin, Path.GetFileName(f)));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "SkarpSharp.Core.dll"), Path.Combine(_bin, "SkarpSharp.Core.dll"), overwrite: true);
        return true;
    }

    /// <summary>This machine's PATH without any folder that holds a skarp program (git's own folders stay: git for
    /// Windows crashes without them).</summary>
    static string PathWithoutSkarp()
    {
        var exe = OperatingSystem.IsWindows() ? "skarp.exe" : "skarp";
        return string.Join(Path.PathSeparator, (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !File.Exists(Path.Combine(d.Trim('"'), exe)) && !File.Exists(Path.Combine(d.Trim('"'), "skarp.cmd"))));
    }

    (int Exit, string Out, string Error) Run(string program, bool withSkarp, params string[] args)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = _repo.Root, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _repo.Environment) psi.Environment[k] = v;
        psi.Environment["PATH"] = (withSkarp ? _bin + Path.PathSeparator : "") + PathWithoutSkarp();
        psi.Environment["SKARP_HOME"] = Path.Combine(_temp, "home");
        psi.Environment["SKARP_NO_USER_CONFIG"] = "1";
        // The reports read CLAUDECODE (Claude's ways out); the hooks here are a person's, whoever runs the tests.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment["GIT_AUTHOR_DATE"] = psi.Environment["GIT_COMMITTER_DATE"] = "@0 +0000";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, err.Result);
    }

    (int Exit, string Out, string Error) Git(params string[] args) => Run("git", true, args);
    (int Exit, string Out, string Error) Skarp(params string[] args) => Run(Path.Combine(_bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), true, args);

    int Commits => _repo.Git("rev-list", "--count", "HEAD").Trim() is var n ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : 0;

    [Test]
    public async Task Commits_and_pushes_through_the_installed_hooks()
    {
        if (!Prepare()) { Skip.Test("the CLI project is not built (dotnet build src/SkarpSharp.Cli)"); return; }
        _repo.Write("src/A.cs", StagedCheckTests.Good);
        var baseline = Skarp("baseline", ".", "--no-history", "--no-semantic");
        await Assert.That(baseline.Exit).IsEqualTo(0).Because(baseline.Error);
        _repo.Commit("init", DateTimeOffset.UnixEpoch);
        var bare = Path.Combine(_temp, "bare.git");
        _repo.Git("init", "-q", "--bare", bare);
        _repo.Git("remote", "add", "origin", bare);

        var install = Skarp("install", "git-hooks", "--pre-push", "--yes");
        await Assert.That(install.Exit).IsEqualTo(0).Because(install.Out + install.Error);

        // Blocked: the staged file makes code health worse.
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Git("add", "src/A.cs");
        int before = Commits;
        var blocked = Git("commit", "-m", "worse");
        await Assert.That(blocked.Exit).IsEqualTo(1).Because(blocked.Out);
        await Assert.That(blocked.Error).Contains("Skarp stopped this commit: 1 new problem in 1 staged file; health past its limit in 1 staged file.").And.Contains("  new line 5 A.F brain-method: long (").And.Contains("`git commit --no-verify`");
        await Assert.That(Commits).IsEqualTo(before);

        // commit -a judges the working change too (git's index.lock).
        _repo.Git("reset", "-q");
        var all = Git("commit", "-a", "-m", "worse too");
        await Assert.That(all.Exit).IsNotEqualTo(0);
        await Assert.That(Commits).IsEqualTo(before);

        // Passing: a clean file.
        _repo.Git("checkout", "-q", "--", "src/A.cs");
        _repo.Write("src/B.cs", StagedCheckTests.Good.Replace("class A", "class B"));
        _repo.Git("add", "src/B.cs");
        var passing = Git("commit", "-m", "clean");
        await Assert.That(passing.Exit).IsEqualTo(0).Because(passing.Error);
        await Assert.That(Commits).IsEqualTo(before + 1);
        var push = Git("push", "-q", "origin", "main");
        await Assert.That(push.Exit).IsEqualTo(0).Because(push.Error);

        // --no-verify: committed although worse; the push hook then stops the push.
        _repo.Write("src/A.cs", StagedCheckTests.Bad());
        _repo.Git("add", "src/A.cs");
        var skipped = Git("commit", "--no-verify", "-m", "worse, skipped");
        await Assert.That(skipped.Exit).IsEqualTo(0);
        await Assert.That(skipped.Error).DoesNotContain("Skarp");
        var pushBad = Git("push", "origin", "main");
        await Assert.That(pushBad.Exit).IsNotEqualTo(0);
        await Assert.That(pushBad.Error).Contains("Skarp stopped this push: 1 new problem in 1 file; health past its limit in 1 file.").And.Contains("`git push --no-verify`");
        // The push saves its run, so the handle it prints resolves.
        int at = pushBad.Error.IndexOf("eg: `skarp show ", StringComparison.Ordinal);
        await Assert.That(at).IsGreaterThan(0).Because(pushBad.Error);
        var shown = Skarp("show", pushBad.Error[(at + "eg: `skarp show ".Length)..].Split('`')[0]);
        await Assert.That(shown.Exit).IsEqualTo(0).Because(shown.Error);
        await Assert.That(Git("push", "-q", "--no-verify", "origin", "main").Exit).IsEqualTo(0);

        // Skarp missing: the commit goes through with one line; with --strict it is blocked.
        _repo.Write("src/C.cs", StagedCheckTests.Good.Replace("class A", "class C"));
        _repo.Git("add", "src/C.cs");
        var missing = Run("git", false, "commit", "-m", "no skarp");
        await Assert.That(missing.Exit).IsEqualTo(0).Because(missing.Error);
        await Assert.That(missing.Error).Contains("Skarp: not found, commit not checked");
        await Assert.That(Skarp("install", "git-hooks", "--strict", "--yes").Exit).IsEqualTo(0);
        _repo.Write("src/D.cs", StagedCheckTests.Good.Replace("class A", "class D"));
        _repo.Git("add", "src/D.cs");
        int count = Commits;
        var strict = Run("git", false, "commit", "-m", "no skarp, strict");
        await Assert.That(strict.Exit).IsNotEqualTo(0);
        await Assert.That(Commits).IsEqualTo(count);

        var check = Skarp("install", "git-hooks", "--check");
        await Assert.That(check.Exit).IsEqualTo(0).Because(check.Out);
        await Assert.That(Skarp("install", "git-hooks", "--remove", "--yes").Exit).IsEqualTo(0);
        await Assert.That(_repo.Git("config", "--local", "--list")).DoesNotContain("hook.skarp");
    }
}
