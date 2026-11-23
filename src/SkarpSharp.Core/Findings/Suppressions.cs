using SkarpSharp.Model;

namespace SkarpSharp.Findings;

/// <summary>
/// The suppressions in effect for a run: per file, line spans per rule. Member and type attributes
/// cover their declaration; assembly-level attributes with a <c>Target</c> (<c>~T:</c>, <c>~M:</c>, <c>~P:</c>, <c>~F:</c>,
/// <c>~E:</c>, <c>~N:</c> or a plain name) are resolved against the members and types of every analyzed file. A
/// suppression without a justification does not count.
/// </summary>
public sealed class SuppressionIndex
{
    readonly Dictionary<string, List<(string Rule, int Start, int End, SuppressionAttribute By, string ByPath)>> _spans = new(StringComparer.Ordinal);

    /// <summary>Suppressions without a justification (path of the file holding the attribute).</summary>
    public List<(string Path, SuppressionAttribute Attribute)> Unjustified { get; } = [];

    public bool IsEmpty => _spans.Count == 0;

    public static SuppressionIndex Build(IEnumerable<FileResult> files)
    {
        var index = new SuppressionIndex();
        var list = files.ToList();
        var assembly = new List<(string Path, SuppressionAttribute A)>();
        foreach (var f in list)
            foreach (var a in f.Suppressions ?? [])
            {
                if (!a.Justified) { index.Unjustified.Add((f.Path, a)); continue; }
                if (a.Assembly) assembly.Add((f.Path, a));
                else index.Add(f.Path, a.Rule, a.StartLine, a.EndLine, a, f.Path);
            }
        foreach (var (path, a) in assembly)
            foreach (var f in list)
                foreach (var (start, end) in TargetSpans.Resolve(a.Target, a.Scope, f))
                    index.Add(f.Path, a.Rule, start, end, a, path);
        return index;
    }

    void Add(string path, string rule, int start, int end, SuppressionAttribute by, string byPath)
    {
        if (!_spans.TryGetValue(path, out var list)) _spans[path] = list = [];
        list.Add((rule, start, end, by, byPath));
    }

    /// <summary>The suppression covering line <paramref name="line"/> of a file for a rule, with the path of the file
    /// holding it; null when none does.</summary>
    public (SuppressionAttribute By, string ByPath)? Find(string rule, string path, int line)
    {
        if (!_spans.TryGetValue(path, out var list)) return null;
        foreach (var s in list)
            if (s.Rule == rule && s.Start <= line && line <= s.End) return (s.By, s.ByPath);
        return null;
    }

    /// <summary>
    /// A clone copy (lines <paramref name="start"/> to <paramref name="end"/>) is in suppressed code when more than half of
    /// its lines are covered by suppressions of the rule (a copy may include a type's header or reach into a neighbouring
    /// member); returns the first suppression that covers a line of it, else null.
    /// </summary>
    public (SuppressionAttribute By, string ByPath)? FindCopy(string rule, string path, int start, int end)
    {
        if (!_spans.TryGetValue(path, out var list)) return null;
        (SuppressionAttribute, string)? first = null;
        int covered = 0;
        for (int line = start; line <= end; line++)
        {
            var hit = list.FirstOrDefault(s => s.Rule == rule && s.Start <= line && line <= s.End);
            if (hit.By is null) continue;
            covered++;
            first ??= (hit.By, hit.ByPath);
        }
        return covered * 2 > end - start + 1 ? first : null;
    }

    /// <summary>The suppression of a finding (its line, or every copy of a clone group), or null.</summary>
    public (SuppressionAttribute By, string ByPath)? Find(Finding f)
    {
        if (f.Clone is { } c)
        {
            var copies = c.Instances.Select(i => FindCopy(f.Rule, i.Path, i.StartLine, i.EndLine)).ToList();
            return copies.All(x => x is not null) ? copies[0] : null;
        }
        return f.Path.Length == 0 || f.PathlessId ? null : Find(f.Rule, f.Path, f.Line);
    }

    /// <summary>Splits findings into kept and suppressed (the suppressed with their justification).</summary>
    public (List<Finding> Kept, List<SuppressedFinding> Suppressed) Split(IEnumerable<Finding> findings)
    {
        var kept = new List<Finding>();
        var suppressed = new List<SuppressedFinding>();
        foreach (var f in findings)
        {
            if (IsEmpty || Find(f) is not { } s) { kept.Add(f); continue; }
            suppressed.Add(Suppressed(f, s.By, s.ByPath));
        }
        return (kept, suppressed);
    }

    public static SuppressedFinding Suppressed(Finding f, SuppressionAttribute by, string byPath) => new()
    {
        Id = f.Id, Rule = f.Rule, Severity = f.Severity, Path = f.Path, Line = f.Line, Message = f.Message,
        Justification = by.Justification ?? "", SuppressedBy = $"{byPath}:{by.Line}",
    };
}

/// <summary>Resolves an assembly-level <c>Target</c> against one file's types and members (by full name, generic arity
/// and parameters ignored).</summary>
static class TargetSpans
{
    public static IEnumerable<(int Start, int End)> Resolve(string? target, string? scope, FileResult f)
    {
        if (string.IsNullOrWhiteSpace(target)) return [];
        var (kind, name) = Parse(target);
        if (kind == 'N' || scope is "namespace" or "namespaceanddescendants")
            return f.Types.Where(t => InNamespace(Normalize(t.Name), name, scope == "namespace")).Select(t => (t.StartLine, t.EndLine));
        var types = kind is 'T' or ' ' ? f.Types.Where(t => Normalize(t.Name) == name).Select(t => (t.StartLine, t.EndLine)) : [];
        return kind == 'T' ? types : types.Concat(Members(name, f));
    }

    /// <summary>The members named by <c>Namespace.Type.Member</c> (every overload; accessors of a property).</summary>
    static IEnumerable<(int Start, int End)> Members(string name, FileResult f)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0) yield break;
        var (type, member) = (name[..dot], name[(dot + 1)..]);
        foreach (var m in f.Members)
        {
            int sep = m.Key.IndexOf("::", StringComparison.Ordinal);
            if (m.Parent is null && sep > 0 && Normalize(m.Key[..sep]) == type && MemberName(m.Key[(sep + 2)..]) == member)
                yield return (m.StartLine, m.EndLine);
        }
    }

    static (char Kind, string Name) Parse(string target)
    {
        var t = target.Trim().TrimStart('~');
        char kind = ' ';
        if (t.Length > 2 && t[1] == ':') { kind = char.ToUpperInvariant(t[0]); t = t[2..]; }
        int paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren];
        t = t.Replace(".#ctor", "..ctor", StringComparison.Ordinal).Replace(".#cctor", "..cctor", StringComparison.Ordinal);
        return (kind, StripArity(t));
    }

    /// <summary><c>Ns.Outer+Inner`1</c> becomes <c>Ns.Outer.Inner</c>.</summary>
    static string Normalize(string typeName) => StripArity(typeName.Replace('+', '.'));

    static string StripArity(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '`') { sb.Append(s[i]); continue; }
            while (i + 1 < s.Length && (s[i + 1] == '`' || char.IsAsciiDigit(s[i + 1]))) i++;
        }
        return sb.ToString();
    }

    static bool InNamespace(string fullType, string ns, bool exact)
    {
        if (!fullType.StartsWith(ns + ".", StringComparison.Ordinal)) return false;
        return !exact || fullType.IndexOf('.', ns.Length + 1) < 0;
    }

    /// <summary>The member name of a key's member part (<c>Run#2/lambda1</c> gives <c>Run</c>, <c>Name.get</c> gives <c>Name</c>).</summary>
    static string MemberName(string rest)
    {
        int cut = rest.IndexOfAny(['#', '/', '(']);
        var name = cut < 0 ? rest : rest[..cut];
        if (name is ".ctor" or ".cctor") return name;
        int dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
