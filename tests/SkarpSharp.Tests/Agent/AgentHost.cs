using System.Text;
using SkarpSharp.Commands;

namespace SkarpSharp.Tests.Agent;

/// <summary>
/// Runs skarp commands in process for the agent-loop tests: a private tracking home (what SKARP_HOME would
/// be), a settable clock, a working folder and standard input.
/// </summary>
internal sealed class AgentHost : IDisposable
{
    readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("skarp-agent-");

    public AgentHost() => Home = Path.Combine(_temp.FullName, "home");

    public string Home { get; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Variables for skarp's git processes (a <see cref="History.SyntheticRepo"/>'s isolation); null: inherited.</summary>
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; set; }

    /// <summary>A new folder outside any git work tree.</summary>
    public string Folder(string name)
    {
        var dir = Path.Combine(_temp.FullName, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public (int Exit, string Out, string Err) RunWithInput(string cwd, string? stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var stdout = new MemoryStream();
        int exit = SkarpCommand.RunAsync(args, new CommandIO
        {
            Out = o, Error = e, OpenStdout = () => new NonClosing(stdout),
            In = stdin is null ? null : new StringReader(stdin), WorkingDirectory = cwd, Home = Home, Clock = () => Now, GitEnvironment = GitEnvironment,
        }).GetAwaiter().GetResult();
        var json = Encoding.UTF8.GetString(stdout.ToArray());
        return (exit, o + json, e.ToString());
    }

    public (int Exit, string Out, string Err) Run(string cwd, params string[] args) => RunWithInput(cwd, null, args);

    /// <summary>The non-empty lines of a command's standard output.</summary>
    public static List<string> Lines(string output) =>
        [.. output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Length > 0)];

    /// <summary>The finding lines of the text format (indented, starting with the lead word), without the indent.</summary>
    public static List<string> FindingLines(string output) => [.. Lines(output).Where(IsFinding).Select(l => l[2..])];

    static readonly System.Text.RegularExpressions.Regex Finding = new(@"^  (new|worse|better|fixed|warning|notice|recorded) ");
    static readonly System.Text.RegularExpressions.Regex Heading = new(@"^(Skarp: )?(?<path>[^ ]+\.cs)( health .*)?$");

    static bool IsFinding(string line) => Finding.IsMatch(line);

    /// <summary>The file headings (<c>src/A.cs health 9.0 -&gt; 8.8</c>), without a leading <c>Skarp: </c>.</summary>
    public static List<string> Headings(string output) =>
        [.. Lines(output).Where(l => Heading.IsMatch(l)).Select(l => l.StartsWith("Skarp: ", StringComparison.Ordinal) ? l["Skarp: ".Length..] : l)];

    /// <summary>The file headings that carry a health score.</summary>
    public static List<string> HealthOnly(string output) => [.. Headings(output).Where(l => l.Contains(" health ", StringComparison.Ordinal))];

    /// <summary>Each finding line with its file heading's path before it (<c>src/A.cs: worse line 5 ...</c>).</summary>
    public static List<string> WithPaths(string output)
    {
        var list = new List<string>();
        string path = "";
        foreach (var l in Lines(output))
        {
            var m = Heading.Match(l);
            if (m.Success) path = m.Groups["path"].Value;
            else if (IsFinding(l)) list.Add(path + ": " + l[2..]);
        }
        return list;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_temp.FullName, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            _temp.Delete(recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    /// <summary>A class whose method <c>M{index}</c> has <paramref name="parameters"/> parameters (too-many-parameters:
    /// notice from 5, warning from 8 in the default preset).</summary>
    public static string Method(int parameters, int index = 0) =>
        $"    public void M{index}({string.Join(", ", Enumerable.Range(0, parameters).Select(i => $"int p{i}"))}) {{ }}\n";

    public static string Class(string name, params string[] members) => $"namespace Demo;\n\npublic class {name}\n{{\n{string.Concat(members)}}}\n";
}
