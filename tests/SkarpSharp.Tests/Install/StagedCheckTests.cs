using SkarpSharp.Commands;
using SkarpSharp.Configuration;
using SkarpSharp.Tests.History;

namespace SkarpSharp.Tests.Install;

/// <summary>
/// <c>skarp check --staged</c> judges the
/// index, never the working tree, on each fixture: a plain commit, <c>commit -a</c> and <c>commit &lt;file&gt;</c> (git's
/// temporary index in <c>GIT_INDEX_FILE</c>, absolute), partial staging, a rename, a deletion, a staged baseline and staged
/// settings, no baseline, and zero files. Real git in a temporary repository with the machine's git configuration shut
/// out (<see cref="SyntheticRepo"/>); nothing reads <c>~/.skarp</c>.
/// </summary>
public sealed class StagedCheckTests : IDisposable
{
    readonly SyntheticRepo _repo = new();
    readonly string _home = Directory.CreateTempSubdirectory("skarp-staged-home-").FullName;

    public void Dispose()
    {
        _repo.Dispose();
        try { Directory.Delete(_home, true); } catch (IOException) { }
    }

    /// <summary>A small, clean class.</summary>
    internal const string Good = "namespace N;\n\npublic class A\n{\n    public int F(int x) => x + 1;\n}\n";

    /// <summary>A method far over the brain-method thresholds (a gate-set warning).</summary>
    internal static string Bad(string type = "A")
    {
        var body = string.Join("\n", Enumerable.Range(0, 80).Select(i =>
            $"        if (x == {i} && y > {i} || x < -{i}) {{ if (y > {i}) {{ for (int k = 0; k < {i}; k++) {{ if (k % 2 == 0) {{ x++; }} else {{ y--; }} }} }} else {{ y--; }} }}"));
        return $"namespace N;\n\npublic class {type}\n{{\n    public int F(int x, int y)\n    {{\n{body}\n        return x + y;\n    }}\n}}\n";
    }

    async Task<(int Exit, string Out, string Error)> Check(IReadOnlyDictionary<string, string?>? extra = null, params string[] args)
    {
        var env = new Dictionary<string, string?>(_repo.Environment);
        if (extra is not null) foreach (var (k, v) in extra) env[k] = v;
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int exit = await SkarpCommand.RunAsync(["check", "--staged", .. args], new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => Stream.Null, WorkingDirectory = _repo.Root, Home = _home,
            SettingsLocations = SettingsLocations.None, GitEnvironment = env, In = new ThrowingReader(),
        });
        return (exit, o.ToString(), e.ToString());
    }

    Task<(int Exit, string Out, string Error)> Gate() => Check(null, "--gate");

    /// <summary>Records the baseline of the working tree in process and stages it.</summary>
    async Task Baseline()
    {
        var o = new StringWriter();
        int exit = await SkarpCommand.RunAsync(["baseline", _repo.Root, "--no-history", "--no-semantic"], new CommandIO
        {
            Out = o, Error = o, OpenStdout = () => Stream.Null, WorkingDirectory = _repo.Root, SettingsLocations = SettingsLocations.None,
        });
        if (exit != 0) throw new InvalidOperationException(o.ToString());
        _repo.Git("add", ".skarp-baseline.json");
    }

    /// <summary>A repository with <c>src/A.cs</c> (good) and its baseline committed.</summary>
    async Task Committed()
    {
        _repo.Write("src/A.cs", Good);
        await Baseline();
        _repo.Commit("init", DateTimeOffset.UnixEpoch);
    }

    static readonly DateTimeOffset When = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task Plain_commit_judges_the_staged_blob_not_the_working_file()
    {
        await Committed();
        _repo.Write("src/A.cs", Bad());
        _repo.Git("add", "src/A.cs");
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(3).Because(error);
        await Assert.That(output).IsEmpty();
        // The file also lost 2.8 health, past the 1.0 limit.
        await Assert.That(error).StartsWith("Skarp stopped this commit: 1 new problem in 1 staged file; health past its limit in 1 staged file.\n");
        await Assert.That(error).Contains("\nsrc/A.cs health 10.0 -> 7.2 (down 2.8, limit under 1.0) (10 = healthy)\n");
        await Assert.That(error).Contains("\nsrc/A.cs health ").And.Contains("  new line 5 A.F brain-method: long (");
        await Assert.That(error).Contains("`git commit --no-verify`");
        await Assert.That(error).Contains("skarp baseline .");

        _repo.Write("src/A.cs", Good); // fixed on disk, not staged: the commit would still hold the bad version
        (exit, _, error) = await Gate();
        await Assert.That(exit).IsEqualTo(3).Because(error);

        _repo.Git("add", "src/A.cs");
        _repo.Write("src/A.cs", Bad()); // staged good, working bad: the commit is good
        (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output + error).IsEmpty();
        await Assert.That(Directory.Exists(Path.Combine(_repo.Root, ".skarp", "checked"))).IsFalse();
    }

    [Test]
    public async Task Commit_all_and_commit_of_one_file_use_the_index_git_names()
    {
        await Committed();
        _repo.Write("src/B.cs", Good.Replace("class A", "class B"));
        _repo.Git("add", "src/B.cs");
        _repo.Commit("b", When);
        _repo.Write("src/A.cs", Bad());
        _repo.Write("src/B.cs", Bad("B"));
        // Nothing staged in .git/index: a plain commit checks nothing.
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output + error).IsEmpty();

        // commit -a: git's index.lock holds every change (absolute GIT_INDEX_FILE).
        var all = Path.Combine(_repo.Root, ".git", "index-all.lock");
        File.Copy(Path.Combine(_repo.Root, ".git", "index"), all);
        _repo.Git(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = all }, "add", "-u");
        (exit, _, error) = await Check(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = all }, "--gate");
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(error).Contains("src/A.cs").And.Contains("src/B.cs");

        // commit src/B.cs: a next-index-<pid>.lock from HEAD plus that file only.
        var one = Path.Combine(_repo.Root, ".git", "next-index-1234.lock");
        _repo.Git(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = one }, "read-tree", "HEAD");
        _repo.Git(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = one }, "add", "src/B.cs");
        (exit, _, error) = await Check(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = one }, "--gate");
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(error).Contains("src/B.cs").And.DoesNotContain("src/A.cs");

        // The relative form git sets for a plain commit, read against the root.
        (exit, _, _) = await Check(new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = ".git/index" }, "--gate");
        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task Partial_staging_judges_only_the_staged_hunk()
    {
        // Two types far apart: the bad change is made to the first, a harmless one to the second; only the second is staged.
        var second = "\npublic class Z\n{\n    public int G() => 1;\n}\n";
        _repo.Write("src/A.cs", Good + string.Concat(Enumerable.Repeat("\n// spacer", 40)) + second);
        await Baseline();
        _repo.Commit("init", When);
        _repo.Write("src/A.cs", Bad() + string.Concat(Enumerable.Repeat("\n// spacer", 40)) + second.Replace("=> 1", "=> 2"));
        var diff = _repo.Git("diff", "-U0", "src/A.cs").Replace("\r\n", "\n");
        int last = diff.LastIndexOf("\n@@", StringComparison.Ordinal);
        int firstHunk = diff.IndexOf("\n@@", StringComparison.Ordinal);
        var patch = diff[..(firstHunk + 1)] + diff[(last + 1)..];
        var patchFile = Path.Combine(_home, "second.patch");
        File.WriteAllText(patchFile, patch);
        _repo.Git("apply", "--cached", "--unidiff-zero", patchFile);
        var staged = _repo.Git("show", ":src/A.cs");
        await Assert.That(staged).Contains("=> 2").And.DoesNotContain("for (int k");
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output + error).IsEmpty();
    }

    [Test]
    public async Task A_rename_is_judged_under_its_new_name_and_a_deletion_checks_nothing()
    {
        await Committed();
        _repo.Git("mv", "src/A.cs", "src/A2.cs");
        _repo.Write("src/A2.cs", Bad());
        _repo.Git("add", "src/A2.cs");
        var (exit, _, error) = await Gate();
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(error).Contains("\nsrc/A2.cs health ").And.Contains("  new line 5 A.F brain-method: long (");

        _repo.Git("reset", "-q", "--hard");
        _repo.Git("rm", "-q", "src/A.cs");
        (exit, var output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output + error).IsEmpty();
    }

    [Test]
    public async Task A_staged_baseline_is_used_and_named()
    {
        await Committed();
        _repo.Write("src/A.cs", Bad());
        _repo.Git("add", "src/A.cs");
        await Baseline(); // accepts the findings and stages the new baseline
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(error).Contains("Skarp: this commit changes .skarp-baseline.json");
        await Assert.That(output).IsEmpty();

        // The accepted baseline on disk but not staged: the commit still compares with the committed one.
        _repo.Git("reset", "-q", "--", ".skarp-baseline.json");
        (exit, _, error) = await Gate();
        await Assert.That(exit).IsEqualTo(3);
        await Assert.That(error).DoesNotContain("changes .skarp-baseline.json");
    }

    [Test]
    public async Task Staged_settings_are_read_from_the_index()
    {
        await Committed();
        _repo.Write("src/A.cs", Bad());
        _repo.Git("add", "src/A.cs");
        _repo.Write(".skarp.json", """{ "version": 1, "exclude": ["src/**"] }""");
        _repo.Git("add", ".skarp.json");
        File.Delete(Path.Combine(_repo.Root, ".skarp.json")); // only in the index
        var (exit, _, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0).Because(error);

        // On disk only (not staged): the commit does not hold it, so it does not apply.
        _repo.Git("rm", "-q", "--cached", ".skarp.json");
        _repo.Write(".skarp.json", """{ "version": 1, "exclude": ["src/**"] }""");
        (exit, _, _) = await Gate();
        await Assert.That(exit).IsEqualTo(3);
    }

    [Test]
    public async Task Without_a_baseline_in_the_commit_one_note_and_exit_0()
    {
        _repo.Write("src/A.cs", Good);
        _repo.Commit("init", When);
        _repo.Write("src/A.cs", Bad());
        _repo.Git("add", "src/A.cs");
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).IsEqualTo("Skarp: no baseline (.skarp-baseline.json) in the commit, so it is not checked: `skarp baseline .` records one, then `git add .skarp-baseline.json`\n");

        // A baseline on disk that is not committed or staged does not count either.
        await Baseline();
        _repo.Git("reset", "-q", "--", ".skarp-baseline.json");
        (exit, _, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(error).Contains("Skarp: no baseline");
    }

    [Test]
    public async Task Zero_staged_cs_files_is_silent_and_line_endings_as_stored_do_not_matter()
    {
        await Committed();
        _repo.Write("readme.txt", "hello");
        _repo.Git("add", "readme.txt");
        var (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output + error).IsEmpty();

        // A CRLF blob with a byte order mark: the same findings (none new).
        _repo.WriteBytes("src/A.cs", [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Good.Replace("\n", "\r\n"))]);
        _repo.Git("add", "src/A.cs");
        (exit, output, error) = await Gate();
        await Assert.That(exit).IsEqualTo(0).Because(error);
        await Assert.That(output + error).IsEmpty();
    }

    [Test]
    public async Task Without_gate_the_lines_go_to_standard_error_and_exit_0_and_usage_is_checked()
    {
        await Committed();
        _repo.Write("src/A.cs", Bad());
        _repo.Git("add", "src/A.cs");
        var (exit, output, error) = await Check();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output).IsEmpty();
        await Assert.That(error).Contains("\nsrc/A.cs health ").And.Contains("  new line 5 A.F brain-method: ").And.DoesNotContain("Skarp stopped");

        (exit, _, error) = await Check(null, "src/A.cs");
        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(error).Contains("takes no files");
        foreach (var bad in new[] { "--track", "--no-baseline", "--semantic" })
        {
            (exit, _, error) = await Check(null, bad);
            await Assert.That(exit).IsEqualTo(2);
            await Assert.That(error).Contains($"does not take {bad}");
        }
    }

    /// <summary>Standard input that fails the test when read: the commit hook never reads it.</summary>
    sealed class ThrowingReader : TextReader
    {
        public override int Read() => throw new InvalidOperationException("check --staged read standard input");
        public override string ReadToEnd() => throw new InvalidOperationException("check --staged read standard input");
        public override Task<string> ReadToEndAsync() => throw new InvalidOperationException("check --staged read standard input");
    }
}
