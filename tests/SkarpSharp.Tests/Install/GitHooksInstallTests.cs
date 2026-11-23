using SkarpSharp.Commands;
using SkarpSharp.Install;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// <c>skarp install git-hooks</c> in process against real git in temporary repositories. git never sees the machine's
/// configuration (<see cref="SyntheticRepo"/>: no system or global config); the program lookup uses a fake PATH; nothing
/// reads the real <c>~/.skarp</c> or <c>~/.claude</c>.
/// </summary>
public sealed class GitHooksInstallTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly string _bin = Directory.CreateTempSubdirectory("skarp-githooks-bin-").FullName;
    readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    bool _interactive;
    string _answers = "";

    public GitHooksInstallTests()
    {
        _env["PATH"] = _bin;
        _env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        File.WriteAllText(Path.Combine(_bin, OperatingSystem.IsWindows() ? "skarp.exe" : "skarp"), "");
        _repo.Write("src/A.cs", StagedCheckTests.Good);
        _repo.Commit("init", DateTimeOffset.UnixEpoch);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_bin, true); } catch (IOException) { }
    }

    InstallSeams Seams => new()
    {
        Variable = n => _env.GetValueOrDefault(n), UserHome = _bin, GitIgnored = (_, _) => true,
        Interactive = () => _interactive, Answers = new StringReader(_answers), GitEnvironment = _repo.Environment,
        RunProgram = _ => throw new InvalidOperationException("git-hooks runs no hook program"),
    };

    async Task<(int Exit, string Out, string Error)> RunIn(string cwd, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["install", "git-hooks", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = cwd, Install = Seams,
        });
        return (exit, o.ToString(), e.ToString());
    }

    Task<(int Exit, string Out, string Error)> Run(params string[] args) => RunIn(Path.Combine(_repo.Root, "src"), args);

    string ConfigPath => Path.Combine(_repo.Root, ".git", "config");
    byte[] Config => File.ReadAllBytes(ConfigPath);
    string Get(string key) => _repo.Git("config", "--local", "--get-all", key).Replace("\r\n", "\n");

    // ---- local ------------------------------------------------------------------------------------------------------

    [Test]
    public async Task Install_writes_command_then_event_and_a_second_install_changes_nothing()
    {
        var (exit, output, error) = await Run("--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("pre-commit: add hook.skarp-pre-commit:").And.Contains("Wrote ");
        await Assert.That(Get("hook.skarp-pre-commit.command")).IsEqualTo(GitHooks.Command(new GitHookSpec(), GitHooks.Commit) + "\n");
        await Assert.That(Get("hook.skarp-pre-commit.event")).IsEqualTo("pre-commit\n");
        var list = _repo.Git("config", "--local", "--list");
        await Assert.That(list.IndexOf("hook.skarp-pre-commit.command", StringComparison.Ordinal)).IsLessThan(list.IndexOf("hook.skarp-pre-commit.event", StringComparison.Ordinal));
        await Assert.That(_repo.Git("hook", "list", "pre-commit")).Contains("skarp-pre-commit");
        await Assert.That(output).Contains("note: no .skarp-baseline.json yet").And.Contains("git add .skarp-baseline.json");

        var before = Config;
        (exit, output, _) = await Run("--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("Nothing to change");
        await Assert.That(Config).IsEquivalentTo(before);
    }

    [Test]
    public async Task Install_then_remove_restores_the_config_byte_for_byte_and_leaves_other_hooks_alone()
    {
        _repo.Git("config", "hook.lint.command", "dotnet format --verify-no-changes");
        _repo.Git("config", "hook.lint.event", "pre-commit");
        _repo.Git("config", "core.hooksPath", ".myhooks");
        var script = Path.Combine(_repo.Root, ".git", "hooks", "pre-commit");
        File.WriteAllText(script, "#!/bin/sh\necho mine\n");
        foreach (var options in new[] { new[] { "--yes" }, ["--yes", "--pre-push"], ["--yes", "--pre-push", "--semantic", "--strict"], ["--yes", "--command", @"C:\Tools\skarp.exe"] })
        {
            var before = Config;
            var (exit, output, error) = await Run(options);
            await Assert.That(exit).IsEqualTo(0).Because(error);
            await Assert.That(output).Contains("note: core.hooksPath is .myhooks");
            (exit, _, error) = await Run("--remove", "--yes");
            await Assert.That(exit).IsEqualTo(0).Because(error);
            await Assert.That(Config).IsEquivalentTo(before).Because(string.Join(" ", options));
        }
        await Assert.That(Get("hook.lint.command")).IsEqualTo("dotnet format --verify-no-changes\n");
        await Assert.That(File.ReadAllText(script)).IsEqualTo("#!/bin/sh\necho mine\n");
        var (code, nothing, _) = await Run("--remove", "--yes");
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(nothing).Contains("Nothing to remove");
    }

    [Test]
    public async Task Push_hook_options_update_in_place_and_an_installed_push_hook_is_kept_until_no_pre_push()
    {
        await Run("--yes", "--pre-push");
        await Assert.That(Get("hook.skarp-pre-push.command")).Contains("--no-semantic --push-check");
        var (_, output, _) = await Run("--yes", "--pre-push", "--semantic");
        await Assert.That(output).Contains("pre-push: update hook.skarp-pre-push");
        await Assert.That(Get("hook.skarp-pre-push.command")).Contains("exec skarp analyze --gate --no-history --push-check");
        (_, output, _) = await Run("--yes");
        await Assert.That(output).Contains("pre-push: kept as installed (--no-pre-push removes it)");
        await Assert.That(Get("hook.skarp-pre-push.event")).IsEqualTo("pre-push\n");
        (_, output, _) = await Run("--yes", "--no-pre-push");
        await Assert.That(output).Contains("pre-push: remove hook.skarp-pre-push");
        await Assert.That(_repo.Git("config", "--local", "--list")).DoesNotContain("skarp-pre-push");
    }

    [Test]
    public async Task An_orphaned_event_and_a_duplicate_are_repaired_and_reported_by_check()
    {
        _repo.Git("config", "hook.skarp-pre-commit.event", "pre-commit");
        var (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).Contains("problem: hook.skarp-pre-commit.event without hook.skarp-pre-commit.command stops every commit");
        (exit, output, _) = await Run("--yes");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("pre-commit: repair hook.skarp-pre-commit");
        (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("note: hook.skarp-pre-commit.event comes before .command").And.Contains("All good.");

        _repo.Git("config", "--add", "hook.skarp-pre-commit.event", "pre-commit");
        (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).Contains("needs one command and one event pre-commit");
        await Run("--yes");
        await Assert.That(Get("hook.skarp-pre-commit.event")).IsEqualTo("pre-commit\n");
    }

    [Test]
    public async Task Check_names_the_program_the_managers_and_the_baseline()
    {
        var (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("No Skarp git hooks installed");
        await Run("--yes", "--pre-push");
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".husky"));
        (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("git: git version").And.Contains("program: skarp -> ").And.Contains("note: husky also runs hooks here")
            .And.Contains("note: no .skarp-baseline.json yet");
        _env["PATH"] = Path.Combine(_bin, "empty");
        (exit, output, _) = await Run("--check");
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).Contains("problem: skarp not found: every commit goes through unchecked");
        await Run("--yes", "--strict");
        (_, output, _) = await Run("--check");
        await Assert.That(output).Contains("with --strict every commit is blocked");
    }

    [Test]
    public async Task Writing_needs_a_terminal_or_yes_and_dry_run_and_print_write_nothing()
    {
        var before = Config;
        var (exit, _, error) = await Run();
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("no terminal to ask on");
        _interactive = true;
        _answers = "n\n";
        (exit, var output, _) = await Run();
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(output).Contains("Nothing written.");
        (exit, output, _) = await Run("--dry-run");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).Contains("dry run: nothing written");
        (exit, output, error) = await Run("--print");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).IsEqualTo(string.Join("\n", GitHooks.ConfigLines(new GitHookSpec())) + "\n");
        await Assert.That(error).Contains("pre-commit: add");
        await Assert.That(Config).IsEquivalentTo(before);
        _answers = "y\n";
        (exit, _, _) = await Run();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Get("hook.skarp-pre-commit.event")).IsEqualTo("pre-commit\n");
    }

    [Test]
    public async Task Usage_errors()
    {
        foreach (var (args, message) in new (string[], string)[]
        {
            (["--check", "--yes"], "--check takes no other options"),
            (["--remove", "--pre-push"], "--remove takes --shared, --dry-run and --yes only"),
            (["--semantic"], "--semantic applies to the push hook"),
            (["--print", "--dry-run"], "choose one"),
            (["--pre-push", "--no-pre-push"], "choose one"),
            (["--command", "C:/it's/skarp.exe"], "single quote"),
            (["--shared", "--no-pre-push"], "--no-pre-push applies to the local git config"),
            (["--bogus"], "unknown option --bogus"),
            (["extra"], "unexpected argument 'extra'"),
        })
        {
            var (exit, _, error) = await Run(args);
            await Assert.That(exit).IsEqualTo(2).Because(string.Join(" ", args));
            await Assert.That(error).Contains(message);
        }
        var (code, help, _) = await Run("--help");
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(help).Contains("skarp install git-hooks --check");
        var outside = Directory.CreateTempSubdirectory("skarp-nogit-").FullName;
        try
        {
            var (exit, _, error) = await RunIn(outside, "--yes");
            await Assert.That(exit).IsEqualTo(2);
            await Assert.That(error).Contains("not in a git work tree");
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public async Task A_linked_worktree_installs_into_the_shared_config_and_runs_it()
    {
        var wt = Path.Combine(Path.GetDirectoryName(_repo.Root)!, "wt");
        _repo.Git("worktree", "add", "-q", wt);
        var (exit, output, error) = await RunIn(wt, "--yes");
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output).Contains("Wrote " + ConfigPath);
        await Assert.That(Get("hook.skarp-pre-commit.event")).IsEqualTo("pre-commit\n");
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = wt, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "hook", "list", "--show-scope", "pre-commit" }) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _repo.Environment) psi.Environment[k] = v;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var list = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        await Assert.That(list).Contains("local").And.Contains("skarp-pre-commit");
        (exit, _, _) = await RunIn(wt, "--remove", "--yes");
        await Assert.That(exit).IsEqualTo(0);
    }

    // ---- shared -----------------------------------------------------------------------------------------------------

    string Script(string hook) => Path.Combine(_repo.Root, ".githooks", hook);

    [Test]
    public async Task Shared_scripts_round_trip_byte_for_byte_on_every_fixture()
    {
        foreach (var (name, text) in GitHooksTests.ScriptFixtures())
        {
            if (name == "older block") continue; // replaced in place: it does not come back (GitHooksTests covers it)
            foreach (var hook in new[] { "pre-commit", "pre-push" })
            {
                if (File.Exists(Script(hook))) File.Delete(Script(hook));
                if (text is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Script(hook))!); File.WriteAllText(Script(hook), text); }
            }
            var (exit, output, error) = await Run("--shared", "--pre-push", "--yes");
            await Assert.That(exit).IsEqualTo(0).Because(error);
            await Assert.That(output).Contains("git config core.hooksPath .githooks").And.Contains("git add --chmod=+x .githooks/pre-commit");
            var installed = File.ReadAllText(Script("pre-commit"));
            await Assert.That(installed).Contains("# >>> skarp >>>").And.DoesNotContain("\r");
            (_, output, _) = await Run("--shared", "--pre-push", "--yes");
            await Assert.That(output).Contains("already holds Skarp's block");
            (exit, _, error) = await Run("--remove", "--shared", "--yes");
            await Assert.That(exit).IsEqualTo(0).Because(error);
            foreach (var hook in new[] { "pre-commit", "pre-push" })
                if (text is null) await Assert.That(File.Exists(Script(hook))).IsFalse().Because(name);
                else await Assert.That(File.ReadAllText(Script(hook))).IsEqualTo(text).Because(name);
        }
    }

    [Test]
    public async Task A_tracked_script_is_marked_executable_and_a_non_shell_script_gets_the_block_printed()
    {
        _repo.Write(".githooks/pre-commit", "#!/bin/sh\necho team\n");
        _repo.Git("add", ".githooks/pre-commit");
        var (_, output, _) = await Run("--shared", "--yes");
        await Assert.That(output).Contains("Marked .githooks/pre-commit executable in git's index");
        await Assert.That(_repo.Git("ls-files", "-s", ".githooks/pre-commit")).StartsWith("100755");

        _repo.Write(".githooks/pre-push", "#!/usr/bin/env python3\nprint('x')\n");
        (_, output, _) = await Run("--shared", "--pre-push", "--yes");
        await Assert.That(output).Contains(".githooks/pre-push: not a shell script; add this block yourself:");
        await Assert.That(File.ReadAllText(Script("pre-push"))).IsEqualTo("#!/usr/bin/env python3\nprint('x')\n");
    }

    /// <summary>The manager fixtures: the file that marks each.</summary>
    static readonly (string Name, string File, string Content)[] Managers =
    [
        ("husky", ".husky/pre-commit", "npm test\n"),
        ("Husky.Net", ".husky/task-runner.json", "{ \"tasks\": [] }\n"),
        ("lefthook", "lefthook.yml", "pre-commit:\n  commands: {}\n"),
        ("lefthook", ".lefthook.yaml", "pre-commit:\n"),
        ("pre-commit", ".pre-commit-config.yaml", "repos: []\n"),
    ];

    [Test]
    public async Task Each_manager_gets_its_snippet_and_nothing_is_written()
    {
        foreach (var (name, file, content) in Managers)
        {
            using var repo = new SyntheticRepo();
            repo.Write(file, content);
            repo.Write(".skarp-baseline.json", "{}");
            var config = File.ReadAllBytes(Path.Combine(repo.Root, ".git", "config"));
            var files = Directory.GetFiles(repo.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".git" + Path.DirectorySeparatorChar)).Order().ToList();
            var (exit, output, error) = await Manager(repo, "--shared", "--pre-push", "--yes");
            await Assert.That(exit).IsEqualTo(0).Because(error);
            await Assert.That(output).StartsWith($"{name} manages this repository's hooks");
            await Assert.That(output).Contains("--push-check");
            await Assert.That(File.ReadAllBytes(Path.Combine(repo.Root, ".git", "config"))).IsEquivalentTo(config).Because(name);
            await Assert.That(Directory.GetFiles(repo.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".git" + Path.DirectorySeparatorChar)).Order().ToList()).IsEquivalentTo(files).Because(name);
            await Assert.That(File.ReadAllText(Path.Combine(repo.Root, file))).IsEqualTo(content);
            (_, output, _) = await Manager(repo, "--shared");
            await Assert.That(output).DoesNotContain("--push-check").Because(name + ": the push part only with --pre-push");
        }
    }

    async Task<(int, string, string)> Manager(SyntheticRepo repo, params string[] args)
    {
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        var seams = new InstallSeams { Variable = n => _env.GetValueOrDefault(n), Interactive = () => false, GitEnvironment = repo.Environment };
        int exit = await SkarpCommand.RunAsync(["install", "git-hooks", .. args], new CommandIO { Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = repo.Root, Install = seams });
        return (exit, o.ToString(), e.ToString());
    }

    // ---- docs ----------------------------------------------------------------------------------------

    static string Page()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkarpSharp.slnx"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(root!.FullName, "docs", "git-hooks.md")).Replace("\r\n", "\n");
    }

    static string Block(string page, string marker)
    {
        int at = page.IndexOf("<!-- equals: " + marker + " (GitHooksInstallTests) -->\n```", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("marker not found: " + marker);
        int start = page.IndexOf('\n', page.IndexOf("```", at, StringComparison.Ordinal)) + 1;
        return page[start..page.IndexOf("```\n", start, StringComparison.Ordinal)];
    }

    [Test]
    public async Task The_by_hand_page_snippets_equal_print()
    {
        var page = Page();
        var (_, local, _) = await Run("--pre-push", "--print");
        await Assert.That(Block(page, "skarp install git-hooks --pre-push --print")).IsEqualTo(local);
        var (_, shared, _) = await Run("--shared", "--pre-push", "--print");
        await Assert.That(Block(page, "skarp install git-hooks --shared --pre-push --print")).IsEqualTo(shared);
        foreach (var (name, file, content) in Managers.DistinctBy(m => m.Name))
        {
            using var repo = new SyntheticRepo();
            repo.Write(file, content);
            repo.Write(".skarp-baseline.json", "{}");
            var (_, output, _) = await Manager(repo, "--shared", "--pre-push");
            var snippet = output[(output.IndexOf('\n') + 1)..];
            await Assert.That(Block(page, "snippet for " + name)).IsEqualTo(snippet).Because(name);
        }
    }
}
