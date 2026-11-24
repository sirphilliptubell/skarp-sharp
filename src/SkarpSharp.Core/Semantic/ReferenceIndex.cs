using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SkarpSharp.Discovery;

namespace SkarpSharp.Semantic;

/// <summary>What the reference walk of one compilation needs from the run.</summary>
public sealed class ReferenceOptions
{
    /// <summary>The analyzed directory (paths in the facts are relative to it).</summary>
    public required string Root { get; init; }
    /// <summary>True when the syntax tier kept the file (path relative to the root): candidates come only from kept files.</summary>
    public required Func<string, bool> KeptFile { get; init; }
    /// <summary>Assembly names of the solution's projects: only their symbols are recorded.</summary>
    public required IReadOnlySet<string> SolutionAssemblies { get; init; }
    /// <summary>A test project: walked, references flagged as test use, never reported.</summary>
    public bool IsTestProject { get; init; }
    /// <summary>Source generators ran.</summary>
    public bool Generators { get; init; } = true;
    /// <summary>Shared semantic models and error diagnostics of the compilation (null: created here).</summary>
    public CompilationModels? Models { get; init; }
    /// <summary>Also record the namespace references of the project's kept, non-test, handwritten files
    /// (<see cref="ProjectFacts.Namespaces"/>).</summary>
    public bool Namespaces { get; init; }
    /// <summary>Collect the unused-code candidates (false when only the namespace references are needed).</summary>
    public bool Candidates { get; init; } = true;
}

/// <summary>One declared private or internal member or type that may be reported.</summary>
public sealed class UnusedCandidate
{
    /// <summary>Symbol id (<see cref="SymbolIds"/>).</summary>
    public string Id { get; init; } = "";
    /// <summary>method, constructor, field, constant, property, event, class, struct, interface, enum, delegate, record, record struct.</summary>
    public string Kind { get; init; } = "";
    /// <summary>private, internal or file.</summary>
    public string Accessibility { get; init; } = "";
    /// <summary>The name roots by strings and inactive code compare with (a constructor carries its type's name).</summary>
    public string Name { get; init; } = "";
    /// <summary>Display name for messages (member name, or the short type name).</summary>
    public string Display { get; init; } = "";
    /// <summary>Allowlist name: namespace, types and member joined with dots, no generic arity (<c>Ns.Outer.Inner.Member</c>).</summary>
    public string FullName { get; init; } = "";
    /// <summary>Finding place: <c>Type::Member</c> (overloads <c>#2</c>, <c>#3</c> in declaration order) or the type name.</summary>
    public string PlaceKey { get; init; } = "";
    public string Path { get; init; } = "";
    public int Line { get; init; }
    public bool IsType { get; init; }
    /// <summary>Ids of the containing types, innermost first.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];
    /// <summary>Visible to friend assemblies (declared internal inside types that are not private).</summary>
    public bool FriendVisible { get; init; }
    /// <summary>An instance field of a struct (rooted when the struct has native layout use).</summary>
    public bool StructField { get; init; }
    /// <summary>A classic extension method (<c>this</c> parameter): another target may bind a call to it.</summary>
    public bool Extension { get; init; }
    /// <summary>Types: the names of the extension methods declared in it, whatever their accessibility (a call to one
    /// names the method, not the type).</summary>
    public IReadOnlyList<string> ExtensionNames { get; init; } = [];
    /// <summary>A non-constant field or an auto-property (written-but-never-read is judged for it).</summary>
    public bool ValueMember { get; init; }
    /// <summary>The field or auto-property has a non-constant initializer (a write that runs when its type is live).</summary>
    public bool Initialized { get; init; }
    /// <summary>Why a written-but-never-read value member is not reported (record member, struct
    /// instance member, fixed layout, delegate, IDisposable, GCHandle, object, dynamic or open type parameter type), or null.</summary>
    public string? UnreadExclusion { get; init; }
    /// <summary>The symbol root is an override, virtual, abstract, interface implementation or
    /// pattern member: it keeps the member (and what it uses) alive only while its containing type is live.</summary>
    public bool TypeBound { get; init; }
    /// <summary>The symbol-level root, or null.</summary>
    public string? Root { get; set; }
    /// <summary>Why the candidate cannot be judged (unresolved symbols, invalid code), or null.</summary>
    public string? NotEvaluated { get; set; }
}

/// <summary>How a reference uses its target. Only field and property references are read or write;
/// every other reference is other, which counts as a read.</summary>
public enum EdgeKind : byte
{
    Other,
    Read,
    Write,
}

/// <summary>
/// One reference fact (ids only). The source is the id of the innermost candidate-shaped symbol around the
/// reference (the member, else the nearest private, internal or file-local containing type), or a pseudo-source:
/// <see cref="Live"/> for code that is never a candidate (public members of public types, generated code) and
/// <see cref="Test"/> for test projects and test files.
/// </summary>
public readonly record struct ReferenceEdge(string Source, string Target, EdgeKind Kind)
{
    public const string Live = "*live*";
    public const string Test = "*test*";
}

/// <summary>
/// The compact per-compilation facts of the reference walk (no symbols, so the compilation is released before the
/// next project): the ids used from code and from tests, the name roots, the reflected and layout-sensitive types, the
/// friend assemblies and the candidates.
/// </summary>
public sealed class ProjectFacts
{
    public string Project { get; init; } = "";
    public string ProjectFile { get; init; } = "";
    public string AssemblyName { get; init; } = "";
    public string TargetFramework { get; init; } = "";
    public bool IsTest { get; init; }
    /// <summary>Every reference to a private or internal symbol of the solution from outside the target itself
    /// (source, target, read, write or other).</summary>
    public HashSet<ReferenceEdge> Edges { get; } = [];
    /// <summary>Types passed to a method marked [RequiresUnreferencedCode] (their members are read by reflection).</summary>
    public HashSet<string> UnreferencedCodeTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Identifier-shaped words of string literals, dynamic member names and identifiers in invalid code.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    /// <summary>Identifier words of code inactive in this compilation (#if branches not taken).</summary>
    public HashSet<string> InactiveWords { get; } = new(StringComparer.Ordinal);
    /// <summary>Types whose members are reflected over: typeof(T), or a type argument of a [DynamicallyAccessedMembers] parameter.</summary>
    public HashSet<string> ReflectedTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Structs with native layout use (pointers, sizeof, extern signatures, interop type arguments).</summary>
    public HashSet<string> LayoutTypes { get; } = new(StringComparer.Ordinal);
    /// <summary>Names of methods called (or named as method groups) that bind outside the solution, for example
    /// <c>Remove</c> of <c>Dictionary</c>; in a target not analyzed the same call may bind to a solution's extension method.</summary>
    public HashSet<string> ExternalCallNames { get; } = new(StringComparer.Ordinal);
    /// <summary>InternalsVisibleTo friends (assembly names).</summary>
    public List<string> Friends { get; } = [];
    public List<UnusedCandidate> Candidates { get; } = [];
    /// <summary>Why the project's candidates cannot be judged (XAML or Razor without generated code, generators off), or null.</summary>
    public string? NotEvaluated { get; set; }
    /// <summary>The namespace references of the project (null when not asked for, and for test projects).</summary>
    public NamespaceFacts? Namespaces { get; set; }
}

/// <summary>Semantic models and error diagnostics of one compilation, shared by the type measurement and the reference walk.</summary>
public sealed class CompilationModels(Compilation compilation, Func<SyntaxTree, bool> isGenerated)
{
    readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _models = new();
    readonly ConcurrentDictionary<SyntaxTree, Diagnostic[]> _errors = new();
    readonly ConcurrentDictionary<SyntaxTree, bool> _generated = new();

    public Compilation Compilation => compilation;
    public SemanticModel Model(SyntaxTree tree) => _models.GetOrAdd(tree, t => compilation.GetSemanticModel(t));
    public bool IsGenerated(SyntaxTree tree) => _generated.GetOrAdd(tree, isGenerated);
    /// <summary>Error diagnostics of a tree (binding every member body once).</summary>
    public Diagnostic[] Errors(SyntaxTree tree) => _errors.GetOrAdd(tree, t => [.. Model(t).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)]);
}

/// <summary>
/// The reference walk: one descent per
/// syntax tree (generated trees included) over the operation trees of every declaration, plus the symbols of
/// signatures, base lists and constraints, and semantic-model queries where the operation tree is silent (foreach,
/// await, deconstruction, nameof). A reference counts for the definition it binds to and for the containing types of
/// that definition; a reference located inside the target member itself, or inside the target type (nested types
/// included), does not count. Only private and internal symbols of the solution's assemblies are
/// recorded. Also collects the name roots, reflection and layout facts, friends and the candidates.
/// The walk has two sides: <see cref="Walker"/> (declarations, names) and <see cref="OperationWalker"/> (operation
/// trees) find the references and know their source (the enclosing member) and the kind of operation; <see cref="Uses"/>
/// turns each referenced symbol into the recorded facts (every reference passes through <see cref="Uses.Use"/> or
/// <see cref="Uses.UseType"/>, the place where edges are recorded).
/// </summary>
public static partial class ReferenceIndex
{
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^@?[A-Za-z_][A-Za-z0-9_]*(`\d+)?([.+/][A-Za-z_][A-Za-z0-9_]*(`\d+)?)*$", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedName();

    /// <summary>Longer string literals are not read for names.</summary>
    const int MaxNameStringLength = 4000;
    /// <summary>Longest text before the first comma that can be a (qualified) name.</summary>
    const int MaxQualifiedNameLength = 120;
    /// <summary>Longest text between braces read for identifiers.</summary>
    const int MaxBracedLength = 200;

    public static ProjectFacts Extract(CompiledProject compiled, ReferenceOptions options)
    {
        var comp = compiled.Compilation;
        var input = compiled.Input;
        var models = options.Models ?? new CompilationModels(comp, t => compiled.GeneratedTrees.Contains(t) || GeneratedCode.IsGeneratedFileName(t.FilePath) || GeneratedCode.HasGeneratedHeader(t));
        var facts = new ProjectFacts
        {
            Project = Path.GetRelativePath(options.Root, input.Record.Project).Replace('\\', '/'), ProjectFile = input.Record.Project, AssemblyName = input.AssemblyName,
            TargetFramework = input.Record.TargetFramework, IsTest = options.IsTestProject,
        };
        bool measured = input.Measure && !options.IsTestProject;
        if (options.Namespaces && measured) facts.Namespaces = NamespaceFacts.Declared(comp, models);
        var notEvaluated = new ProjectWalk(compiled, options, models).Run(facts);
        facts.Friends.AddRange(Friends(comp));
        if (measured && options.Candidates)
        {
            facts.NotEvaluated = ProjectNotEvaluated(compiled, options.Generators);
            facts.Candidates.AddRange(CandidateCollector.Collect(compiled, models, options, notEvaluated));
        }
        return facts;
    }

    /// <summary>The walk of every syntax tree of one compilation, in parallel; the facts are merged under a lock.</summary>
    sealed class ProjectWalk(CompiledProject compiled, ReferenceOptions options, CompilationModels models)
    {
        /// <summary>Walks every tree into the facts; returns the types that cannot be judged (invalid code, unresolved types).</summary>
        public Dictionary<INamedTypeSymbol, string> Run(ProjectFacts facts)
        {
            var trees = compiled.Compilation.SyntaxTrees.ToList();
            var notEvaluated = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
            var shared = new Shared(compiled.Compilation, options.SolutionAssemblies);
            var gate = new Lock();
            Parallel.For(0, trees.Count, i =>
            {
                var uses = Walk(trees[i], shared);
                lock (gate)
                {
                    uses.AddTo(facts, notEvaluated);
                    if (facts.Namespaces is { } ns) uses.Namespaces?.AddTo(ns);
                }
            });
            return notEvaluated;
        }

        Uses Walk(SyntaxTree tree, Shared shared)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot();
            bool test = options.IsTestProject || TestDetection.FindTestUsing(root).Namespace is not null;
            bool generated = models.IsGenerated(tree);
            var uses = new Uses(shared, test, generated) { Namespaces = NamespaceSource(tree, root, test, generated) };
            var w = new Walker(Model(tree), uses);
            w.Node(root, default);
            w.Tokens(root);
            return uses;
        }

        /// <summary>The namespace collector of a tree that is a namespace source (a kept, handwritten, non-test
        /// file of a measured project); other trees of such a project only count as excluded; null otherwise.</summary>
        NamespaceUses? NamespaceSource(SyntaxTree tree, CompilationUnitSyntax root, bool test, bool generated)
        {
            if (!options.Namespaces) return null;
            if (options.IsTestProject || !compiled.Input.Measure) return null;
            var path = Path.GetRelativePath(options.Root, tree.FilePath).Replace('\\', '/');
            if (generated) return NamespaceUses.Skipped(NamespaceSourceKind.Generated);
            if (test || !options.KeptFile(path)) return NamespaceUses.Skipped(NamespaceSourceKind.Excluded);
            return new NamespaceUses(compiled.Compilation.AssemblyName ?? "", path, tree, root);
        }

        /// <summary>
        /// Models of measured files are shared with the measurement and the candidates; other trees (generated code,
        /// tests, excluded files) get a model of their own that is dropped right after the walk (the walker is not
        /// kept), which bounds the memory of binding large generated code.
        /// </summary>
        SemanticModel Model(SyntaxTree tree)
        {
            bool shared = compiled.Input.Measure && !options.IsTestProject && !models.IsGenerated(tree)
                && options.KeptFile(Path.GetRelativePath(options.Root, tree.FilePath).Replace('\\', '/'));
            return shared ? models.Model(tree) : compiled.Compilation.GetSemanticModel(tree);
        }
    }

    /// <summary>InternalsVisibleTo friends of the compilation's assembly (assembly names).</summary>
    static IEnumerable<string> Friends(Compilation comp)
    {
        foreach (var a in comp.Assembly.GetAttributes())
            if (a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"
                && a.ConstructorArguments is [{ Value: string friend }])
                yield return friend.Split(',')[0].Trim();
    }

    /// <summary>Project-level reasons the candidates cannot be judged, or null.</summary>
    static string? ProjectNotEvaluated(CompiledProject compiled, bool generators)
    {
        if (!generators && compiled.Input.AnalyzerPaths.Count > 0) return "source generators not run (--no-generators): uses from generated code are unknown";
        var global = compiled.Compilation.GetDeclarationDiagnostics().FirstOrDefault(IsGlobalUnresolved);
        if (global is not null) return $"unresolved symbols in the compilation ({global.Id})";
        return MarkupWithoutOutput(compiled) is { } markup ? $"{markup} files without their generated C# (markup compile or generators did not run)" : null;
    }

    static bool IsGlobalUnresolved(Diagnostic d) =>
        d.Location == Location.None && d.Severity == DiagnosticSeverity.Error && TypeMeasurement.UnresolvedCodes.Contains(d.Id);

    /// <summary>The markup kind of a project whose markup has no generated C# compiled (no generator output, no
    /// <c>.g.cs</c> file), or null.</summary>
    static string? MarkupWithoutOutput(CompiledProject compiled)
    {
        var input = compiled.Input;
        if (input.Record.Project.Length == 0) return null;
        var markup = MarkupFiles(Path.GetDirectoryName(input.Record.Project)!);
        if (markup is null || compiled.GeneratedTrees.Count > 0) return null;
        return input.SourcePaths.Any(IsMarkupOutput) ? null : markup;
    }

    static readonly ConcurrentDictionary<string, string?> MarkupCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first markup kind (.xaml, .axaml, .razor, .cshtml) in the project folder (bin and obj skipped), or null.</summary>
    static string? MarkupFiles(string folder) => MarkupCache.GetOrAdd(folder, FindMarkup);

    static string? FindMarkup(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (MarkupIn(dir) is { } ext) return ext;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!IsSkippedFolder(sub)) stack.Push(sub);
        }
        return null;
    }

    static string? MarkupIn(string dir) =>
        Directory.EnumerateFiles(dir).Select(f => Path.GetExtension(f).ToLowerInvariant()).FirstOrDefault(ext => ext is ".xaml" or ".axaml" or ".razor" or ".cshtml");

    static bool IsSkippedFolder(string path) => Path.GetFileName(path) is "bin" or "obj" or ".git" or "node_modules";

    static bool IsMarkupOutput(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds the names a string literal may stand for: a (dotted, nested, assembly-qualified) type or member name,
    /// and every identifier inside braces (DebuggerDisplay, format strings). Spaced text is not a name.</summary>
    internal static void AddStringNames(string text, HashSet<string> names)
    {
        if (text.Length == 0 || text.Length > MaxNameStringLength) return;
        AddQualifiedName(text.Split(',')[0].Trim(), names);
        AddBracedIdentifiers(text, names);
    }

    static void AddQualifiedName(string head, HashSet<string> names)
    {
        if (head.Length > MaxQualifiedNameLength || !QualifiedName().IsMatch(head)) return;
        foreach (var segment in head.TrimStart('@').Split('.', '+', '/'))
        {
            int tick = segment.IndexOf('`');
            AddAccessorName(tick < 0 ? segment : segment[..tick], names);
        }
    }

    /// <summary>Adds the name and, for an accessor name (<c>get_X</c>), the property or event name.</summary>
    static void AddAccessorName(string name, HashSet<string> names)
    {
        names.Add(name);
        foreach (var prefix in (ReadOnlySpan<string>)["get_", "set_", "add_", "remove_", "init_"])
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length) names.Add(name[prefix.Length..]);
    }

    static void AddBracedIdentifiers(string text, HashSet<string> names)
    {
        int open = 0;
        while ((open = text.IndexOf('{', open)) >= 0)
        {
            int close = text.IndexOf('}', open + 1);
            if (close < 0) break;
            if (close - open <= MaxBracedLength)
                foreach (Match m in Identifier().Matches(text, open + 1).TakeWhile(m => m.Index < close)) names.Add(m.Value);
            open = close + 1;
        }
    }

    /// <summary>Adds every identifier-shaped word of a text (files compiled only by targets not analyzed).</summary>
    internal static void AddIdentifierWords(string text, HashSet<string> words)
    {
        foreach (var m in Identifier().EnumerateMatches(text)) words.Add(text.Substring(m.Index, m.Length));
    }

    /// <summary>State shared by the walkers of one compilation (all thread safe).</summary>
    sealed class Shared(Compilation compilation, IReadOnlySet<string> solution)
    {
        public Compilation Compilation { get; } = compilation;
        readonly ConcurrentDictionary<ISymbol, string?> _ids = new(SymbolEqualityComparer.Default);
        readonly ConcurrentDictionary<ISymbol, byte> _declared = new(SymbolEqualityComparer.Default);

        public string? Id(ISymbol s) => _ids.GetOrAdd(s, SymbolIds.Of);
        public bool InSolution(ISymbol s) => s.ContainingAssembly is { } a && solution.Contains(a.Name);
        /// <summary>True the first time a declared symbol (type header or member signature) is seen in this compilation.</summary>
        public bool FirstDeclaration(ISymbol s) => _declared.TryAdd(s, 0);
    }

    /// <summary>The source of a reference: the enclosing named member (lambdas, local functions and accessors folded in)
    /// and the innermost enclosing type.</summary>
    readonly record struct Ctx(ISymbol? Member, INamedTypeSymbol? Type);

    /// <summary>
    /// The facts of one syntax tree's walk: what each referenced symbol means (a use of its definition and containing
    /// types, a reflected or layout-sensitive type, an unresolved type, a call bound outside the solution), plus the
    /// names and inactive words the walker adds. Symbols only, no syntax.
    /// </summary>
    sealed class Uses(Shared shared, bool test, bool generated)
    {
        const int MaxQuotedCode = 60;

        public HashSet<ReferenceEdge> Edges { get; } = [];
        public HashSet<string> UnreferencedCode { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Inactive { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Reflected { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Layout { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExternalCalls { get; } = new(StringComparer.Ordinal);
        public Dictionary<INamedTypeSymbol, string> NotEvaluated { get; } = new(SymbolEqualityComparer.Default);
        public Shared Shared => shared;
        /// <summary>The tree's namespace collector (null: the tree is not a namespace source, or a declaration's
        /// header is being used for unused code only).</summary>
        public NamespaceUses? Namespaces { get; set; }

        /// <summary>A throwaway copy whose unused-code facts are dropped: it records the namespace references of a
        /// declaration's header or signature in the tree that holds its first declaration (deterministic).</summary>
        public Uses NamespaceOnly() => new(shared, test, generated) { Namespaces = Namespaces };

        /// <summary>Merges this tree's facts into the project's (called under the project's lock).</summary>
        public void AddTo(ProjectFacts facts, Dictionary<INamedTypeSymbol, string> notEvaluated)
        {
            facts.Edges.UnionWith(Edges);
            facts.UnreferencedCodeTypes.UnionWith(UnreferencedCode);
            facts.Names.UnionWith(Names);
            facts.InactiveWords.UnionWith(Inactive);
            facts.ReflectedTypes.UnionWith(Reflected);
            facts.LayoutTypes.UnionWith(Layout);
            facts.ExternalCallNames.UnionWith(ExternalCalls);
            // Deterministic whatever the thread order: the ordinal-smallest reason of a type's parts.
            foreach (var (t, reason) in NotEvaluated)
                if (!notEvaluated.TryGetValue(t, out var had) || string.CompareOrdinal(reason, had) < 0) notEvaluated[t] = reason;
        }

        /// <summary>Invalid code: its type is not evaluated (the first reason of the tree is kept).</summary>
        public void Invalid(SyntaxNode syntax, Ctx ctx)
        {
            if (ctx.Type is not { } t || NotEvaluated.ContainsKey(t)) return;
            var text = syntax.ToString().ReplaceLineEndings(" ");
            NotEvaluated[t] = $"invalid code at line {syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {(text.Length > MaxQuotedCode ? text[..(MaxQuotedCode - 3)] + "..." : text)}";
        }

        /// <summary>A call bound outside the solution: its name (extension methods of targets not analyzed).</summary>
        public void External(IMethodSymbol method)
        {
            if (!shared.InSolution(method)) ExternalCalls.Add(method.Name);
        }

        public void Use(ISymbol? symbol, Ctx ctx) => Use(symbol, ctx, EdgeKind.Other);

        /// <summary>A reference to a symbol with its kind (read or write for field and property references).</summary>
        public void Use(ISymbol? symbol, Ctx ctx, EdgeKind kind)
        {
            switch (symbol)
            {
                case null: return;
                case ITypeSymbol t: UseType(t, ctx); return;
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise, AssociatedSymbol: { } owner }:
                    symbol = owner;
                    break;
                case IFieldSymbol { AssociatedSymbol: { } backed }:
                    symbol = backed;
                    break;
            }
            if (SymbolIds.Definition(symbol) is not { } def || !shared.InSolution(def)) return;
            if (!SymbolEqualityComparer.Default.Equals(def, ctx.Member)) Record(def, ctx, kind);
            if (def.ContainingType is { } owner2) UseTypeDefinition(owner2.OriginalDefinition, ctx);
        }

        public void UseType(ITypeSymbol? type, Ctx ctx)
        {
            switch (type)
            {
                case IArrayTypeSymbol a: UseType(a.ElementType, ctx); return;
                case IPointerTypeSymbol p: UseType(p.PointedAtType, ctx); MarkLayout(p.PointedAtType); return;
                case IFunctionPointerTypeSymbol f: UseFunctionPointer(f, ctx); return;
                case INamedTypeSymbol n: UseNamedType(n, ctx); return;
                default: return; // null, type parameters, dynamic
            }
        }

        void UseFunctionPointer(IFunctionPointerTypeSymbol f, Ctx ctx)
        {
            foreach (var p in f.Signature.Parameters)
            {
                UseType(p.Type, ctx);
                MarkLayout(p.Type);
            }
            UseType(f.Signature.ReturnType, ctx);
        }

        void UseNamedType(INamedTypeSymbol n, Ctx ctx)
        {
            // Tuples first: a deconstruction (var (a, b) = ...) compiles without System.ValueTuple (net35), and its
            // tuple type then wraps an error type that is not an unresolved symbol of the code.
            if (n.IsTupleType)
            {
                foreach (var e in n.TupleElements) UseType(e.Type, ctx);
                return;
            }
            if (n.TypeKind == TypeKind.Error)
            {
                UseErrorType(n, ctx);
                return;
            }
            if (n.IsGenericType && !n.IsUnboundGenericType) UseTypeArguments(n, ctx);
            if (n.ContainingType is { IsGenericType: true } outer && !ReferenceEquals(outer, outer.OriginalDefinition)) UseType(outer, ctx);
            UseTypeDefinition(n.OriginalDefinition, ctx);
        }

        /// <summary>An unresolved type: its type is not evaluated, its name and candidate symbols count.</summary>
        void UseErrorType(INamedTypeSymbol n, Ctx ctx)
        {
            if (ctx.Type is { } t && !NotEvaluated.ContainsKey(t)) NotEvaluated[t] = $"unresolved type '{n.Name}'";
            Names.Add(n.Name);
            if (n is IErrorTypeSymbol e)
                foreach (var c in e.CandidateSymbols) Use(c, ctx);
        }

        void UseTypeArguments(INamedTypeSymbol n, Ctx ctx)
        {
            var def = n.OriginalDefinition;
            for (int i = 0; i < n.TypeArguments.Length; i++)
            {
                UseType(n.TypeArguments[i], ctx);
                if (HasDam(def.TypeParameters[i])) Reflect(n.TypeArguments[i]);
            }
        }

        /// <summary>The type arguments of a called generic method; <c>System.Runtime*</c> methods mark them layout-sensitive.</summary>
        public void UseTypeArguments(IMethodSymbol m, Ctx ctx)
        {
            if (m.TypeArguments.Length == 0) return;
            bool interop = m.ContainingType?.ContainingNamespace?.ToDisplayString() is { } ns && ns.StartsWith("System.Runtime", StringComparison.Ordinal);
            var def = m.OriginalDefinition;
            for (int i = 0; i < m.TypeArguments.Length; i++)
            {
                UseType(m.TypeArguments[i], ctx);
                if (HasDam(def.TypeParameters[i])) Reflect(m.TypeArguments[i]);
                if (interop) MarkLayout(m.TypeArguments[i]);
            }
        }

        static bool HasDam(ITypeParameterSymbol tp) =>
            tp.GetAttributes().Any(a => a.AttributeClass?.Name == "DynamicallyAccessedMembersAttribute");

        /// <summary>A use of a type is a use of its containing types too, except those the source lies inside.</summary>
        void UseTypeDefinition(INamedTypeSymbol def, Ctx ctx)
        {
            if (Namespaces is { Active: true } ns) ns.Add(def, ctx.Type, shared.InSolution);
            for (var c = def; c is not null; c = c.ContainingType)
            {
                if (!shared.InSolution(c) || IsInside(ctx, c)) return;
                Record(c, ctx, EdgeKind.Other);
            }
        }

        static bool IsInside(Ctx ctx, INamedTypeSymbol type) => ctx.Type is { } self && SymbolFacts.IsWithin(self, type);

        void Record(ISymbol def, Ctx ctx, EdgeKind kind)
        {
            if (!IsRecorded(def)) return;
            if (shared.Id(def) is { } id) Edges.Add(new ReferenceEdge(Source(ctx), id, kind));
        }

        Ctx _lastCtx;
        string? _lastSource;

        /// <summary>The edge source of a reference: test code, generated code, else the innermost
        /// candidate-shaped symbol around it (cached: consecutive references mostly share the context).</summary>
        string Source(Ctx ctx)
        {
            if (test) return ReferenceEdge.Test;
            if (generated) return ReferenceEdge.Live;
            if (_lastSource is null || _lastCtx != ctx)
            {
                _lastCtx = ctx;
                _lastSource = CandidateSource(ctx) ?? ReferenceEdge.Live;
            }
            return _lastSource;
        }

        /// <summary>The enclosing member when it has a candidate's kind and accessibility, else the nearest private,
        /// internal or file-local containing type (a public member of an internal type speaks for its type), or null.</summary>
        string? CandidateSource(Ctx ctx)
        {
            if (IsCandidateMember(ctx.Member) && shared.Id(ctx.Member!) is { } member) return member;
            for (var t = ctx.Type; t is not null; t = t.ContainingType)
                if (CandidateNames.Access(t) is not null && shared.Id(t) is { } type) return type;
            return null;
        }

        static bool IsCandidateMember(ISymbol? m) =>
            m is { ContainingType: { } owner } && CandidateNames.MemberKind(m, owner) is not null && CandidateNames.Access(m) is not null;

        /// <summary>A call to a method marked [RequiresUnreferencedCode] reflects over the types of its
        /// arguments (before the implicit conversion) and its type arguments (JsonSerializer.Serialize(value)).</summary>
        public void UnreferencedCodeCall(IInvocationOperation call)
        {
            if (!call.TargetMethod.GetAttributes().Any(a => a.AttributeClass?.Name == "RequiresUnreferencedCodeAttribute")) return;
            foreach (var arg in call.Arguments)
                MarkUnreferenced(arg.Value is IConversionOperation { IsImplicit: true } c ? c.Operand.Type : arg.Value.Type);
            foreach (var ta in call.TargetMethod.TypeArguments) MarkUnreferenced(ta);
        }

        void MarkUnreferenced(ITypeSymbol? type)
        {
            while (type is IArrayTypeSymbol a) type = a.ElementType;
            if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } n) return;
            foreach (var ta in n.TypeArguments) MarkUnreferenced(ta);
            if (shared.InSolution(n) && shared.Id(n.OriginalDefinition) is { } id) UnreferencedCode.Add(id);
        }

        /// <summary>Only private, internal and file-local symbols (and members without accessibility) are recorded.</summary>
        static bool IsRecorded(ISymbol def) =>
            def.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.NotApplicable || def is INamedTypeSymbol { IsFileLocal: true };

        public void Reflect(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } n || !shared.InSolution(n)) return;
            if (shared.Id(n.OriginalDefinition) is { } id) Reflected.Add(id);
        }

        /// <summary>A struct with native layout use, and the struct types of its instance fields.</summary>
        public void MarkLayout(ITypeSymbol type)
        {
            while (type is IPointerTypeSymbol or IArrayTypeSymbol)
                type = type is IPointerTypeSymbol p ? p.PointedAtType : ((IArrayTypeSymbol)type).ElementType;
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } s || !shared.InSolution(s)) return;
            if (shared.Id(s.OriginalDefinition) is not { } id || !Layout.Add(id)) return;
            foreach (var f in s.OriginalDefinition.GetMembers().OfType<IFieldSymbol>())
                if (!f.IsStatic) MarkLayout(f.Type);
        }

        public void UseConstraints(ITypeParameterSymbol tp, Ctx ctx)
        {
            foreach (var c in tp.ConstraintTypes) UseType(c, ctx);
        }

        /// <summary>Declared signature types of a member.</summary>
        public void UseSignature(ISymbol member, Ctx ctx)
        {
            switch (member)
            {
                case IMethodSymbol m:
                    UseMethodSignature(m, ctx);
                    break;
                case IPropertySymbol p:
                    UseType(p.Type, ctx);
                    foreach (var x in p.Parameters) UseType(x.Type, ctx);
                    break;
                case IFieldSymbol f: UseType(f.Type, ctx); break;
                case IEventSymbol e: UseType(e.Type, ctx); break;
            }
        }

        /// <summary>Parameter, return and constraint types and explicit implementations; extern signatures mark their
        /// structs as layout-sensitive.</summary>
        void UseMethodSignature(IMethodSymbol m, Ctx ctx)
        {
            bool native = m.IsExtern || m.GetAttributes().Any(a => a.AttributeClass?.Name is "DllImportAttribute" or "LibraryImportAttribute");
            foreach (var p in m.Parameters) UseSignatureType(p.Type, native, ctx);
            if (!m.ReturnsVoid) UseSignatureType(m.ReturnType, native, ctx);
            foreach (var tp in m.TypeParameters) UseConstraints(tp, ctx);
            foreach (var e in m.ExplicitInterfaceImplementations) Use(e, ctx);
        }

        void UseSignatureType(ITypeSymbol type, bool native, Ctx ctx)
        {
            UseType(type, ctx);
            if (native) MarkLayout(type);
        }
    }

    /// <summary>
    /// The syntax walk of one tree: declarations set the source (<see cref="Ctx"/>), type headers and member signatures
    /// give their types (once per compilation), the operation tree of each declaration goes to the
    /// <see cref="OperationWalker"/>, and string literals and inactive code give names. Every reference goes to <see cref="Uses"/>.
    /// </summary>
    sealed class Walker
    {
        readonly SemanticModel _model;
        readonly Uses _uses;
        readonly OperationWalker _operations;
        Uses? _namespaceOnly;

        public Walker(SemanticModel model, Uses uses)
        {
            _model = model;
            _uses = uses;
            _operations = new OperationWalker(model, uses);
        }

        public void Node(SyntaxNode node, Ctx ctx)
        {
            ctx = Enter(node, ctx);
            if (_model.GetOperation(node) is { Parent: null } op)
            {
                _operations.Walk(op, ctx);
                // A member's body operation does not hold its attributes or parameter default values.
                foreach (var c in node.ChildNodes())
                    if (c is AttributeListSyntax or BaseParameterListSyntax) Node(c, ctx);
                return;
            }
            foreach (var c in node.ChildNodes()) Node(c, ctx);
        }

        /// <summary>The source inside a declaration: a type (its header is used once), a member (its signature is used
        /// once) or an enum member; other nodes keep the source.</summary>
        Ctx Enter(SyntaxNode node, Ctx ctx)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax when _model.GetDeclaredSymbol(node) is INamedTypeSymbol type:
                    var inType = new Ctx(type, type);
                    Declaration(type, uses => Header(type, inType, uses));
                    return inType;
                case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }
                    when _model.GetDeclaredSymbol(node) is { } member:
                    var def = SymbolIds.Definition(member) ?? member;
                    var inMember = ctx with { Member = def };
                    Declaration(def, uses => uses.UseSignature(def, inMember));
                    return inMember;
                case EnumMemberDeclarationSyntax when _model.GetDeclaredSymbol(node) is { } enumMember:
                    return ctx with { Member = enumMember };
                default:
                    return ctx;
            }
        }

        /// <summary>
        /// A type header or member signature: used once per compilation for unused code (by the first tree to reach it,
        /// namespace references off), and for namespace references only by the tree holding its first declaration in
        /// path and position order, so they do not depend on the order of the parallel walk.
        /// </summary>
        void Declaration(ISymbol symbol, Action<Uses> use)
        {
            var namespaces = _uses.Namespaces;
            if (_uses.Shared.FirstDeclaration(symbol))
            {
                _uses.Namespaces = null;
                use(_uses);
                _uses.Namespaces = namespaces;
            }
            if (namespaces is not { Active: true } || !namespaces.AtFirstDeclaration(symbol)) return;
            use(_namespaceOnly ??= _uses.NamespaceOnly());
        }

        /// <summary>String literals (names) and inactive preprocessor text (words).</summary>
        public void Tokens(SyntaxNode root)
        {
            foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
            {
                if (IsStringText(token.Kind())) AddStringNames(token.ValueText, _uses.Names);
                if (token.HasLeadingTrivia) AddInactiveWords(token.LeadingTrivia);
            }
        }

        static bool IsStringText(SyntaxKind kind) => kind is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.InterpolatedStringTextToken;

        void AddInactiveWords(SyntaxTriviaList trivia)
        {
            foreach (var t in trivia)
                if (t.IsKind(SyntaxKind.DisabledTextTrivia))
                    foreach (Match m in Identifier().Matches(t.ToString())) _uses.Inactive.Add(m.Value);
        }

        /// <summary>A type's base list, constraints, delegate signature and implicit base-constructor call.</summary>
        static void Header(INamedTypeSymbol type, Ctx ctx, Uses uses)
        {
            uses.UseType(type.BaseType, ctx);
            foreach (var i in type.Interfaces) uses.UseType(i, ctx);
            foreach (var tp in type.TypeParameters) uses.UseConstraints(tp, ctx);
            if (type.DelegateInvokeMethod is { } invoke) uses.UseSignature(invoke, ctx);
            if (type.BaseType is { } b && uses.Shared.InSolution(b) && CallsBaseConstructorImplicitly(type))
                foreach (var bc in b.InstanceConstructors)
                    if (bc.Parameters.All(p => p.IsOptional || p.IsParams)) uses.Use(bc, ctx);
        }

        /// <summary>A class without a declared constructor (or with a primary constructor) calls the base's parameterless one.</summary>
        static bool CallsBaseConstructorImplicitly(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Class && type.InstanceConstructors.Any(c => c.IsImplicitlyDeclared || c.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax));
    }

    /// <summary>
    /// The operation walk of one declaration: the members and types each operation binds to, and semantic-model queries
    /// where the operation tree is silent (foreach, await, deconstruction, nameof). Every reference goes to <see cref="Uses"/>.
    /// </summary>
    sealed class OperationWalker(SemanticModel model, Uses uses)
    {
        public void Walk(IOperation root, Ctx ctx)
        {
            var stack = new Stack<IOperation>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var op = stack.Pop();
                if (op is INameOfOperation nameOf)
                {
                    // nameof names a symbol without an operation for method groups: resolve the argument's syntax.
                    Candidates(nameOf.Argument.Syntax, ctx);
                    continue;
                }
                uses.Namespaces?.At(op.Syntax);
                Operation(op, ctx);
                if (op.Kind != OperationKind.Invalid) uses.UseType(op.Type, ctx);
                foreach (var child in op.ChildOperations) stack.Push(child);
            }
        }

        /// <summary>The symbols one operation binds to: members (calls, references, operators), then types (type tests,
        /// patterns, declarations), then the operations the semantic model completes.</summary>
        void Operation(IOperation op, Ctx ctx)
        {
            if (!MemberOperation(op, ctx) && !TypeOperation(op, ctx)) OtherOperation(op, ctx);
        }

        /// <summary>Calls, member references and user-defined operators; false when the operation is none.</summary>
        bool MemberOperation(IOperation op, Ctx ctx)
        {
            switch (op)
            {
                case IInvocationOperation i:
                    uses.Use(i.TargetMethod, ctx);
                    uses.UseTypeArguments(i.TargetMethod, ctx);
                    uses.External(i.TargetMethod);
                    uses.UnreferencedCodeCall(i);
                    return true;
                case IMethodReferenceOperation mr:
                    uses.Use(mr.Method, ctx);
                    uses.External(mr.Method);
                    return true;
                case IObjectCreationOperation o: uses.Use(o.Constructor, ctx); return true;
                case IMemberReferenceOperation m: uses.Use(m.Member, ctx, ValueUsage.Of(m)); return true;
                case IConversionOperation c: uses.Use(c.OperatorMethod, ctx); return true;
                case IBinaryOperation b: uses.Use(b.OperatorMethod, ctx); return true;
                case IUnaryOperation u: uses.Use(u.OperatorMethod, ctx); return true;
                case ICompoundAssignmentOperation ca: uses.Use(ca.OperatorMethod, ctx); return true;
                case IIncrementOrDecrementOperation id: uses.Use(id.OperatorMethod, ctx); return true;
                default: return false;
            }
        }

        /// <summary>Type operands, patterns, catch and variable types, nested signatures, collection builders and implicit
        /// indexers; false when the operation is none of them.</summary>
        bool TypeOperation(IOperation op, Ctx ctx)
        {
            switch (op)
            {
                case ITypeOfOperation t: uses.UseType(t.TypeOperand, ctx); uses.Reflect(t.TypeOperand); return true;
                case ISizeOfOperation s: uses.UseType(s.TypeOperand, ctx); uses.MarkLayout(s.TypeOperand); return true;
                case IIsTypeOperation it: uses.UseType(it.TypeOperand, ctx); return true;
                case IDeclarationPatternOperation dp: uses.UseType(dp.MatchedType, ctx); return true;
                case ITypePatternOperation tp: uses.UseType(tp.MatchedType, ctx); return true;
                case IRecursivePatternOperation rp: uses.UseType(rp.MatchedType, ctx); uses.Use(rp.DeconstructSymbol, ctx); return true;
                case ICatchClauseOperation cc: uses.UseType(cc.ExceptionType, ctx); return true;
                case IVariableDeclaratorOperation vd: uses.UseType(vd.Symbol.Type, ctx); return true;
                case ILocalFunctionOperation lf: uses.UseSignature(lf.Symbol, ctx); return true;
                case IAnonymousFunctionOperation af: uses.UseSignature(af.Symbol, ctx); return true;
                case ICollectionExpressionOperation ce: uses.Use(ce.ConstructMethod, ctx); return true;
                case IImplicitIndexerReferenceOperation ii: uses.Use(ii.LengthSymbol, ctx); uses.Use(ii.IndexerSymbol, ctx); return true;
                default: return false;
            }
        }

        /// <summary>foreach, deconstruction, await, nameof in attributes and constants, dynamic and invalid code.</summary>
        void OtherOperation(IOperation op, Ctx ctx)
        {
            switch (op)
            {
                case IForEachLoopOperation { Syntax: CommonForEachStatementSyntax fe }: ForEach(fe, ctx); break;
                case IDeconstructionAssignmentOperation { Syntax: AssignmentExpressionSyntax da }: Deconstruction(model.GetDeconstructionInfo(da), ctx); break;
                case IAwaitOperation { Syntax: AwaitExpressionSyntax aw }: Await(aw, ctx); break;
                case IAttributeOperation a:
                    // nameof inside attribute arguments folds to a constant: resolve it from the syntax.
                    foreach (var inv in a.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        if (IsNameOf(inv)) Candidates(inv.ArgumentList.Arguments[0].Expression, ctx);
                    break;
                case ILiteralOperation { Syntax: InvocationExpressionSyntax lit } when IsNameOf(lit):
                    Candidates(lit.ArgumentList.Arguments[0].Expression, ctx);
                    break;
                case IDynamicMemberReferenceOperation dm: uses.Names.Add(dm.MemberName); break;
                case IDynamicObjectCreationOperation:
                    if (op.Type is INamedTypeSymbol created) foreach (var c in created.InstanceConstructors) uses.Use(c, ctx);
                    break;
                case IDynamicInvocationOperation or IDynamicIndexerAccessOperation: Candidates(op.Syntax, ctx); break;
                case IInvalidOperation: Invalid(op, ctx); break;
                default:
                    if (op.Kind == OperationKind.None) Candidates(op.Syntax, ctx);
                    break;
            }
        }

        void ForEach(CommonForEachStatementSyntax fe, Ctx ctx)
        {
            var info = model.GetForEachStatementInfo(fe);
            uses.Use(info.GetEnumeratorMethod, ctx); uses.Use(info.MoveNextMethod, ctx); uses.Use(info.CurrentProperty, ctx); uses.Use(info.DisposeMethod, ctx);
            if (fe is ForEachVariableStatementSyntax fv) Deconstruction(model.GetDeconstructionInfo(fv), ctx);
        }

        void Await(AwaitExpressionSyntax aw, Ctx ctx)
        {
            var info = model.GetAwaitExpressionInfo(aw);
            uses.Use(info.GetAwaiterMethod, ctx); uses.Use(info.GetResultMethod, ctx); uses.Use(info.IsCompletedProperty, ctx);
        }

        static bool IsNameOf(InvocationExpressionSyntax inv) =>
            inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && inv.ArgumentList.Arguments.Count == 1;

        /// <summary>The bound symbol or, when binding left several, every candidate (method groups, ambiguity).</summary>
        void Candidates(SyntaxNode syntax, Ctx ctx)
        {
            var info = model.GetSymbolInfo(syntax);
            uses.Use(info.Symbol, ctx);
            foreach (var c in info.CandidateSymbols) uses.Use(c, ctx);
        }

        /// <summary>Invalid code: the type is not evaluated; candidates and every identifier in it count as uses and names.</summary>
        void Invalid(IOperation op, Ctx ctx)
        {
            uses.Invalid(op.Syntax, ctx);
            Candidates(op.Syntax, ctx);
            foreach (var token in op.Syntax.DescendantTokens())
                if (token.IsKind(SyntaxKind.IdentifierToken)) uses.Names.Add(token.ValueText);
        }

        void Deconstruction(DeconstructionInfo info, Ctx ctx)
        {
            uses.Use(info.Method, ctx);
            foreach (var n in info.Nested) Deconstruction(n, ctx);
        }
    }
}

/// <summary>
/// The read or write kind of a field or property reference
/// (after Roslyn's value usage and IDE0052): writes are assignment targets (also object initializers and <c>with</c>),
/// compound assignments and increments whose value is dropped, <c>out</c> arguments and deconstruction targets;
/// everything else reads (<c>ref</c>, <c>in</c>, <c>??=</c>, address-of, a receiver such as <c>_s.X = 1</c>).
/// </summary>
internal static class ValueUsage
{
    public static EdgeKind Of(IOperation reference) => reference is IFieldReferenceOperation or IPropertyReferenceOperation
        ? IsWrite(reference) ? EdgeKind.Write : EdgeKind.Read
        : EdgeKind.Other;

    static bool IsWrite(IOperation op) => op.Parent switch
    {
        ISimpleAssignmentOperation a => a.Target == op && !a.IsRef,
        ICompoundAssignmentOperation c => c.Target == op && Dropped(c),
        IIncrementOrDecrementOperation i => Dropped(i),
        IArgumentOperation arg => arg.Parameter?.RefKind == RefKind.Out,
        ITupleOperation t => IsDeconstructionTarget(t),
        _ => false,
    };

    /// <summary>The value of the operation is not used (an expression statement, also a for incrementor or a void lambda body).</summary>
    static bool Dropped(IOperation op) => op.Parent is IExpressionStatementOperation;

    static bool IsDeconstructionTarget(ITupleOperation tuple)
    {
        IOperation outer = tuple;
        while (outer.Parent is ITupleOperation parent) outer = parent;
        return outer.Parent is IDeconstructionAssignmentOperation d && d.Target == outer;
    }
}
