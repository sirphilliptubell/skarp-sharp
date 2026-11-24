using System.Globalization;
using System.Xml;

namespace SkarpSharp.Coverage;

/// <summary>One Cobertura report as skarp keeps it: per file name as written in the report, the distinct line numbers
/// and whether any entry of the line had hits.</summary>
public sealed class CoberturaReport
{
    public required string Path { get; init; }
    /// <summary>The <c>timestamp</c> attribute (seconds since 1970 UTC); 0 when absent or not a number.</summary>
    public long Timestamp { get; init; }
    /// <summary><c>coverlet</c>, <c>microsoft</c>, <c>reportgenerator</c> or <c>unknown</c> (from the document's shape).</summary>
    public required string Producer { get; init; }
    /// <summary>The <c>&lt;sources&gt;</c> entries in order (Coverlet's base folders); empty for Microsoft reports.</summary>
    public required IReadOnlyList<string> Sources { get; init; }
    /// <summary>File name (as written) to line number to covered. Duplicate entries of a line (method and class level,
    /// lambda and state machine classes) are folded: covered when any of them has hits.</summary>
    public required IReadOnlyDictionary<string, Dictionary<int, bool>> Files { get; init; }
}

/// <summary>A coverage input skarp cannot use (not Cobertura, unreadable): the message names the file and the reason.</summary>
public sealed class CoverageFormatException(string message) : Exception(message);

/// <summary>
/// Forward-only Cobertura reader: <see cref="XmlReader"/> without DTD processing, resolver or DOM, so it
/// is Native AOT safe and its memory follows the number of distinct lines, not the file size. Class, method and
/// condition data are ignored; only (file, line, hits &gt; 0) is kept.
/// </summary>
public static class CoberturaReader
{
    public static CoberturaReport Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Read(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CoverageFormatException($"{path}: cannot read: {ex.Message}");
        }
    }

    public static CoberturaReport Read(Stream stream, string path)
    {
        Span<byte> head = stackalloc byte[64];
        int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..n];
        bool bom = head is [0xEF, 0xBB, 0xBF, ..];
        if (Sniff(bom ? head[3..] : head) is { } refusal) throw new CoverageFormatException($"{path}: {refusal}");
        stream.Position = 0;

        var state = new ParseState(path);
        try
        {
            using var x = XmlReader.Create(stream, ReaderSettings);
            while (x.Read()) state.Visit(x);
        }
        catch (XmlException ex)
        {
            throw new CoverageFormatException($"{path}: not a readable Cobertura report ({ex.Message})");
        }
        if (!state.SawRoot) throw new CoverageFormatException($"{path}: not a Cobertura report (empty document)");
        var producer = state.Version == "0" ? "reportgenerator"
            : state.HasSources ? "coverlet"
            : bom || state.HasComplexity ? "microsoft"
            : "unknown";
        return new CoberturaReport { Path = path, Timestamp = state.Timestamp, Producer = producer, Sources = state.Sources, Files = state.Files };
    }

    static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true,
        IgnoreProcessingInstructions = true, CheckCharacters = false,
    };

    /// <summary>The reader's state while walking the document once.</summary>
    sealed class ParseState(string path)
    {
        public readonly Dictionary<string, Dictionary<int, bool>> Files = new(StringComparer.Ordinal);
        public readonly List<string> Sources = [];
        public bool HasSources, SawRoot, HasComplexity;
        public long Timestamp;
        public string Version = "";
        bool inSource;
        Dictionary<int, bool>? current;

        public void Visit(XmlReader x)
        {
            switch (x.NodeType)
            {
                case XmlNodeType.Element when !SawRoot: Root(x); break;
                case XmlNodeType.Element: Element(x); break;
                case XmlNodeType.Text or XmlNodeType.CDATA when inSource:
                    Sources.Add(x.Value.Trim());
                    inSource = false;
                    break;
                case XmlNodeType.EndElement when x.LocalName == "class": current = null; break;
                case XmlNodeType.EndElement when x.LocalName == "source" && inSource:
                    Sources.Add("");
                    inSource = false;
                    break;
            }
        }

        void Root(XmlReader x)
        {
            SawRoot = true;
            if (x.LocalName != "coverage")
                throw new CoverageFormatException($"{path}: not a Cobertura report (root element <{x.LocalName}>; {Hint(x.LocalName)})");
            long.TryParse(x.GetAttribute("timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out Timestamp);
            Version = x.GetAttribute("version") ?? "";
            HasComplexity = x.GetAttribute("complexity") is not null;
        }

        void Element(XmlReader x)
        {
            switch (x.LocalName)
            {
                case "sources": HasSources = true; break;
                case "source":
                    if (x.IsEmptyElement) Sources.Add("");
                    else inSource = true;
                    break;
                case "class":
                    current = x.IsEmptyElement || x.GetAttribute("filename") is not { Length: > 0 } name ? null
                        : Files.TryGetValue(name, out var f) ? f : Files[name] = [];
                    break;
                case "line" when current is not null:
                    if (!int.TryParse(x.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) || line < 1) break;
                    current[line] = Hits(x.GetAttribute("hits")) || current.GetValueOrDefault(line);
                    break;
            }
        }
    }

    /// <summary>A reason to refuse the input from its first bytes, before XML parsing; null when it may be XML.</summary>
    static string? Sniff(ReadOnlySpan<byte> head)
    {
        int i = 0;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        if (i == head.Length) return head.Length == 0 ? "not a Cobertura report (empty file)" : null;
        if (head[i] == (byte)'<') return null;
        var text = System.Text.Encoding.ASCII.GetString(head[i..]);
        if (text.StartsWith("TN:", StringComparison.Ordinal) || text.StartsWith("SF:", StringComparison.Ordinal))
            return "an lcov report, not Cobertura; skarp reads Cobertura XML only (Coverlet: --format cobertura; Microsoft code coverage: --coverage-output-format cobertura)";
        if (text.StartsWith('{'))
            return "a JSON file, not Cobertura (Coverlet's default coverage.json?); skarp reads Cobertura XML only (Coverlet: --format cobertura)";
        return "not a Cobertura report (not XML; a binary .coverage file? Microsoft code coverage writes Cobertura with --coverage-output-format cobertura)";
    }

    static string Hint(string root) => root switch
    {
        "CoverageSession" => "an OpenCover report; skarp reads Cobertura XML only",
        "results" or "CoverageDSPriv" => "a Microsoft XML coverage report; use --coverage-output-format cobertura",
        _ => "skarp reads Cobertura XML only (Coverlet or Microsoft code coverage)",
    };

    /// <summary>True when the hits attribute is above zero (whole numbers, very large numbers and decimals are accepted).</summary>
    static bool Hits(string? s)
    {
        if (s is null) return false;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v > 0;
        if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d > 0;
        // A digit string too long for a long is a very large count.
        return s.Length > 0 && s.All(char.IsAsciiDigit) && s.Any(c => c != '0');
    }
}
