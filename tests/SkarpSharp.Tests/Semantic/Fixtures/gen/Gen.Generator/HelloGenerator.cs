using Microsoft.CodeAnalysis;
namespace Gen.Generator;
[Generator]
public sealed class HelloGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterPostInitializationOutput(c => c.AddSource("Hello.g.cs",
            "namespace Generated { public static class Hello { public static string World() => \"hi\"; } public sealed class Widget { public int Size { get; set; } } }"));
}
