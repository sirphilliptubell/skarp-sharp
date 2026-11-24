// Pattern: auto-properties and a record with a primary constructor.
// Expected: no finding; auto-property lines (Coverlet reports them, Microsoft does not) count for the file, not a member.
// Seen: coverage producers differ on auto-properties and record lines.
namespace Mock.Lib;

public sealed class Customer
{
    public string Name { get; set; } = "";
    public int Orders { get; init; }
    public string Display => Orders > 0 ? $"{Name} ({Orders})" : Name;
}

public record Point(int X, int Y);
