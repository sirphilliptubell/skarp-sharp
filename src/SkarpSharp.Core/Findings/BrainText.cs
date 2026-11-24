namespace SkarpSharp.Findings;

/// <summary>The long explanations of the composite rules (<c>skarp explain brain-method</c>, <c>brain-class</c>).</summary>
public static class BrainText
{
    const string Composite = """
        How it is reported: one line in place of its parts (the text format, Markdown, GitHub annotations and
        skarp check), listing them; --all on skarp analyze and skarp show <handle> list the parts one by one. The JSON keeps every part
        finding in findings and adds the composite to the composites list (rule, place, severity, status, part ids); SARIF
        reports it with its parts as related locations, next to the parts' own results. The composite has no threshold,
        score weight or gate entry of its own: the baseline records the parts, the score charges the parts once, and the
        gate judges the parts. Its severity is the highest of its parts; against the baseline it is known when every part
        is known, new when every part is new, and worsened otherwise.

        Suppression: [SuppressMessage("skarp", "<this rule>", Justification = "<why>")] on the member or type hides the
        composite and all its parts there. Suppressing one part hides that part only; the composite stays when the
        remaining findings still meet its definition.

        <default state>
        """;

    /// <summary>On by default, because a comparable product ships it on for C#.</summary>
    const string MethodDefault = """
        On by default (CodeScene's Brain Method is on in its C# rules, NDepend's "Avoid methods too big, too complex" is
        close). It only relabels findings that are reported anyway, so scores, gate and baseline do not change. Switch it off with --rule
        brain-method=off on skarp analyze or baseline, or "rules": { "brain-method": false } in .skarp.json.
        """;

    /// <summary>Stays off; no comparable product ships it on for C#.</summary>
    const string ClassDefault = """
        Experimental, off by default (many of its findings are not worth a reviewer's time; no other code health tool ships a Brain Class rule on by default for C#): switch it on with --rule brain-class=on on skarp analyze or baseline, or "rules": {
        "brain-class": true } in .skarp.json. Static classes are never brain classes (they are rarely a design problem of this kind).
        """;

    public static readonly string Method = """
        What it measures: a member that has all of the long-method, complex-method and deep-nesting findings at once
        (each at its own threshold in effect: by default over 50 physical lines, cognitive complexity over 15, nesting
        depth over 3). It finds nothing the parts do not find: it groups them under one name. The part set is the rule
        parameter parts (comma-separated member rules: long-method, complex-method, deep-nesting, bumpy-road,
        complex-condition, too-many-parameters, path-explosion) and minParts (how many of them must fire; default all).
        Lanza and Marinescu's Brain Method (2006) adds a fourth leg, many accessed variables; skarp leaves it out at the
        syntax tier, as CodeScene's Brain Method (size, complexity, nesting) does.

        Why it matters: a method that is long, branchy and deeply nested at once tends to hold the logic of its class in
        one place ("centralizes the functionality", Lanza and Marinescu): it is the hardest kind of method to read, test
        and change safely, and it attracts more change.

        How to improve: split it. Extract the commented or blank-line separated steps into methods with names that say
        what they do (Extract Method); flatten the nesting with guard clauses, early returns and continue; move each
        branch of a large if/switch into its own method or a lookup table; pull loop bodies out into methods. Re-run
        skarp: each part that drops below its threshold leaves the composite, and it disappears when a part is gone.

        """ + Composite.Replace("<this rule>", RuleIds.BrainMethod, StringComparison.Ordinal).Replace("<default state>", MethodDefault, StringComparison.Ordinal);

    public static readonly string Class = """
        What it measures: a class, struct or record that has a large-type finding, at least one brain method (see skarp
        explain brain-method; brain methods count here even when the brain-method rule itself is off) and at least
        minMethods (10) methods (constructors not counted): a small type with one big method is not a brain class, the
        brain method says everything. Its parts are the large-type finding and each
        brain method's parts; partial declarations are merged as large-type merges them. The rule parameter preset
        lanza-marinescu-2006 adds the book's size legs (Lanza and Marinescu 2006, Java values):
        two or more brain methods and 195 or more source lines, or one brain method with 390 or more source lines and
        WMC 94 or more; and WMC 47 or more in either case (the book's cohesion leg needs the semantic tier and is left
        out). Interfaces and static classes are not judged.

        Why it matters: a brain class concentrates complex logic: it is large and some of its methods are brain methods.
        It is where changes collide and where a reader needs the most context. Unlike god-class (semantic tier) it does
        not ask whether the class uses other classes' data; the two often meet on the same type.

        How to improve: start with its brain methods (skarp explain brain-method). Then look for groups of members that
        work on the same fields and move each group into its own class (Extract Class), and for overloads or variants
        that repeat the same core (extract the shared part). A matured utility class that has caused no maintenance
        trouble may be fine as it is: suppress the composite with a justification.

        """ + Composite.Replace("<this rule>", RuleIds.BrainClass, StringComparison.Ordinal).Replace("<default state>", ClassDefault, StringComparison.Ordinal);
}
