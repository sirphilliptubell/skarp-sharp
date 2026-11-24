using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.Model;

namespace SkarpSharp.Tests;

/// <summary>
/// Every worked example of the file and member metrics, as data (WorkedExamples/&lt;entry id&gt;.json). One test case per expectation: "&lt;entry&gt;/example-&lt;n&gt;".
/// </summary>
public class WorkedExampleTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "WorkedExamples");

    public static IEnumerable<(string, string, string, string)> Cases()
    {
        var data = new List<(string, string, string, string)>();
        foreach (var file in Directory.GetFiles(Dir, "*.json").Order(StringComparer.Ordinal))
        {
            var entry = Path.GetFileNameWithoutExtension(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var ex in doc.RootElement.EnumerateArray())
            {
                var example = ex.GetProperty("example").GetString()!;
                foreach (var e in ex.GetProperty("expect").EnumerateArray())
                    data.Add((entry, example, e.GetProperty("member").GetString()!, e.GetProperty("metric").GetString()!));
            }
        }
        return data;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Example(string entry, string example, string member, string metric)
    {
        var (code, expected, notes) = Load(entry, example, member, metric);
        var file = Analyzer.AnalyzeSource(code);
        long actual = member == "<file>" ? FileValue(file.Lines, metric) : MemberValue(Find(file, member), metric);
        await Assert.That(expected == actual).IsTrue().Because($"{entry}/example-{example} {member} {metric}: expected {expected}, actual {actual}. Notes: {notes}");
    }

    // Returns a value, so it checks synchronously (Assert.Fail throws) and its callers stay unchanged.
    static MemberResult Find(FileResult file, string name)
    {
        // By simple name; a name such as ".ctor(int?,bool)" selects by id suffix (overloads share a simple name).
        var matches = file.Members.Where(m => m.Name == name).ToList();
        if (matches.Count == 0) matches = file.Members.Where(m => m.Id.EndsWith("::" + name, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) Assert.Fail($"member '{name}' found {matches.Count} times; members: {string.Join(", ", file.Members.Select(m => m.Name))}");
        return matches[0];
    }

    static long MemberValue(MemberResult m, string metric) => metric switch
    {
        "cyclomatic" => m.Cyclomatic,
        "cyclomaticMicrosoft" => m.CyclomaticMicrosoft,
        "cognitive" => m.Cognitive,
        "npath" => m.Npath,
        "nesting" => m.Nesting,
        "statements" => m.Statements,
        _ => FileValue(m.Lines, metric),
    };

    static long FileValue(LineCountsDto l, string metric) => metric switch
    {
        "physicalLines" => l.Physical,
        "blankLines" => l.Blank,
        "commentLines" => l.Comment,
        "mixedLines" => l.Mixed,
        "sourceLines" => l.Source,
        "directiveLines" => l.Directive,
        "inactiveLines" => l.Inactive,
        _ => throw new ArgumentException($"unknown metric {metric}"),
    };

    static (string code, long expected, string notes) Load(string entry, string example, string member, string metric)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, entry + ".json")));
        foreach (var ex in doc.RootElement.EnumerateArray())
        {
            if (ex.GetProperty("example").GetString() != example) continue;
            foreach (var e in ex.GetProperty("expect").EnumerateArray())
                if (e.GetProperty("member").GetString() == member && e.GetProperty("metric").GetString() == metric)
                    return (ex.GetProperty("code").GetString()!, e.GetProperty("value").GetInt64(),
                        ex.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "");
        }
        throw new InvalidOperationException("case not found");
    }
}