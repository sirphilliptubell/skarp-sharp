// Pattern: a partial type split over two files (this part is not tested).
// Expected: untested-complex-method, notice, on Validate (in this file only).
// Seen: partial classes spread over several files are not measured per file by coverage tools.
namespace Mock.Lib;

public sealed partial class Order
{
    public string? Validate()
    {
        if (Lines.Count == 0) return "empty";
        if (Lines.Count > 100) return "too many lines";
        foreach (var l in Lines)
        {
            if (l < 0) return "negative line";
            if (l > 10_000) return "line too large";
        }
        return Total() > 50_000 ? "total too large" : null;
    }
}
