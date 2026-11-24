using System.Text;
using SkarpSharp.History;

namespace SkarpSharp.Tests.History;

/// <summary>
/// Byte fixtures for the <c>git log -z --numstat</c> parser. Every fixture is fed at every chunk size
/// from 1 to its full length and must give the same records, so tokens split across read buffers are covered.
/// </summary>
public class GitLogParserTests
{
    /// <summary>Records what the parser emits, as text lines.</summary>
    sealed class Recorder(PathTable paths) : IHistoryConsumer
    {
        public List<string> Lines { get; } = [];
        public bool Completed { get; private set; }
        public void OnCommit(RawCommit c)
        {
            Lines.Add($"commit {c.Hash} parents={c.ParentCount} author={c.AuthorName} <{c.AuthorEmail}> at={c.AuthorTime} ct={c.CommitterTime}");
            foreach (var r in c.Files)
                Lines.Add($"  {(r.Binary ? "bin" : $"{r.Added}/{r.Deleted}")} {(r.IsRename ? paths[r.OldPathId] + " -> " : "")}{paths[r.PathId]}");
        }
        public void OnComplete() => Completed = true;
    }

    static byte[] Header(string hash, string parents, string name, string email, long at, long ct) =>
        Encoding.UTF8.GetBytes($"\u0001{hash}\u001f{parents}\u001f{name}\u001f{email}\u001f{at}\u001f{ct}\0");

    static byte[] Concat(params object[] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts)
        {
            var b = p as byte[] ?? Encoding.UTF8.GetBytes((string)p);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    // Returns a value, so it checks synchronously (Assert.Fail throws).
    static (List<string> Lines, ReaderTotals Totals) Parse(byte[] bytes, int chunk)
    {
        var paths = new PathTable();
        var rec = new Recorder(paths);
        var parser = new GitLogParser(paths, rec);
        for (int i = 0; i < bytes.Length; i += chunk)
            parser.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
        parser.Complete();
        if (!rec.Completed) Assert.Fail("the parser did not complete");
        return (rec.Lines, parser.Totals);
    }

    static async Task AssertAllChunkSizes(byte[] bytes, string[] expected, long expectedRows)
    {
        for (int chunk = 1; chunk <= bytes.Length; chunk++)
        {
            var (lines, totals) = Parse(bytes, chunk);
            await Assert.That(expected.SequenceEqual(lines)).IsTrue().Because($"chunk {chunk}:\n{string.Join("\n", lines)}");
            await Assert.That(totals.Rows).IsEqualTo(expectedRows);
            await Assert.That(totals.Malformed).IsEqualTo(0);
        }
    }

    const string H1 = "1111111111111111111111111111111111111111";
    const string H2 = "2222222222222222222222222222222222222222";
    const string H3 = "3333333333333333333333333333333333333333";

    [Test]
    public async Task Plain_rows_and_root_commit()
    {
        var bytes = Concat(
            Header(H2, H1, "Alice", "alice@x.org", 1700000100, 1700000200), "\n1\t0\tA.cs\0", "1\t0\tB.cs\0",
            Header(H1, "", "Alice", "alice@x.org", 1700000000, 1700000000), "\n10\t0\tA.cs\0", "3\t0\tB.cs\0");
        await AssertAllChunkSizes(bytes,
        [
            $"commit {H2} parents=1 author=Alice <alice@x.org> at=1700000100 ct=1700000200", "  1/0 A.cs", "  1/0 B.cs",
            $"commit {H1} parents=0 author=Alice <alice@x.org> at=1700000000 ct=1700000000", "  10/0 A.cs", "  3/0 B.cs",
        ], 4);
    }

    [Test]
    public async Task Rename_with_spaces_in_the_path()
    {
        // Observed on git 2.56.1: empty path field, then old and new.
        var bytes = Concat(Header(H2, H1, "Alice", "alice@x.org", 5, 6), "\n0\t0\t\0B.cs\0sub/B with space.cs\0", "2\t1\t\0old.cs\0new.cs\0");
        await AssertAllChunkSizes(bytes,
        [
            $"commit {H2} parents=1 author=Alice <alice@x.org> at=5 ct=6", "  0/0 B.cs -> sub/B with space.cs", "  2/1 old.cs -> new.cs",
        ], 2);
        await Assert.That(Parse(bytes, 3).Totals.Renames).IsEqualTo(2);
    }

    [Test]
    public async Task Rename_as_first_entry_followed_by_plain_entry()
    {
        var bytes = Concat(Header(H2, H1, "A", "a@x", 1, 1), "\n5\t5\t\0x/Old Name.cs\0y/New Name.cs\0", "1\t1\tz.cs\0");
        await AssertAllChunkSizes(bytes, [$"commit {H2} parents=1 author=A <a@x> at=1 ct=1", "  5/5 x/Old Name.cs -> y/New Name.cs", "  1/1 z.cs"], 2);
    }

    [Test]
    public async Task Binary_rows_count_zero_and_are_flagged()
    {
        var bytes = Concat(Header(H1, "", "Alice", "alice@x.org", 1, 2), "\n-\t-\tdata.bin\0", "-\t-\t\0logo.png\0img/logo.png\0");
        await AssertAllChunkSizes(bytes, [$"commit {H1} parents=0 author=Alice <alice@x.org> at=1 ct=2", "  bin data.bin", "  bin logo.png -> img/logo.png"], 2);
        var (_, t) = Parse(bytes, 5);
        await Assert.That(t.BinaryRows).IsEqualTo(2);
        await Assert.That(t.Added + t.Deleted).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_commits_have_only_a_header()
    {
        // Observed: an empty commit is "\x01header\0" directly followed by the next header; also as the last commit.
        var bytes = Concat(
            Header(H3, H2, "A", "a@x", 3, 3), "\n1\t0\ta.txt\0",
            Header(H2, H1, "A", "a@x", 2, 2),
            Header(H1, "", "A", "a@x", 1, 1));
        await AssertAllChunkSizes(bytes,
        [
            $"commit {H3} parents=1 author=A <a@x> at=3 ct=3", "  1/0 a.txt",
            $"commit {H2} parents=1 author=A <a@x> at=2 ct=2",
            $"commit {H1} parents=0 author=A <a@x> at=1 ct=1",
        ], 1);
    }

    [Test]
    public async Task Commit_emptied_by_ignore_whitespace_keeps_its_line_feed()
    {
        // Observed on git 2.56.1 with -w: "\x01header\0\n" directly followed by the next header (no entry token).
        var bytes = Concat(
            Header(H3, H2, "A", "a@x", 3, 3), "\n",
            Header(H2, H1, "A", "a@x", 2, 2), "\n0\t1\ta.txt\0",
            Header(H1, "", "A", "a@x", 1, 1), "\n");
        await AssertAllChunkSizes(bytes,
        [
            $"commit {H3} parents=1 author=A <a@x> at=3 ct=3",
            $"commit {H2} parents=1 author=A <a@x> at=2 ct=2", "  0/1 a.txt",
            $"commit {H1} parents=0 author=A <a@x> at=1 ct=1",
        ], 1);
    }

    [Test]
    public async Task Tab_newline_and_non_ascii_paths()
    {
        // -z never quotes paths: a tab in the path stays after the second tab; non-ASCII stays raw UTF-8.
        var bytes = Concat(Header(H1, "", "Jürgen Müller", "jm@x.de", 1, 1),
            "\n1\t2\tdir/with\ttab.cs\0", "3\t4\tÜmlaut/Größe.cs\0", "5\t6\t日本/ファイル.cs\0", "7\t8\tline\nfeed.cs\0");
        await AssertAllChunkSizes(bytes,
        [
            $"commit {H1} parents=0 author=Jürgen Müller <jm@x.de> at=1 ct=1",
            "  1/2 dir/with\ttab.cs", "  3/4 Ümlaut/Größe.cs", "  5/6 日本/ファイル.cs", "  7/8 line\nfeed.cs",
        ], 4);
    }

    [Test]
    public async Task Octopus_merge_header_counts_parents()
    {
        var bytes = Concat(Header(H3, $"{H1} {H2} 4444444444444444444444444444444444444444", "M", "m@x", 9, 9));
        await AssertAllChunkSizes(bytes, [$"commit {H3} parents=3 author=M <m@x> at=9 ct=9"], 0);
    }

    [Test]
    public async Task Path_starting_with_the_marker_byte_is_a_rename_operand_not_a_header()
    {
        var bytes = Concat(Header(H1, "", "A", "a@x", 1, 1), "\n1\t1\t\0\u0001odd.cs\0even.cs\0");
        await AssertAllChunkSizes(bytes, [$"commit {H1} parents=0 author=A <a@x> at=1 ct=1", "  1/1 \u0001odd.cs -> even.cs"], 1);
    }

    [Test]
    public async Task Same_path_bytes_intern_to_one_id()
    {
        var paths = new PathTable();
        var a = paths.Intern("src/A.cs"u8);
        await Assert.That(paths.Intern("src/A.cs"u8)).IsEqualTo(a);
        await Assert.That(paths.Intern("src/a.cs"u8)).IsNotEqualTo(a);
        await Assert.That(paths[a]).IsEqualTo("src/A.cs");
        await Assert.That(paths.TryGet("src/A.cs", out var b) && b == a).IsTrue();
    }

    [Test]
    public async Task Malformed_tokens_are_counted_not_thrown()
    {
        var bytes = Concat("garbage\0", Header(H1, "", "A", "a@x", 1, 1), "\nnot-a-row\0", "x\ty\tz.cs\0", "1\t1\tok.cs\0");
        var (lines, t) = Parse(bytes, 3);
        await Assert.That(lines).IsEquivalentTo(new[] { $"commit {H1} parents=0 author=A <a@x> at=1 ct=1", "  1/1 ok.cs" }, CollectionOrdering.Matching);
        await Assert.That(t.Malformed).IsEqualTo(3);
    }
}