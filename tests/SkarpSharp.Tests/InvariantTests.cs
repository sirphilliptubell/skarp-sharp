using System.Text.Json;
using SkarpSharp.Analysis;
using SkarpSharp.Model;

namespace SkarpSharp.Tests;

/// <summary>Invariants of the analysis output: determinism, member ids and line counts.</summary>
public class InvariantTests
{
    const string Sample = """
        using System;
        namespace N;
        partial class A<T>
        {
            int f = 1, g;
            Func<int, int> h = x => x > 0 ? x : -x;
            public int P => f > 0 ? f : g;
            int Q { get { return f; } set { if (value > 0) f = value; } }
            public A() { }
            void M(int x)
            {
                int Local(int y) { return y > 0 ? 1 : 0; }
                for (int i = 0; i < x; i++) { if (i % 2 == 0 && i > 3 || i == 1) Local(i); }
                Action a = () => { while (x > 0) x--; };
            }
        }
        """;

    [Test]
    public async Task Output_does_not_depend_on_parallelism()
    {
        var dir = Directory.CreateTempSubdirectory("skarp-inv-");
        try
        {
            for (int i = 0; i < 40; i++)
                File.WriteAllText(Path.Combine(dir.FullName, $"F{i:00}.cs"), Sample.Replace("class A", $"class A{i}"));
            string Run(int p)
            {
                var r = Analyzer.AnalyzeDirectory(dir.FullName, new AnalyzerOptions { Parallelism = p });
                r.Run = new RunInfo(); // timing and environment differ by design
                return JsonSerializer.Serialize(r, ResultJsonContext.Default.AnalysisResult);
            }
            var one = Run(1);
            await Assert.That(Run(8)).IsEqualTo(one);
            await Assert.That(Run(3)).IsEqualTo(one);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    public async Task Every_member_has_cyclomatic_at_least_one_and_unique_ids()
    {
        var file = Analyzer.AnalyzeSource(Sample);
        await Assert.That(file.Members).All(m => m.Cyclomatic >= 1 && m.CyclomaticMicrosoft >= 1 && m.Npath >= 1);
        await Assert.That(file.Members.Select(m => m.Id).Distinct().Count()).IsEqualTo(file.Members.Count);
        // The two nested bodies of M and the field lambda are their own records with parents.
        await Assert.That(file.Members).Contains(m => m.Name == "M/Local" && m.Parent is not null);
        await Assert.That(file.Members).Contains(m => m.Name == "M/lambda1" && m.Parent is not null);
        await Assert.That(file.Members).Contains(m => m.Kind == "Lambda" && m.Name.StartsWith("h/", StringComparison.Ordinal));
    }

    [Test]
    public async Task Line_invariant_holds()
    {
        var f = Analyzer.AnalyzeSource(Sample + "\n#if false\nclass Dead { }\n#endif\n/* c */\n");
        var l = f.Lines;
        // physical = blank + comment-only + source + inactive; comment-only = comment - mixed.
        await Assert.That(l.Blank + (l.Comment - l.Mixed) + l.Source + l.Inactive).IsEqualTo(l.Physical);
        await Assert.That(l.Inactive).IsEqualTo(1);
    }
}