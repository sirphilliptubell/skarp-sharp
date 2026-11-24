using SkarpSharp.Install;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// The git-config hook model and the shared-script block
/// model as pure functions: the commands written, their order, recognition and read-back, install and remove of the
/// marked block (LF, byte for byte), and the shell test.
/// </summary>
public sealed class GitHooksTests
{
    const string Guarded = "command -v skarp >/dev/null 2>&1 || { echo 'Skarp: not found, commit not checked; install Skarp to check commits' >&2; exit 0; }; exec skarp check --staged --gate";

    [Test]
    public async Task The_commands_end_in_a_simple_command_and_quote_a_given_program()
    {
        await Assert.That(GitHooks.Command(new GitHookSpec(), GitHooks.Commit)).IsEqualTo(Guarded);
        await Assert.That(GitHooks.Command(new GitHookSpec { Strict = true }, GitHooks.Commit)).IsEqualTo("exec skarp check --staged --gate");
        var path = new GitHookSpec { Command = @"C:\Program Files\Skarp\skarp.exe", CommandGiven = true };
        await Assert.That(GitHooks.Command(path, GitHooks.Commit)).IsEqualTo(
            "command -v 'C:/Program Files/Skarp/skarp.exe' >/dev/null 2>&1 || { echo 'Skarp: not found, commit not checked; install Skarp to check commits' >&2; exit 0; }; exec 'C:/Program Files/Skarp/skarp.exe' check --staged --gate");
        await Assert.That(GitHooks.Command(new GitHookSpec { PrePush = true }, GitHooks.Push)).EndsWith("; exec skarp analyze --gate --no-history --no-semantic --push-check");
        await Assert.That(GitHooks.Command(new GitHookSpec { PrePush = true, Semantic = true }, GitHooks.Push)).EndsWith("; exec skarp analyze --gate --no-history --push-check");
        await Assert.That(GitHooks.CommandError("C:/it's/skarp.exe")).IsNotNull();
        foreach (var spec in Specs())
            foreach (var kind in GitHooks.Kinds)
            {
                var command = GitHooks.Command(spec, kind);
                await Assert.That(command.EndsWith(" fi", StringComparison.Ordinal) || command.EndsWith('}')).IsFalse();
                var back = GitHooks.Parse(command, kind);
                await Assert.That(back).IsNotNull();
                await Assert.That(back!.Command).IsEqualTo(spec.Command.Replace('\\', '/'));
                await Assert.That(back.Strict).IsEqualTo(spec.Strict);
                if (kind == GitHooks.Push) await Assert.That(back.Semantic).IsEqualTo(spec.Semantic);
            }
        await Assert.That(GitHooks.Parse("exec skarp check --staged", GitHooks.Commit)).IsNull(); // not Skarp's form
    }

    static IEnumerable<GitHookSpec> Specs()
    {
        foreach (var program in new[] { "skarp", "C:/Tools/skarp.exe", "/opt/skarp tools/skarp" })
            foreach (var strict in new[] { false, true })
                foreach (var semantic in new[] { false, true })
                    yield return new GitHookSpec { Command = program, CommandGiven = program != "skarp", Strict = strict, Semantic = semantic, PrePush = true };
    }

    [Test]
    public async Task Config_entries_are_read_and_changed_command_first_and_removed_event_first()
    {
        string List(params string[] pairs) => string.Concat(pairs.Select(p => p.Replace('=', '\n') + "\0"));
        GitHookEntry Commit(string list) => GitHooks.Read(list, GitHooks.Commit);
        var list = List("core.bare=false", "hook.other.command=echo", "hook.other.event=pre-commit");
        await Assert.That(Commit(list).Present || GitHooks.Read(list, GitHooks.Push).Present).IsFalse();
        var install = GitHooks.Install(Commit(list), "c");
        await Assert.That(install.Select(GitConfigOp.Key)).IsEquivalentTo(new[] { "hook.skarp-pre-commit.command", "hook.skarp-pre-commit.event" });
        await Assert.That(install[0]).IsEquivalentTo(new[] { "config", "--local", "--replace-all", "hook.skarp-pre-commit.command", "c" });

        var orphan = Commit(List("hook.skarp-pre-commit.event=pre-commit"));
        await Assert.That(orphan.EventFirst).IsTrue();
        await Assert.That(GitHooks.Install(orphan, "c").Select(GitConfigOp.Key)).IsEquivalentTo(new[] { "hook.skarp-pre-commit.command" });

        var twice = Commit(List("hook.skarp-pre-commit.command=c", "hook.skarp-pre-commit.event=pre-commit", "hook.skarp-pre-commit.event=pre-commit"));
        await Assert.That(twice.Complete).IsFalse();
        await Assert.That(GitHooks.Install(twice, "c").Select(GitConfigOp.Key)).IsEquivalentTo(new[] { "hook.skarp-pre-commit.event" }); // --replace-all: one value

        var done = Commit(List("HOOK.skarp-pre-commit.COMMAND=c", "hook.skarp-pre-commit.event=pre-commit"));
        await Assert.That(GitHooks.Install(done, "c")).IsEmpty();
        var remove = GitHooks.Remove(done);
        await Assert.That(remove.Select(GitConfigOp.Key)).IsEquivalentTo(new[] { "hook.skarp-pre-commit.event", "hook.skarp-pre-commit.command" });
        await Assert.That(remove.All(GitConfigOp.IsUnset)).IsTrue();
        await Assert.That(remove[0]).IsEquivalentTo(new[] { "config", "--local", "--unset-all", "hook.skarp-pre-commit.event" });
    }

    [Test]
    public async Task Git_versions_from_2_54_have_config_hooks()
    {
        await Assert.That(GitHooks.Version("git version 2.56.1.windows.1")).IsEqualTo(2056);
        await Assert.That(GitHooks.Supported(GitHooks.Version("git version 2.54.0"))).IsTrue();
        await Assert.That(GitHooks.Supported(GitHooks.Version("git version 3.0.0"))).IsTrue();
        await Assert.That(GitHooks.Supported(GitHooks.Version("git version 2.53.2"))).IsFalse();
        await Assert.That(GitHooks.Version("hub version 2.14")).IsEqualTo(-1);
    }

    /// <summary>The shared-script fixtures: none, a plain sh script, a bash script through env, one that already holds an
    /// older Skarp block in the middle, and an empty file.</summary>
    internal static IEnumerable<(string Name, string? Text)> ScriptFixtures()
    {
        yield return ("none", null);
        yield return ("sh", "#!/bin/sh\necho \"lint\"\nnpm test || exit 1\n");
        yield return ("bash", "#!/usr/bin/env bash\nset -e\nexit_code=0\n");
        yield return ("older block", "#!/bin/sh\necho a\n# >>> skarp >>>\nskarp check --staged || exit $?\n# <<< skarp <<<\necho b\n");
    }

    [Test]
    public async Task The_block_is_appended_replaced_and_removed_byte_for_byte()
    {
        var spec = new GitHookSpec { PrePush = true };
        foreach (var (name, text) in ScriptFixtures())
            foreach (var kind in GitHooks.Kinds)
            {
                var installed = GitHookScripts.Install(text, spec, kind);
                await Assert.That(installed).DoesNotContain("\r").Because(name);
                await Assert.That(installed).Contains(GitHookScripts.Block(spec, kind)).Because(name);
                await Assert.That(GitHookScripts.Install(installed, spec, kind)).IsEqualTo(installed).Because(name + ": a second install changes nothing");
                var removed = GitHookScripts.Remove(installed);
                var original = text is null ? null : name == "older block" ? "#!/bin/sh\necho a\necho b\n" : text;
                await Assert.That(removed).IsEqualTo(original).Because(name);
                if (text is not null && name != "older block") await Assert.That(installed).StartsWith(text).Because(name + ": appended, never overwritten");
            }
        await Assert.That(GitHookScripts.Install(null, spec, GitHooks.Commit)).StartsWith("#!/bin/sh\n# >>> skarp >>>\n");
        await Assert.That(GitHookScripts.Block(new GitHookSpec { Strict = true }, GitHooks.Commit))
            .IsEqualTo("# >>> skarp >>>\n# Skarp's commit check (skarp install git-hooks --shared); --remove --shared takes this block out.\nskarp check --staged --gate || exit $?\n# <<< skarp <<<\n");
        await Assert.That(GitHookScripts.Block(spec, GitHooks.Push)).Contains("--push-check \"$@\" || exit $?");
    }

    [Test]
    public async Task Only_shell_scripts_are_edited()
    {
        foreach (var shell in new[] { "#!/bin/sh\n", "#!/bin/bash\n", "#!/usr/bin/env sh\n", "#!/usr/bin/env bash\n", "#! /bin/dash -e\n", "echo no shebang\n", "" })
            await Assert.That(GitHookScripts.IsShell(shell)).IsTrue().Because(shell);
        foreach (var other in new[] { "#!/usr/bin/env python3\n", "#!/usr/bin/node\n", "#!/usr/bin/env pwsh\n", "#!\n" })
            await Assert.That(GitHookScripts.IsShell(other)).IsFalse().Because(other);
    }

    [Test]
    public async Task The_call_parses_back_through_every_printed_command()
    {
        foreach (var remove in new[] { false, true })
            foreach (var spec in Specs())
                foreach (var shared in new[] { false, true })
                    foreach (bool? push in new bool?[] { null, true, false })
                    {
                        if (shared && push == false) continue;
                        var s = remove ? new GitHookSpec { Shared = shared } : spec with { Shared = shared, PrePush = push, Semantic = push == true && spec.Semantic };
                        var call = new GitHooksCall(remove, s);
                        var (back, error) = GitHooksCall.Parse(call.Args());
                        await Assert.That(back).IsEqualTo(call).Because(error ?? string.Join(" ", call.Args()));
                        await Assert.That(call.Args()).DoesNotContain("--yes");
                    }
    }
}
