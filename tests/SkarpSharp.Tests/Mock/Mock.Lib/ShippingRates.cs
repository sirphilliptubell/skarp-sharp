// Pattern: untested complex method (a member with many branches that no test runs).
// Expected: untested-complex-method, notice (cyclomatic 6 or more, 0 covered lines).
// Seen: untested complex methods in real repositories.
namespace Mock.Lib;

public static class ShippingRates
{
    public static decimal Rate(string zone, decimal weight, bool express)
    {
        if (weight <= 0) return 0;
        decimal rate;
        if (zone == "north") rate = 4.5m;
        else if (zone == "south") rate = 5.25m;
        else if (zone == "east") rate = 6m;
        else rate = 8m;
        if (express) rate *= 2;
        if (weight > 20) rate += 10;
        return rate * weight;
    }
}
