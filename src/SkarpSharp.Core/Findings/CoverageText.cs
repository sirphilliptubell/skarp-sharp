namespace SkarpSharp.Findings;

/// <summary>The <c>skarp explain</c> text of the coverage rule.</summary>
public static class CoverageText
{
    public const string Rule = """
        What it measures: members (methods, constructors, accessors, operators; their lambdas, local functions, async and
        iterator bodies folded in) with cyclomatic complexity 6 or more, as skarp counts it, that have at least one
        coverable line in the coverage reports and not one covered line. skarp never runs tests: it reads the Cobertura
        reports you produce (--coverage <file> on skarp analyze or baseline, repeatable, or "coverage": ["glob", ...] in .skarp.json; Coverlet or
        Microsoft code coverage) and merges them per line (a line is covered when any report covers it). The message
        names both numbers ("cyclomatic complexity 12, 0 of 9 coverable lines covered"). Never reported: members whose
        span holds code inactive in every analyzed variant (#if branches none of the planned symbol sets turns on), members and
        types marked [ExcludeFromCodeCoverage], generated code and test projects. On whenever a report is given; without
        one the rule is not evaluated, and a baseline keeps its recorded findings of this rule as they are (a run without
        a report never shows them as fixed; nor does a file the reports say nothing about). Notice level, score weight 0,
        not in the default gate (--gate-on untested-complex-method:notice on skarp analyze gates it); left out of skarp check, because the
        report is stale while code is being edited. Threshold: --threshold untested-complex-method.notice=<n> on skarp analyze or baseline, or
        thresholds.untested-complex-method.notice in .skarp.json.

        Why it matters: a member with many decision points and no test that runs it is where a change is most likely to
        break something unnoticed. The cut at 6 is CRAP's zero-coverage cut (CRAP = cc^2 x (1 - coverage)^3 + cc; over 30
        at 0% coverage from cc 6 on), but CRAP itself is not computed: it has no published validation, and on well-tested
        code its value is ruled by complexity alone. Coverage is a weak signal of test quality (Inozemtseva and Holmes,
        ICSE 2014: once suite size is controlled, coverage correlates only low to moderately with how many faults a suite
        finds); here it is used only for what it shows reliably: code no test runs at all. A finding says "no test ran
        this", never "poorly tested". Most findings are code that really is not run by the suites; some is build-time generator code checked through its output, dead code, or platform-specific code, where a test is not wanted.

        How to improve: add a test that runs the member's main paths before changing it, or reduce its complexity
        (extract the branches into smaller members that are easier to test). Dead code: delete it. Code that only runs
        under another target framework: collect one report per target framework and pass them all (the reports are
        merged). Deliberately untested code: [SuppressMessage("skarp", "untested-complex-method", Justification = "<why>")].
        """;
}
