using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkarpSharp.Semantic;

/// <summary>
/// The symbol-level roots of the unused-code rules: the neutral attributes that do not make code used, the framework-scanned base types and interfaces
/// that make a type used, the member names the compiler calls by pattern, and the per-symbol checks (overrides,
/// interface implementations, attributes, entry points, event handlers, designer pairs, deserialization constructors).
/// Name roots (strings, inactive code, dynamic code), reflection roots (typeof, DynamicallyAccessedMembers) and
/// InternalsVisibleTo are decided over the whole solution in <see cref="UnusedCode"/>.
/// </summary>
public static class UnusedRoots
{
    /// <summary>
    /// Attributes that only the compiler, analyzers or the debugger read, never the program through
    /// reflection, so they do not make code used. The core list (Obsolete, SuppressMessage, ExcludeFromCodeCoverage,
    /// EditorBrowsable, Conditional, MethodImpl, the Caller* and nullable-analysis attributes, the debugger attributes,
    /// SkipLocalsInit, DebuggerDisplay) plus attributes of the same nature (platform, trimming and AOT annotations,
    /// contracts, overload priority). Full names; any other attribute makes code used.
    /// </summary>
    public static readonly IReadOnlySet<string> NeutralAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.ObsoleteAttribute", "System.CLSCompliantAttribute",
        "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute", "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute",
        "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute", "System.ComponentModel.EditorBrowsableAttribute",
        "System.Diagnostics.ConditionalAttribute", "System.Runtime.CompilerServices.MethodImplAttribute",
        "System.Runtime.CompilerServices.SkipLocalsInitAttribute", "System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute",
        "System.Runtime.CompilerServices.CallerMemberNameAttribute", "System.Runtime.CompilerServices.CallerFilePathAttribute",
        "System.Runtime.CompilerServices.CallerLineNumberAttribute", "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute",
        "System.Diagnostics.CodeAnalysis.AllowNullAttribute", "System.Diagnostics.CodeAnalysis.DisallowNullAttribute",
        "System.Diagnostics.CodeAnalysis.MaybeNullAttribute", "System.Diagnostics.CodeAnalysis.NotNullAttribute",
        "System.Diagnostics.CodeAnalysis.MaybeNullWhenAttribute", "System.Diagnostics.CodeAnalysis.NotNullWhenAttribute",
        "System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute", "System.Diagnostics.CodeAnalysis.MemberNotNullAttribute",
        "System.Diagnostics.CodeAnalysis.MemberNotNullWhenAttribute", "System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute",
        "System.Diagnostics.CodeAnalysis.DoesNotReturnIfAttribute", "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
        "System.Diagnostics.CodeAnalysis.StringSyntaxAttribute", "System.Diagnostics.CodeAnalysis.ConstantExpectedAttribute",
        "System.Diagnostics.CodeAnalysis.UnscopedRefAttribute", "System.Diagnostics.CodeAnalysis.ExperimentalAttribute",
        "System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute", "System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute",
        "System.Diagnostics.CodeAnalysis.RequiresAssemblyFilesAttribute",
        "System.Diagnostics.DebuggerHiddenAttribute", "System.Diagnostics.DebuggerStepThroughAttribute", "System.Diagnostics.DebuggerNonUserCodeAttribute",
        "System.Diagnostics.DebuggerBrowsableAttribute", "System.Diagnostics.DebuggerDisplayAttribute", "System.Diagnostics.DebuggerStepperBoundaryAttribute",
        "System.Diagnostics.StackTraceHiddenAttribute", "System.Diagnostics.Contracts.PureAttribute",
        "System.Runtime.Versioning.SupportedOSPlatformAttribute", "System.Runtime.Versioning.UnsupportedOSPlatformAttribute",
        "System.Runtime.Versioning.SupportedOSPlatformGuardAttribute", "System.Runtime.Versioning.UnsupportedOSPlatformGuardAttribute",
        "System.Runtime.Versioning.ObsoletedOSPlatformAttribute", "System.Runtime.Versioning.RequiresPreviewFeaturesAttribute",
        "System.Runtime.TargetedPatchingOptOutAttribute",
    };

    /// <summary>Attributes that mark generated code: such members and types are never reported (like generated files).</summary>
    static readonly IReadOnlySet<string> GeneratedAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.CodeDom.Compiler.GeneratedCodeAttribute", "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
    };

    /// <summary>
    /// Base types and interfaces whose implementations frameworks find by assembly scanning or by
    /// convention (namespace and name without generic arity). A type deriving from or implementing one of them is used;
    /// other base types and interfaces do not make a type used.
    /// </summary>
    public static readonly IReadOnlySet<string> FrameworkScanned = new HashSet<string>(StringComparer.Ordinal)
    {
        // ASP.NET Core and ASP.NET
        "Microsoft.AspNetCore.Mvc.ControllerBase", "Microsoft.AspNetCore.Mvc.ViewComponent", "Microsoft.AspNetCore.Mvc.RazorPages.PageModel",
        "Microsoft.AspNetCore.Mvc.RazorPages.Page", "Microsoft.AspNetCore.Mvc.Razor.RazorPage", "Microsoft.AspNetCore.Components.IComponent",
        "Microsoft.AspNetCore.SignalR.Hub", "Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper", "Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata",
        "Microsoft.AspNetCore.Hosting.IStartup", "Microsoft.AspNetCore.Hosting.IStartupFilter", "Microsoft.AspNetCore.Hosting.IHostingStartup",
        "Microsoft.AspNetCore.Authorization.IAuthorizationHandler", "Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention",
        "System.Web.Mvc.Controller", "System.Web.Http.ApiController", "System.Web.IHttpModule", "System.Web.IHttpHandler", "System.Web.HttpApplication",
        // Hosting, options, dependency-injection modules
        "Microsoft.Extensions.Hosting.IHostedService", "Microsoft.Extensions.Options.IConfigureOptions", "Microsoft.Extensions.Options.IPostConfigureOptions",
        "Microsoft.Extensions.Options.IValidateOptions", "Autofac.Module", "Autofac.Core.IModule", "AutoMapper.Profile",
        // Mediators, message handlers, validators, jobs
        "MediatR.IRequestHandler", "MediatR.INotificationHandler", "MediatR.IPipelineBehavior", "MediatR.IStreamRequestHandler",
        "MediatR.Pipeline.IRequestPreProcessor", "MediatR.Pipeline.IRequestPostProcessor", "MediatR.Pipeline.IRequestExceptionHandler",
        "MassTransit.IConsumer", "NServiceBus.IHandleMessages", "Rebus.Handlers.IHandleMessages", "Quartz.IJob", "FluentValidation.IValidator",
        // Entity Framework Core
        "Microsoft.EntityFrameworkCore.Migrations.Migration", "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot",
        "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration", "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory",
        "Microsoft.EntityFrameworkCore.Design.IDesignTimeServices",
        // Tools loaded by name: MSBuild tasks, PowerShell cmdlets, installers
        "Microsoft.Build.Framework.ITask", "System.Management.Automation.Cmdlet", "System.Configuration.Install.Installer",
    };

    /// <summary>Member names the compiler calls by pattern (foreach, await, using, deconstruction, collection initializers,
    /// index and range, fixed, interpolated string handlers); rooted by name.</summary>
    public static readonly IReadOnlySet<string> PatternNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetEnumerator", "MoveNext", "Current", "Dispose", "DisposeAsync", "GetAsyncEnumerator", "MoveNextAsync", "GetAwaiter", "IsCompleted",
        "GetResult", "OnCompleted", "UnsafeOnCompleted", "Deconstruct", "Count", "Length", "Slice", "GetPinnableReference", "AppendLiteral",
        "AppendFormatted",
    };

    public static string FullName(INamedTypeSymbol t) => t.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + MetadataPath(t) : MetadataPath(t);

    static string MetadataPath(INamedTypeSymbol t) => t.ContainingType is { } outer ? MetadataPath(outer) + "." + t.Name : t.Name;

    public static string AttributeName(AttributeData a) => a.AttributeClass is { } c ? FullName(c) : "";

    /// <summary>The first attribute that makes code used (not neutral), or null.</summary>
    public static string? LiveAttribute(ISymbol symbol)
    {
        foreach (var a in symbol.GetAttributes())
            if (a.AttributeClass is { } c && !NeutralAttributes.Contains(FullName(c))) return c.Name.EndsWith("Attribute", StringComparison.Ordinal) ? c.Name[..^9] : c.Name;
        return null;
    }

    public static bool IsGeneratedByAttribute(ISymbol symbol) => symbol.GetAttributes().Any(a => GeneratedAttributes.Contains(AttributeName(a)));

    /// <summary>A live attribute on the member, its accessors, or a containing type.</summary>
    public static string? AttributeRoot(ISymbol member)
    {
        if (LiveAttribute(member) is { } own) return $"attribute [{own}]";
        IEnumerable<ISymbol?> accessors = member switch
        {
            IPropertySymbol p => [p.GetMethod, p.SetMethod],
            IEventSymbol e => [e.AddMethod, e.RemoveMethod],
            _ => [],
        };
        foreach (var acc in accessors.OfType<ISymbol>())
            if (!acc.IsImplicitlyDeclared && LiveAttribute(acc) is { } a) return $"attribute [{a}] on an accessor";
        for (var t = member.ContainingType; t is not null; t = t.ContainingType)
            if (LiveAttribute(t) is { } c) return $"attribute [{c}] on containing type {t.Name}";
        return null;
    }

    /// <summary>The framework-scanned base type or interface the type derives from or implements, or null.</summary>
    public static string? FrameworkBase(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (FrameworkScanned.Contains(FullName(b.OriginalDefinition))) return FullName(b.OriginalDefinition);
        foreach (var i in type.AllInterfaces)
            if (FrameworkScanned.Contains(FullName(i.OriginalDefinition))) return FullName(i.OriginalDefinition);
        return null;
    }

    /// <summary>True for a method whose signature is (object, EventArgs or derived): WinForms, WPF and ASP.NET handlers.</summary>
    public static bool HasEventHandlerSignature(IMethodSymbol m)
    {
        if (m.Parameters.Length != 2 || m.Parameters[0].Type.SpecialType != SpecialType.System_Object) return false;
        for (var t = m.Parameters[1].Type as INamedTypeSymbol; t is not null; t = t.BaseType)
            if (t.Name == "EventArgs" && t.ContainingNamespace?.ToDisplayString() == "System") return true;
        return false;
    }

    static bool Implements(INamedTypeSymbol type, string fullName) => type.AllInterfaces.Any(i => FullName(i.OriginalDefinition) == fullName);

    /// <summary>The symbol-level root of a member candidate (null: none). Order: the strongest reason first.</summary>
    public static string? MemberRoot(ISymbol m, Compilation compilation) =>
        DeclarationRoot(m) ?? UnboundRoot(m, compilation) ?? PatternRoot(m);

    /// <summary>Roots that hold whatever the containing type: methods the runtime or a framework calls, attributes.</summary>
    static string? UnboundRoot(ISymbol m, Compilation compilation) =>
        (m is IMethodSymbol method ? MethodRoot(method, compilation) : null) ?? AttributeRoot(m);

    /// <summary>
    /// True when the member's root only holds while its containing type is live: overrides,
    /// virtual and abstract members, interface implementations and pattern members are reached through an instance of
    /// the type, so in a type nothing uses they keep nothing alive. Extern, partial, framework-called and attributed
    /// members stay roots whatever their type.
    /// </summary>
    public static bool IsTypeBound(ISymbol m, Compilation compilation, string? root)
    {
        if (root is null || IsUnbound(m, compilation)) return false;
        return IsDispatched(m) || PatternRoot(m) is not null;
    }

    static bool IsUnbound(ISymbol m, Compilation compilation) => m.IsExtern || IsPartial(m) || UnboundRoot(m, compilation) is not null;

    static bool IsDispatched(ISymbol m) => m.IsOverride || m.IsVirtual || m.IsAbstract || ImplementsInterfaceMember(m);

    /// <summary>Roots by declaration: overrides, virtual, abstract, extern, interface implementations, partial members.</summary>
    static string? DeclarationRoot(ISymbol m)
    {
        if (m.IsOverride) return "override";
        if (m.IsVirtual) return "virtual";
        if (m.IsAbstract) return "abstract";
        if (m.IsExtern) return "extern (P/Invoke)";
        if (ImplementsInterfaceMember(m)) return "interface implementation";
        return IsPartial(m) ? "partial member" : null;
    }

    /// <summary>Methods called by the runtime or a framework: entry points, deserialization constructors, event
    /// handlers, designer pairs.</summary>
    static string? MethodRoot(IMethodSymbol method, Compilation compilation)
    {
        if (IsEntryPoint(method, compilation)) return "entry point";
        if (method.MethodKind == MethodKind.Constructor && IsDeserializationConstructor(method)) return "deserialization constructor (ISerializable)";
        if (HasEventHandlerSignature(method)) return "event handler signature (object, EventArgs)";
        return IsDesignerPair(method) ? "designer pair (ShouldSerializeX or ResetX)" : null;
    }

    /// <summary>Members the compiler calls by pattern (names, collection initializer Add, indexers).</summary>
    static string? PatternRoot(ISymbol m)
    {
        if (m is IMethodSymbol or IPropertySymbol && PatternNames.Contains(m.Name)) return $"compiler pattern member ({m.Name})";
        if (m is IMethodSymbol { Name: "Add" } && Implements(m.ContainingType, "System.Collections.IEnumerable")) return "compiler pattern member (Add of a collection initializer)";
        return m is IPropertySymbol { IsIndexer: true } ? "compiler pattern member (indexer)" : null;
    }

    public static bool IsEntryPoint(IMethodSymbol m, Compilation compilation) =>
        (m.Name == "Main" && m.IsStatic) || SymbolEqualityComparer.Default.Equals(compilation.GetEntryPoint(CancellationToken.None), m);

    static bool ImplementsInterfaceMember(ISymbol m)
    {
        bool explicitImpl = m switch
        {
            IMethodSymbol ms => ms.ExplicitInterfaceImplementations.Length > 0,
            IPropertySymbol p => p.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol e => e.ExplicitInterfaceImplementations.Length > 0,
            _ => false,
        };
        if (explicitImpl) return true;
        if (m is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) return false;
        foreach (var i in m.ContainingType.AllInterfaces)
            foreach (var im in i.GetMembers(m.Name))
                if (SymbolEqualityComparer.Default.Equals(m.ContainingType.FindImplementationForInterfaceMember(im), m)) return true;
        return false;
    }

    static bool IsPartial(ISymbol m)
    {
        if (m is IMethodSymbol { IsPartialDefinition: true } or IMethodSymbol { PartialImplementationPart: not null } or IMethodSymbol { PartialDefinitionPart: not null })
            return true;
        if (m is IPropertySymbol { IsPartialDefinition: true } or IPropertySymbol { PartialImplementationPart: not null }) return true;
        foreach (var r in m.DeclaringSyntaxReferences)
            if (r.GetSyntax() is MemberDeclarationSyntax d && d.Modifiers.Any(x => x.ValueText == "partial")) return true;
        return false;
    }

    static bool IsDeserializationConstructor(IMethodSymbol c) =>
        c.Parameters.Length == 2 && c.Parameters[0].Type.Name == "SerializationInfo" && c.Parameters[1].Type.Name == "StreamingContext";

    static bool IsDesignerPair(IMethodSymbol m)
    {
        if (m.Parameters.Length != 0) return false;
        string? property = m.Name.StartsWith("ShouldSerialize", StringComparison.Ordinal) && m.ReturnType.SpecialType == SpecialType.System_Boolean ? m.Name[15..]
            : m.Name.StartsWith("Reset", StringComparison.Ordinal) && m.ReturnsVoid ? m.Name[5..] : null;
        return property is { Length: > 0 } && m.ContainingType.GetMembers(property).Any(x => x is IPropertySymbol);
    }
}
