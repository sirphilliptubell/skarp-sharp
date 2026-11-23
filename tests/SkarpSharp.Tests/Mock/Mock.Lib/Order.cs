// Pattern: a partial type split over two files (this part is tested).
// Expected: no finding here; Order.Validation.cs holds the untested part.
// Seen: partial classes spread over several files are not measured per file by coverage tools.
namespace Mock.Lib;

public sealed partial class Order
{
    public List<decimal> Lines { get; } = [];

    public decimal Total()
    {
        decimal sum = 0;
        foreach (var l in Lines) sum += l;
        return sum;
    }
}
