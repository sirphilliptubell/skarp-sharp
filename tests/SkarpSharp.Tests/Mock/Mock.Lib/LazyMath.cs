// Pattern: a lambda created but never invoked (Microsoft reports its line twice: covered under the member, 0 under <>c).
// Expected: no finding and no staleness warning (a line is covered when any entry covers it).
// Seen: in real code, an async helper lambda that is created but never invoked.
namespace Mock.Lib;

public static class LazyMath
{
    public static int Prepare(int x)
    {
        Func<int> cube = () => x * x * x;
        return x + 1;
    }
}
