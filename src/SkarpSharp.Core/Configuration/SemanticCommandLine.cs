namespace SkarpSharp.Configuration;

/// <summary>
/// The semantic tier's settings (<c>semantic.*</c> in <c>.skarp.json</c>) and flags (<c>--no-semantic</c>,
/// <c>--restore</c>, <c>--solution</c>, <c>--no-generators</c>, <c>--all-targets</c>, <c>--target</c>,
/// <c>--semantic-output</c>), applied with the usual precedence (default &lt; file &lt; flag).
/// </summary>
public static class SemanticCommandLine
{
    internal static void ApplyFile(SkarpOptions o, SettingsFile f, string path)
    {
        if (f.UnusedCode?.Allow is { } allow)
        {
            if (allow.Any(string.IsNullOrWhiteSpace)) throw new SettingsException($"{path}: unusedCode.allow: empty pattern");
            o._unusedAllow.Clear();
            o._unusedAllow.AddRange(allow.Select(a => a.Trim()));
        }
        ApplyNamespaceCycles(o, f);
        if (f.Semantic is not { } s) return;
        if (s.Enabled is bool en) { o.Semantic = en; }
        if (s.Generators is bool gen) { o.Generators = gen; }
        if (s.Solution is { } sln)
        {
            if (sln.Length == 0) throw new SettingsException($"{path}: semantic.solution: empty path");
            o.Solution = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, sln));
           
        }
        if (s.Targets is { } targets)
        {
            if (ValidateTargets(targets) is { } error) throw new SettingsException($"{path}: semantic.targets: {error}");
            o._targets.Clear();
            o._targets.AddRange(targets);
           
        }
    }

    /// <summary><c>namespaceCycles.includeGeneratedTargets</c>.</summary>
    static void ApplyNamespaceCycles(SkarpOptions o, SettingsFile f)
    {
        if (f.NamespaceCycles?.IncludeGeneratedTargets is not bool keep) return;
        o.IncludeGeneratedTargets = keep;
    }

    static string? ValidateTargets(IReadOnlyList<string> targets)
    {
        if (targets.Any(string.IsNullOrWhiteSpace)) return "empty target framework";
        if (targets.Count > 1 && targets.Any(t => t is "all" or "highest")) return "\"all\" and \"highest\" stand alone";
        return null;
    }

    /// <summary>Applies one semantic flag; returns an error message, or null (also for flags that are not semantic).</summary>
    internal static string? ApplyFlag(SkarpOptions o, string name, string? v)
    {
        const SettingOrigin Fl = SettingOrigin.CommandLine;
        switch (name)
        {
            case "--semantic": o.Semantic = true; o.Mark("semantic.enabled", Fl); break;
            case "--no-semantic": o.Semantic = false; o.Mark("semantic.enabled", Fl); break;
            case "--restore": o.Restore = true; break;
            case "--generators": o.Generators = true; o.Mark("semantic.generators", Fl); break;
            case "--no-generators": o.Generators = false; o.Mark("semantic.generators", Fl); break;
            case "--solution":
                if (string.IsNullOrEmpty(v)) return $"Invalid value for {name}: '{v}' (expected a .sln or .slnx file)";
                o.Solution = Path.GetFullPath(v);
                o.Mark("semantic.solution", Fl);
                break;
            case "--all-targets":
                o._targets.Clear();
                o._targets.Add("all");
                o.Mark("semantic.targets", Fl);
                break;
            case "--target":
                if (string.IsNullOrWhiteSpace(v)) return $"Invalid value for {name}: '{v}' (expected a target framework such as net10.0)";
                // The flags replace the file's list (as --gate-on does).
                if (o.OriginOf("semantic.targets") != Fl || o._targets.Contains("all")) o._targets.Clear();
                o._targets.Add(v);
                o.Mark("semantic.targets", Fl);
                break;
            case "--include-generated-targets": o.IncludeGeneratedTargets = true; o.Mark("namespaceCycles.includeGeneratedTargets", Fl); break;
            case "--semantic-output":
                if (string.IsNullOrEmpty(v)) return $"Invalid value for {name}: '{v}' (expected a file)";
                o.SemanticOutput = v;
                break;
        }
        return null;
    }

    /// <summary>Checks the combination after all flags: <c>--restore</c> implies the semantic tier, so <c>--restore
    /// --no-semantic</c> is a usage error; the target list must be valid.</summary>
    internal static string? Check(SkarpOptions o, IReadOnlyList<string> flagNames)
    {
        if (o.Restore)
        {
            if (flagNames.Contains("--no-semantic")) return "--restore runs dotnet restore for the semantic tier; it cannot be combined with --no-semantic";
            o.Semantic = true;
        }
        if (ValidateTargets(o.Targets) is { } error) return $"--target: {error}";
        if (flagNames.Contains("--all-targets") && flagNames.Contains("--target")) return "--all-targets and --target cannot be combined";
        return null;
    }
}
