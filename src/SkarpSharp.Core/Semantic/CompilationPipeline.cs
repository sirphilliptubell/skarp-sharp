using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace SkarpSharp.Semantic;

/// <summary>The inputs of one compilation, parsed from the recorded compiler arguments.</summary>
public sealed class CompileInput
{
    public required RecordedProject Record { get; init; }
    public string ProjectDirectory => Path.GetDirectoryName(Record.Project)!;
    public required string AssemblyName { get; init; }
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required IReadOnlyList<(string Path, ImmutableArray<string> Aliases, bool Embed)> References { get; init; }
    public required IReadOnlyList<string> AnalyzerPaths { get; init; }
    public required CSharpParseOptions ParseOptions { get; init; }
    public required CSharpCompilationOptions Options { get; init; }
    public string? ArgumentError { get; init; }
    /// <summary>Measured (chosen target framework) or compiled only as a dependency.</summary>
    public bool Measure { get; set; }
    /// <summary>Walked for references (every chosen input, test projects included) even when not measured.</summary>
    public bool Walk { get; set; }
    public List<(string Path, string Text)> Generated { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>Parses recorded csc arguments with Roslyn's own command-line parser (no own parsing of compiler options).</summary>
    public static CompileInput From(RecordedProject record)
    {
        var args = record.Arguments.Where(a => !a.EndsWith("csc.dll", StringComparison.OrdinalIgnoreCase) && !a.EndsWith("csc.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var dir = Path.GetDirectoryName(record.Project)!;
        var parsed = CSharpCommandLineParser.Default.Parse(args, dir, sdkDirectory: null);
        var error = parsed.Errors.FirstOrDefault(e => e.Severity == DiagnosticSeverity.Error);
        return new CompileInput
        {
            Record = record,
            AssemblyName = Path.GetFileNameWithoutExtension(parsed.OutputFileName ?? parsed.CompilationName ?? record.AssemblyName),
            SourcePaths = [.. parsed.SourceFiles.Select(s => s.Path)],
            References = [.. parsed.MetadataReferences.Select(r => (r.Reference, r.Properties.Aliases, r.Properties.EmbedInteropTypes))],
            AnalyzerPaths = [.. parsed.AnalyzerReferences.Select(a => a.FilePath)],
            ParseOptions = parsed.ParseOptions,
            Options = parsed.CompilationOptions,
            ArgumentError = error?.ToString(),
        };
    }
}

/// <summary>Per compilation: the compilation, which trees are generated, and the input it came from.</summary>
public sealed record CompiledProject(CSharpCompilation Compilation, IReadOnlySet<SyntaxTree> GeneratedTrees, CompileInput Input, int MissingSources);

/// <summary>Stage times and the largest child process of the compile stage.</summary>
public sealed class PipelineStats
{
    /// <summary>Wall time the pipeline waited for generator steps (they run ahead of it, in parallel).</summary>
    public long GeneratorMilliseconds { get; set; }
    /// <summary>Sum of the generator compiler processes' times (more than the wall time when they overlap).</summary>
    public long GeneratorProcessMilliseconds;
    public long CompileMilliseconds { get; set; }
    public long MeasureMilliseconds { get; set; }
    public long ChildPeakWorkingSetBytes { get; set; }
}

/// <summary>
/// Builds the compilations one at a time in dependency order: project references bind to an in-memory
/// metadata-only image of the dependency compiled for the target framework MSBuild negotiated (the framework in the
/// reference path); signed projects get a strong-name provider so friend assemblies with public keys work. With
/// Generators on, each project with analyzers first runs the SDK's own compiler reference-only with
/// analyzers skipped and every output in a temporary folder, and its generated files join the compilation; generator
/// projects inside the solution are compiled and emitted to the temporary folder first. Generator steps of independent
/// projects run in parallel (<see cref="GeneratorParallelism"/> SDK compilers at most), each as soon as the projects it
/// references are compiled; compiling and measuring stay sequential in dependency order.
/// </summary>
public sealed class CompilationPipeline(IReadOnlyList<CompileInput> inputs, bool generators, string workFolder)
{
    readonly Dictionary<string, List<CompileInput>> _byAssembly = inputs.GroupBy(i => i.AssemblyName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AssemblyMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<CompileInput, MetadataReference> _images = [];
    readonly ConcurrentDictionary<CompileInput, string> _imageFiles = new();
    readonly ConcurrentDictionary<CompileInput, string> _analyzerFiles = new();
    readonly Lock _statsLock = new();
    public PipelineStats Stats { get; } = new();

    /// <summary>At most this many generator steps (SDK compiler processes) at once: half the processors, 1 to 4.</summary>
    public static int GeneratorParallelism { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>The input a reference path stands for: a project of the set with that assembly name, preferring the target
    /// framework named in the path (MSBuild's negotiated framework), else the highest.</summary>
    public CompileInput? Resolve(string referencePath, CompileInput self)
    {
        var name = Path.GetFileNameWithoutExtension(referencePath);
        if (name.Equals(self.AssemblyName, StringComparison.OrdinalIgnoreCase) || !_byAssembly.TryGetValue(name, out var candidates)) return null;
        // The framework is a path segment (bin/Debug/net8.0/) or a part of one in the artifacts layout (debug_net8.0,
        // release_net8.0_win-x64); without the second case every target resolved to the highest framework.
        var segments = referencePath.Split('\\', '/').SelectMany(s => s.Split('_')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(c => segments.Contains(c.Record.TargetFramework))
            ?? candidates.OrderByDescending(c => TargetFrameworks.Key(c.Record.TargetFramework)).First();
    }

    /// <summary>Compiles the measured and walked inputs and the dependencies they need; calls <paramref name="measure"/> for
    /// the measured and walked ones (test projects are walked for references without being measured).</summary>
    public async Task RunAsync(Action<CompiledProject> measure, CancellationToken ct = default)
    {
        var deps = inputs.ToDictionary(i => i, i => i.References.Select(r => r.Path).Concat(generators ? i.AnalyzerPaths : [])
            .Select(p => Resolve(p, i)).OfType<CompileInput>().Distinct().ToList());
        var analyzerProjects = generators
            ? inputs.SelectMany(i => i.AnalyzerPaths.Select(a => Resolve(a, i))).OfType<CompileInput>().ToHashSet() : [];
        var order = new List<CompileInput>();
        var state = new Dictionary<CompileInput, int>();
        void Visit(CompileInput n)
        {
            if (state.ContainsKey(n)) return;
            state[n] = 1;
            foreach (var d in deps[n]) Visit(d);
            order.Add(n);
        }
        foreach (var i in inputs.Where(i => i.Measure || i.Walk).OrderBy(i => i.Record.Project, StringComparer.OrdinalIgnoreCase)) Visit(i);
        var neededBy = order.SelectMany(i => deps[i]).ToHashSet();
        // A project is "done" once compiled and its image and analyzer files are emitted; a generator step waits for the
        // projects it references and then for a free slot.
        var done = order.ToDictionary(i => i, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        for (int k = 0; k < order.Count; k++) _position[order[k]] = k + 1; // stable folder names whatever the run order
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(GeneratorParallelism);
        async Task GenerateWhenReadyAsync(CompileInput input)
        {
            foreach (var d in deps[input]) await done[d].Task.WaitAsync(cancel.Token).ConfigureAwait(false);
            await slots.WaitAsync(cancel.Token).ConfigureAwait(false);
            try { await GenerateAsync(input, cancel.Token).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
        var generated = order.ToDictionary(i => i, i => generators && i.AnalyzerPaths.Count > 0 ? Task.Run(() => GenerateWhenReadyAsync(i), cancel.Token) : Task.CompletedTask);
        try
        {
            foreach (var input in order)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                await generated[input].ConfigureAwait(false);
                Stats.GeneratorMilliseconds += sw.ElapsedMilliseconds;
                sw.Restart();
                var compiled = Compile(input);
                Stats.CompileMilliseconds += sw.ElapsedMilliseconds;
                if (input.Measure || input.Walk)
                {
                    sw.Restart();
                    measure(compiled);
                    Stats.MeasureMilliseconds += sw.ElapsedMilliseconds;
                }
                sw.Restart();
                if (analyzerProjects.Contains(input)) EmitAnalyzer(compiled);
                if (neededBy.Contains(input)) EmitImage(compiled);
                Stats.CompileMilliseconds += sw.ElapsedMilliseconds;
                done[input].SetResult();
            }
        }
        finally
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            // Generator steps still running when the pipeline stopped early: wait for them, their outcome no longer matters
            // (the pipeline's own error, if any, is the one that propagates).
            foreach (var t in generated.Values) await t.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            foreach (var m in _metadata.Values) m.Dispose();
            _metadata.Clear();
        }
    }

    CompiledProject Compile(CompileInput input)
    {
        var trees = new SyntaxTree?[input.SourcePaths.Count];
        int missing = 0;
        Parallel.For(0, input.SourcePaths.Count, k =>
        {
            var path = input.SourcePaths[k];
            if (!File.Exists(path)) { Interlocked.Increment(ref missing); return; }
            using var stream = File.OpenRead(path);
            trees[k] = CSharpSyntaxTree.ParseText(SourceText.From(stream), input.ParseOptions, path);
        });
        var all = trees.OfType<SyntaxTree>().ToList();
        var generated = new HashSet<SyntaxTree>();
        foreach (var (path, text) in input.Generated)
        {
            var t = CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), input.ParseOptions, path);
            all.Add(t);
            generated.Add(t);
        }
        var refs = new List<MetadataReference>();
        foreach (var (path, aliases, embed) in input.References)
        {
            if (Resolve(path, input) is { } dep && _images.TryGetValue(dep, out var image))
            {
                refs.Add(aliases.IsDefaultOrEmpty ? image : image.WithAliases(aliases));
                continue;
            }
            if (!File.Exists(path)) { input.Notes.Add($"reference not found: {Path.GetFileName(path)}"); continue; }
            if (!_metadata.TryGetValue(path, out var md)) _metadata[path] = md = AssemblyMetadata.CreateFromFile(path);
            refs.Add(md.GetReference(aliases: aliases.IsDefault ? default : aliases, embedInteropTypes: embed, filePath: path));
        }
        var options = input.Options;
        if (options.CryptoKeyFile is not null || !options.CryptoPublicKey.IsDefaultOrEmpty)
            options = options.WithStrongNameProvider(new DesktopStrongNameProvider([]));
        return new CompiledProject(CSharpCompilation.Create(input.AssemblyName, all, refs, options), generated, input, missing);
    }

    void EmitImage(CompiledProject p)
    {
        using var ms = new MemoryStream();
        var result = p.Compilation.Emit(ms, options: new EmitOptions(metadataOnly: true));
        if (!result.Success)
        {
            _images[p.Input] = p.Compilation.ToMetadataReference();
            p.Input.Notes.Add("metadata-only emit failed; the compilation is kept as the reference");
            return;
        }
        var bytes = ms.ToArray();
        _images[p.Input] = MetadataReference.CreateFromImage(bytes, filePath: p.Input.AssemblyName + ".dll");
        if (generators)
        {
            var file = Path.Combine(workFolder, "images", p.Input.Record.TargetFramework, p.Input.AssemblyName + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
            _imageFiles[p.Input] = file;
        }
    }

    void EmitAnalyzer(CompiledProject p)
    {
        var file = Path.Combine(workFolder, "analyzers", p.Input.Record.TargetFramework, p.Input.AssemblyName + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var fs = File.Create(file);
        var result = p.Compilation.Emit(fs);
        if (result.Success) _analyzerFiles[p.Input] = file;
        else p.Input.Notes.Add("generator project could not be compiled: " + result.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error));
    }

    readonly Dictionary<CompileInput, int> _position = [];

    /// <summary>
    /// Runs the SDK's compiler (the csc.dll MSBuild would use) on the recorded arguments with <c>/refonly</c> and
    /// <c>/skipanalyzers+</c>, so only source generators run; outputs and generated files go to the temporary folder.
    /// Project references and in-solution generator projects point at our emitted files.
    /// </summary>
    async Task GenerateAsync(CompileInput input, CancellationToken ct)
    {
        var csc = Path.Combine(input.Record.RoslynTargetsPath, "bincore", "csc.dll");
        if (!File.Exists(csc)) { input.Notes.Add($"generators not run: compiler not found ({csc})"); return; }
        var args = input.Record.Arguments.ToList();
        foreach (var a in input.AnalyzerPaths)
            if (Resolve(a, input) is { } dep && _analyzerFiles.TryGetValue(dep, out var built)) args = Replace(args, a, built);
        foreach (var (path, _, _) in input.References)
            if (Resolve(path, input) is { } dep && _imageFiles.TryGetValue(dep, out var img)) args = Replace(args, path, img);
        var folder = Path.Combine(workFolder, "gen", _position[input].ToString(System.Globalization.CultureInfo.InvariantCulture));
        var generatedFolder = Path.Combine(folder, "generated");
        Directory.CreateDirectory(generatedFolder); // csc creates per-generator subfolders only when this folder exists
        string[] dropped = ["/out:", "/refout:", "/doc:", "/pdb:", "/errorlog:", "/generatedfilesout:", "/reportanalyzer", "/sourcelink:",
            "/win32manifest:", "/win32res:", "/win32icon:", "/resource:", "/linkresource:", "/embed"];
        var kept = args.Where(x => !x.EndsWith("csc.dll", StringComparison.OrdinalIgnoreCase) && !x.EndsWith("csc.exe", StringComparison.OrdinalIgnoreCase)
            && !dropped.Any(d => x.StartsWith(d, StringComparison.OrdinalIgnoreCase) || x.StartsWith("-" + d[1..], StringComparison.OrdinalIgnoreCase))).ToList();
        kept.AddRange(["/refonly", "/skipanalyzers+", "/out:" + Path.Combine(folder, "out.dll"), "/generatedfilesout:" + generatedFolder, "/nowarn:CS1591"]);
        var rsp = Path.Combine(folder, "args.rsp");
        await File.WriteAllLinesAsync(rsp, kept.Select(Quote), ct).ConfigureAwait(false);
        var run = await DotnetRunner.RunAsync(["exec", csc, "/noconfig", "@" + rsp], input.ProjectDirectory, ct).ConfigureAwait(false);
        Interlocked.Add(ref Stats.GeneratorProcessMilliseconds, run.Milliseconds);
        lock (_statsLock) Stats.ChildPeakWorkingSetBytes = Math.Max(Stats.ChildPeakWorkingSetBytes, run.PeakWorkingSetBytes);
        if (run.StartError is not null) { input.Notes.Add("generators not run: " + run.StartError); return; }
        foreach (var f in Directory.EnumerateFiles(generatedFolder, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            input.Generated.Add((f, await File.ReadAllTextAsync(f, ct).ConfigureAwait(false)));
    }

    static List<string> Replace(List<string> args, string from, string to) =>
        [.. args.Select(x => x.Replace(from, to, StringComparison.OrdinalIgnoreCase))];

    static string Quote(string arg) => arg.Contains(' ', StringComparison.Ordinal) && !arg.Contains('"', StringComparison.Ordinal) ? "\"" + arg + "\"" : arg;
}
