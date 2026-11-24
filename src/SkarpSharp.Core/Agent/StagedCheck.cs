using SkarpSharp.Baselines;
using SkarpSharp.Configuration;

namespace SkarpSharp.Agent;

/// <summary>What <c>skarp check --staged</c> found.</summary>
public sealed class StagedOutcome
{
    /// <summary>The lines, as for a plain check against the baseline.</summary>
    public CheckOutcome Check { get; } = new() { EndOfTurn = true };
    /// <summary>No staged C# file (a reword, a commit of other files): nothing to say.</summary>
    public bool NoFiles { get; set; }
    /// <summary>The baselines (relative to the work tree root) not in the index: their roots were not checked.</summary>
    public List<string> MissingBaselines { get; } = [];
    /// <summary>The baselines the commit changes (staged differs from <c>HEAD</c>).</summary>
    public List<string> ChangedBaselines { get; } = [];
    /// <summary>The number of staged C# files judged.</summary>
    public int Checked { get; set; }
}

/// <summary>
/// <c>skarp check --staged</c>, the syntax findings of the staged C# files compared
/// with the staged baseline, with the staged <c>.skarp.json</c> as the repository settings. Every input comes from the
/// index through <see cref="StagedIndex"/>; the root of a file is found as for a plain check (the nearest
/// <c>.skarp.json</c> on disk, else the work tree root). Blobs are judged by their own text, so line numbers match the
/// working file while byte offsets may not (line endings as stored). Nothing is saved.
/// </summary>
public static class StagedCheck
{
    /// <exception cref="CheckUsageException">Not in a git work tree, or a staged settings file is invalid.</exception>
    /// <exception cref="InvalidOperationException">git failed.</exception>
    /// <exception cref="InvalidDataException">The staged baseline is not a baseline file.</exception>
    public static StagedOutcome Run(string cwd, CheckSettings s, IReadOnlyDictionary<string, string?>? environment)
    {
        var top = Paths.WorkTreeRoot(cwd) ?? throw new CheckUsageException("skarp check --staged: not in a git work tree");
        using var index = StagedIndex.Open(top, environment);
        var walk = new Walk(index, top, s);
        foreach (var p in index.Paths) if (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) walk.Add(p);
        return walk.Finish();
    }

    /// <summary>One run over the staged files, grouped by root in the order roots first appear (a dictionary of string
    /// lists, no LINQ grouping: Native AOT size).</summary>
    sealed class Walk(StagedIndex index, string top, CheckSettings s)
    {
        readonly StagedOutcome _result = new();
        readonly Dictionary<string, List<string>> _roots = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _order = [];

        public void Add(string path)
        {
            var root = FileCheck.RootOf(Path.Combine(top, path));
            if (!_roots.TryGetValue(root, out var list)) { _roots[root] = list = []; _order.Add(root); }
            list.Add(path);
        }

        public StagedOutcome Finish()
        {
            _result.NoFiles = _order.Count == 0;
            foreach (var root in _order) Root(root, _roots[root]);
            return _result;
        }

        /// <summary>One root: its staged settings and baseline, then each staged file of it.</summary>
        void Root(string root, List<string> paths)
        {
            var ctx = CheckRun.Context(root, Settings(root), []);
            var baselinePath = Path.GetFullPath(ctx.Options.BaselineFile());
            var relBaseline = InRepository(top, baselinePath);
            var blob = ctx.Options.NoBaseline || relBaseline is null ? null : index.Contents(":" + relBaseline);
            if (blob?.Content is not { } bytes)
            {
                _result.MissingBaselines.Add(relBaseline ?? baselinePath);
                return;
            }
            if (index.Info("HEAD:" + relBaseline).Oid != blob.Oid) _result.ChangedBaselines.Add(relBaseline!);
            var file = Baseline.Parse(FileCheck.Decode(bytes), relBaseline!);
            _result.Check.Enter(root, ctx.Options);
            foreach (var path in paths)
            {
                var staged = index.Contents(":" + path);
                if (staged.Content is not { } content) continue; // a submodule or an object git cannot give as a blob
                var current = FileCheck.Analyze(ctx.Options, ctx.Rules, root, FileCheck.Relative(root, Path.Combine(top, path)), FileCheck.Decode(content));
                if (current.ExcludedReason is not null) continue;
                _result.Checked++;
                CheckRun.AgainstBaselineFile(file, baselinePath, current, ctx, s, _result.Check, new SortedDictionary<string, string>(StringComparer.Ordinal));
            }
        }

        /// <summary>The settings of a root with its staged <c>.skarp.json</c> as the repository level: read through an
        /// overlay when the index has it; skipped (<c>--no-config</c>) when only the working tree has it.</summary>
        CheckSettings Settings(string root)
        {
            var path = Path.Combine(root, SettingsFileReader.FileName);
            var locations = s.Locations ?? SettingsLocations.Default;
            List<string> args = [.. s.SettingsArgs];
            var rel = InRepository(top, path);
            var staged = rel is null ? null : index.Contents(":" + rel);
            if (staged?.Content is { } text)
            {
                args.AddRange(["--config", path]);
                locations = locations.WithOverlay(path, FileCheck.Decode(text));
            }
            else if (File.Exists(path)) args.Add("--no-config");
            return new CheckSettings
            {
                Gate = s.Gate, JudgeHealth = s.JudgeHealth, MinSeverity = s.MinSeverity, Save = false, SettingsArgs = args, Home = s.Home, Now = s.Now, Locations = locations,
            };
        }
    }

    /// <summary>The path relative to the work tree root with '/' separators, or null when it lies outside.</summary>
    static string? InRepository(string top, string full)
    {
        var rel = Path.GetRelativePath(top, full).Replace('\\', '/');
        if (Path.IsPathRooted(rel)) return null;
        return rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) ? null : rel;
    }
}
