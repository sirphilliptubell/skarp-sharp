namespace SkarpSharp.Findings;

/// <summary>The long explanations of the duplicate-code rules (<c>skarp explain</c>).</summary>
public static class DuplicationText
{
    /// <summary>The note in the Claude hook, which never names a way around a finding.</summary>
    public const string ClaudeHint =
        "duplicate code: refactor real copies into one shared member; if the copies are deliberate, tell the user why they should stay";

    /// <summary>The one-line guidance the text output adds once when it reports a duplicate-code or duplicate-code-near
    /// Finding, also in <c>skarp explain</c> and the README.</summary>
    public const string AgentHint =
        "duplicate code: refactor real copies into one shared member; for deliberate duplication add [SuppressMessage(\"skarp\", \"duplicate-code\", Justification = \"<why>\")] to the member";

    public const string Suppress = """
        Deliberate duplication: refactor real copies into one shared member. When the duplication is deliberate (generated
        shapes, per-type overloads an API repeats on purpose, test data), mark it on the member or type that holds a copy:
            [SuppressMessage("skarp", "duplicate-code", Justification = "<why>")]
        (System.Diagnostics.CodeAnalysis; UnconditionalSuppressMessage works too). A group is left out when every copy is
        in suppressed code; with some copies suppressed, the others still form a group when two or more remain. A
        suppression without a justification does not count. Suppressed findings are counted in the run summary and listed
        in the JSON (suppressed).
        """;

    public const string TokenRule = """
        What it measures: copied code. The Roslyn tokens of every file (comments, whitespace and inactive #if text are
        not tokens; using directives, attribute lists and namespace declarations are left out) are compared with
        identifiers renamed consistently (Baker's parameterized matching: a = b + c matches x = y + z, but not x = x + x)
        and literals reduced to their kind, so exact copies (kind exact) and copies with renamed names or changed values
        (kind renamed) are found. Copies are trimmed to whole statements and to whole members at their ends; a group
        whose copies lie mostly outside method, accessor, constructor and operator bodies (runs of property or field
        declarations) is dropped. Low-content groups are skipped too: copies that average fewer than 5 tokens per line
        and where fewer than 40% of the lines differ in shape (brace-only lines not counted), such as constructors that
        only store injected services (_a = a;), lookup or switch tables and migration builder chains; set
        --threshold duplicate-code.lowContent=report on skarp analyze or baseline (or thresholds.duplicate-code.lowContent
        in .skarp.json) to report them. One finding per clone group (two or more copies; a file may hold several), pointing at
        the ordinal-first copy; the JSON clone block lists every copy. Default minimum: 100 tokens and 10 lines per copy
        (SonarQube's non-Java rule, the CPD documentation example); preset jscpd: 50 tokens and 5 lines. A warning when a
        copy has 50 or more lines, or when 3 or more copies have copyLines (20) or more lines. The value is the lines of
        all copies together, so a group that gains a copy or grows is worsened against the baseline. Test files follow
        the test setting; clones between test and production code are never reported; generated files are skipped. Clones
        are matched on the base view only (the parse with no symbols): code under #if branches it leaves inactive is not
        seen, even where other rules analyze it in a symbol variant. skarp check leaves the rule out (one
        file cannot decide it); analyze, the Stop hook and the reports report it.

        Why it matters: every copy must be found and changed when the logic changes; copies drift apart and a fix lands in
        one of them only. Duplicated code is also extra code to read, review and test.

        How to improve: extract the shared part into one method (with parameters for what differs), a base class, a
        generic method or a helper type, and call it from each place.

        """ + Suppress;

    public const string NearRule = """
        What it measures (on by default, notice only, score weight 0; --rule duplicate-code-near=off on skarp analyze or
        baseline, or "rules": { "duplicate-code-near": false } in .skarp.json, switches it off): whole methods,
        constructors, operators and accessors that are copies with small edits (Type 3: statements added, removed or
        changed). Each method's syntax tree becomes a multiset of normalized subtree fingerprints (local names become one
        marker; called and accessed member names, type names and keywords stay); two methods are similar when the
        multiset Jaccard similarity is at least 0.75 and both have at least 10 lines (Skarp's own choice, which keeps boilerplate matches down). Methods that only forward a call are skipped; a pair that a duplicate-code group already
        covers is not reported again. Similar pairs are merged into groups; one finding per group, notice level only,
        with each pair's similarity in the JSON clone block (kind near-miss). On by default because another code health
        tool ships it on for C# (CodeScene's function-level Code Duplication: 10 lines, 75% similar); weight 0 in the
        code health score because many of the groups are boilerplate of one shape, so it informs without lowering file scores, and as a notice
        it never enters the default gate.

        Why it matters: near copies are the copies that already drifted apart; a fix in one often belongs in the others.

        How to improve: compare the methods, extract the common steps, and pass in what differs (a parameter, a delegate,
        a strategy).

        """ + Suppress;

    public const string Suppression = """
        What it measures: a [SuppressMessage("skarp", "<rule>")] or UnconditionalSuppressMessage attribute (category
        skarp or skarp.<anything>) without a Justification, or with an empty one. Such a suppression does not count: the
        findings it would hide are reported. The finding points at the attribute. Notice level only; it does not lower
        the health score.

        Why it matters: a suppression hides a finding from the reports, the gate and the score. The reason belongs next to
        it, so the next reader can judge whether it still holds.

        How to improve: add Justification = "<why>" to the attribute, or remove the attribute and deal with the finding.

        Inline suppression works for every rule: on a member (its findings) or a type (the type's own findings and those
        of its members), or at assembly level with a Target (~T:Namespace.Type, ~M:Namespace.Type.Method, a namespace
        with Scope "namespace"). skarp check sees only the edited file, so an assembly-level suppression in another file
        (GlobalSuppressions.cs) is not applied there. File-level findings (large-file, active-complex-file) cannot be
        suppressed inline; use --exclude on skarp analyze or baseline, or "exclude" in .skarp.json.
        """;
}
