namespace SkarpSharp.Findings;

/// <summary>The long explanations of the unused-code rules (<c>skarp explain</c>).</summary>
internal static class UnusedCodeText
{
    const string Common = """
        Only certain findings: private and internal members and types; public and protected ones are never
        reported (code outside the solution may use them). Test projects are compiled and walked as users but never
        reported; code they alone use gets the label used-only-by-tests instead. Nothing counts as unused when a root
        applies: entry points (Main, top-level statements), overrides, virtual, abstract and extern members, interface
        implementations, members the compiler calls by pattern (GetEnumerator, Dispose, GetAwaiter, Deconstruct, Add of
        a collection, indexers and others), operators, conversions, static constructors, finalizers, partial members,
        event handlers (object, EventArgs), deserialization constructors and ShouldSerializeX/ResetX pairs, anything with
        an attribute on itself or a containing type (except neutral ones such as Obsolete, SuppressMessage, Conditional,
        DebuggerDisplay, MethodImpl and the nullable annotations), types derived from framework-scanned bases (ASP.NET
        controllers, hosted services, MediatR handlers, EF migrations and others), members of a type used in typeof(T)
        or with [DynamicallyAccessedMembers], fields of structs with native layout use, a name that appears in a string
        literal, dynamic or invalid code or inactive #if code, InternalsVisibleTo to an assembly outside the solution (all
        internal members of that assembly), and the allowlist (unusedCode.allow in .skarp.json: documentation ids such as
        M:Ns.Type.Member or names such as Ns.Type.Member, with * and ?). A type is kept when one of its extension
        methods is kept by its name. A use inside the member itself (recursion) or inside the type itself does not count;
        a documentation cref does not count.

        Code used only by unused code: uses count only when they come from live code: public and
        protected code, generated code, roots, code that is not evaluated, and whatever live code uses in turn. Code
        that only unused code uses is reported too, with "used only by unused code: X (and N more)", and members that
        call each other but nothing else uses are reported together. Overrides, interface implementations and pattern
        members inside a type nothing uses keep nothing alive; framework-scanned types, attributes and names stay roots.
        The semantic JSON gives usedOnlyBy (the unused users) and depth (0: nothing references it; 1: only code at
        depth 0 does; and so on). Delete from depth 0 down; the deeper findings disappear with their users.

        Multi-targeted projects: by default only the highest target framework is analyzed and unused code is
        reported from it; the finding says so, for example "(net10.0 only; --all-targets for all)". Uses that only other
        targets have count through names: names in inactive #if code, names in files that only other targets compile
        (conditional Compile items), and, for an extension method in a file another target compiles, a call in the
        analyzed target to a framework method of the same name (another target may bind it to the extension, as with
        polyfills). With --all-targets on skarp analyze or baseline every target is analyzed and the
        result is certain.

        Not evaluated (no finding, counted in the report): types with unresolved symbols or invalid code (their
        references still count), projects with XAML or Razor files whose generated C# is missing, projects with source
        generators under --no-generators, and internal members visible to a friend project that gave no compiler
        arguments or was not analyzed (--target on skarp analyze or baseline).

        Why it matters: dead code still costs reading, reviewing, migrating and testing time, and it misleads readers about
        what the program does.

        How to improve: delete it, together with the code only it uses (the findings that name it in usedOnlyBy). If it
        is used through a route skarp cannot see (reflection by computed names, external callers), add an attribute such
        as [UsedImplicitly] or an allowlist entry. Notice level; new findings trip the gate only when it is configured
        for them (--gate-on unused-member:notice on skarp analyze, or "gate": { "on": [...] } in .skarp.json); code used
        only by unused code counts as unused too, so a gate on notices can show new findings in code that did not change. Needs the semantic tier.
        """;

    public const string Member = """
        What it measures: a private or internal method, constructor with parameters, field, constant, property or event
        that no code in the solution references, in any analyzed target framework. A reference is any call, object
        creation, read or write, method group, nameof, attribute argument, operator, pattern call (foreach, await,
        deconstruction) or signature that binds to it. Members inside an unused type are reported through the type.
        A field or auto-property that live code writes but never reads is reported as unread-member instead.

        """ + Common;

    public const string Type = """
        What it measures: a private or internal class, struct, record, interface, enum or delegate that no code in the
        solution references from outside the type itself: no creation, static member access, base list, signature,
        cast, pattern, typeof, generic argument or attribute use. A type that implements an interface or derives from a
        class is reported too, unless the base is a framework-scanned one: dependency-injection containers
        that register implementations by scanning assemblies are invisible to skarp, so check before deleting.

        """ + Common;

    public const string TestOnly = """
        What it measures: a private or internal member or type of a production project that only test projects (or
        files using a test framework) reference, directly or through other code only tests use ("used only by tests
        (via X)"). Also a field or auto-property production code writes and only tests read, directly or through
        such code ("written by production code but read only by tests"). It is a label, not a defect: the code may be a test hook, or it may be dead production code that
        only its tests keep alive. It is never gated (--gate-on cannot select it).

        """ + Common;

    public const string Unread = """
        What it measures: a private or internal field or auto-property that live code writes but never
        reads, in any analyzed target framework. Writes: assignments (also in object initializers and with
        expressions), a non-constant initializer, out arguments, deconstruction targets, and compound assignments or
        ++ and -- whose value is not used. Everything else reads: ref and in arguments (Interlocked, Volatile), nameof,
        ??=, address-of, a member access through it (_list.Add(x), _struct.X = 1). Reads from unused code do not count;
        a member only tests read gets the used-only-by-tests label instead. After Roslyn IDE0052, widened to
        internal members.

        Certain only: not reported are properties with bodies, members of records (their synthesized Equals and ToString
        read them), instance members of structs, members of [StructLayout] or [InlineArray] types, and fields or
        properties of delegate, IDisposable, GCHandle, object or dynamic type, or of a type parameter that can be any
        type (no constraint type, not struct or unmanaged), as writing them keeps an object alive; the summary lists
        them as rooted with that reason. The roots of unused-member apply (attributes, names in strings, typeof and
        [DynamicallyAccessedMembers], a type passed to a [RequiresUnreferencedCode] method such as a reflection-based
        serializer, InternalsVisibleTo outside the solution, the allowlist).

        Why it matters: a value nobody reads is dead state: it costs memory, readers look for its purpose, and the code
        computing it may be dead too.

        How to improve: check each write for side effects (a call that computes the value, an object kept alive on
        purpose), then remove the member and its writes; keep the side effects that matter. If something reads it
        through a route skarp cannot see, add an attribute or an allowlist entry (unusedCode.allow). Notice level;
        gateable with --gate-on unread-member:notice on skarp analyze, or "gate": { "on": [...] } in
        .skarp.json. Needs the semantic tier.
        """;
}
