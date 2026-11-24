using System.Diagnostics;
using System.Text;

namespace SkarpSharp.Agent;

/// <summary>A blob read from git: its object name and content (null when git has no such object).</summary>
public sealed class GitBlob(string? oid, byte[]? content)
{
    public string? Oid { get; } = oid;
    public byte[]? Content { get; } = content;
    public bool Exists => Oid is not null;
}

/// <summary>
/// The staged side of a commit, read through git so that <c>GIT_INDEX_FILE</c> (relative or
/// absolute, as git sets it for <c>commit -a</c> and <c>commit &lt;file&gt;</c>) is honoured: one
/// <c>git diff --cached --name-only -z --diff-filter=ACMR</c> for the names and one <c>git cat-file --batch-command</c>,
/// started at the same time, for the blobs (as stored: line endings and byte order mark as in the index). Never opens
/// <c>.git/index</c> and never reads standard input.
/// </summary>
public sealed class StagedIndex : IDisposable
{
    readonly Process _catFile;
    readonly Stream _out;
    readonly StreamWriter _in;
    const int BufferSize = 64 * 1024, ExitWaitMilliseconds = 2000;
    readonly byte[] _buffer = new byte[BufferSize];
    int _start, _end;

    StagedIndex(Process catFile)
    {
        _catFile = catFile;
        _out = catFile.StandardOutput.BaseStream;
        _in = catFile.StandardInput;
        _in.NewLine = "\n";
        _in.AutoFlush = false;
    }

    /// <summary>The staged paths (relative to the work tree root, '/' separators) that are added, copied, modified or
    /// renamed (under the new name); deleted files are not listed.</summary>
    public IReadOnlyList<string> Paths { get; private init; } = [];

    /// <summary>Starts both git processes in <paramref name="root"/> (the work tree root) and waits for the names (the
    /// blob reader starts meanwhile). Synchronous on purpose: a hook is one short process, and async state machines over
    /// new types cost Native AOT size.</summary>
    /// <exception cref="InvalidOperationException">git could not be started or failed (not a repository).</exception>
    public static StagedIndex Open(string root, IReadOnlyDictionary<string, string?>? environment)
    {
        var diff = Start(root, ["diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR"], environment, input: false);
        Process cat;
        try { cat = Start(root, ["cat-file", "--batch-command"], environment, input: true); }
        catch { diff.Dispose(); throw; }
        string names;
        try
        {
            var err = diff.StandardError.ReadToEndAsync();
            names = diff.StandardOutput.ReadToEnd();
            diff.WaitForExit();
            if (diff.ExitCode != 0) throw new InvalidOperationException($"git diff --cached failed: {err.Result.Trim()}");
        }
        catch { cat.Kill(); cat.Dispose(); throw; }
        finally { diff.Dispose(); }
        return new StagedIndex(cat) { Paths = names.Split('\0', StringSplitOptions.RemoveEmptyEntries) };
    }

    /// <summary>The blob reader alone (no staged names), for reading files at <c>HEAD</c>.</summary>
    /// <exception cref="InvalidOperationException">git could not be started.</exception>
    public static StagedIndex Blobs(string root, IReadOnlyDictionary<string, string?>? environment) =>
        new(Start(root, ["cat-file", "--batch-command"], environment, input: true));

    static Process Start(string root, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment, bool input)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = !input, RedirectStandardInput = true,
            WorkingDirectory = root, CreateNoWindow = true, StandardOutputEncoding = input ? null : new UTF8Encoding(false),
            StandardInputEncoding = input ? new UTF8Encoding(false) : null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        // Standard input is always redirected, and closed when git reads none, so git never inherits the
        // caller's (the MCP server's protocol pipe).
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("git not started"); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException($"git could not be started: {ex.Message}"); }
        if (!input) p.StandardInput.Close();
        return p;
    }

    /// <summary>The content of <paramref name="name"/> (<c>:path</c> for the index, <c>HEAD:path</c>); a missing object
    /// gives a blob that does not exist. A path with a line break cannot be asked for and counts as missing.</summary>
    public GitBlob Contents(string name) => Ask("contents", name);

    /// <summary>The object name of <paramref name="name"/> only (no content).</summary>
    public GitBlob Info(string name) => Ask("info", name);

    GitBlob Ask(string command, string name)
    {
        if (name.Contains('\n') || name.Contains('\r')) return new GitBlob(null, null);
        _in.WriteLine($"{command} {name}");
        _in.Flush();
        var header = ReadLine() ?? throw new InvalidOperationException("git cat-file stopped");
        var parts = header.Split(' ');
        // "<oid> <type> <size>", or "<name> missing" / "<name> ambiguous".
        if (parts.Length != 3 || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size))
            return new GitBlob(null, null);
        if (command == "info") return new GitBlob(parts[0], null);
        var content = ReadBytes((int)size);
        ReadBytes(1); // the line feed after the content
        return parts[1] == "blob" ? new GitBlob(parts[0], content) : new GitBlob(null, null);
    }

    bool Fill()
    {
        if (_start == _end) { _start = 0; _end = 0; }
        else if (_end == _buffer.Length) { Array.Copy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
        int n = _out.Read(_buffer, _end, _buffer.Length - _end);
        _end += n;
        return n > 0;
    }

    string? ReadLine()
    {
        var line = new MemoryStream();
        while (true)
        {
            int nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                line.Write(_buffer, _start, nl - _start);
                _start = nl + 1;
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }
            line.Write(_buffer, _start, _end - _start);
            _start = _end;
            if (!Fill()) return null;
        }
    }

    byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        int got = 0;
        while (got < count)
        {
            if (_start == _end && !Fill()) throw new InvalidOperationException("git cat-file stopped early");
            int n = Math.Min(count - got, _end - _start);
            Array.Copy(_buffer, _start, result, got, n);
            _start += n;
            got += n;
        }
        return result;
    }

    public void Dispose()
    {
        try
        {
            _in.Close();
            if (!_catFile.WaitForExit(ExitWaitMilliseconds)) _catFile.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // cat-file already exited (the pipe is closed): nothing left to stop.
        }
        _catFile.Dispose();
    }
}
