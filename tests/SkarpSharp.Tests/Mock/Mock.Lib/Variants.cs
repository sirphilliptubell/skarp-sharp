// Pattern: code under #if symbols. skarp parses the planned symbol variants of this file and
// merges them per member.
// Expected: Platform.Name for net48 and for net8.0+ are variant-only members (keys after the base members); the
// MOCK_LIB/#else twins are two members; Exact.Six comes from an extra framework variant (NET6_0); DevOnly needs a symbol no
// project file mentions (filtered: no variant, no member); Never is an impossible branch (two frameworks), counted and
// never reported; Tagged has an attribute under #if and is one member (no phantom twin); Score is complex only with
// MOCK_LIB on (merged value, a notice that names the variant).
// Seen: framework chains, #if/#else twins and debug switches in real libraries.
namespace Mock.Lib;

public static class Platform
{
#if NET48
    public static string Name() => "framework";
#elif NET8_0_OR_GREATER
    public static string Name() => "modern";
#endif

#if DEBUG
    public static string Build() => "debug";
#endif
}

public static class Twins
{
#if MOCK_LIB
    public static int Pick(int x) => x > 0 ? 1 : 0;
#else
    public static int Pick(int x) => x < 0 ? -1 : 0;
#endif
}

public static class Exact
{
#if NET9_0_OR_GREATER
    public static int Nine() => 9;
#elif NET7_0_OR_GREATER
    public static int Seven() => 7;
#endif
#if NET6_0
    public static int Six() => 6;
#endif
#if MOCK_DEV_TRACE
    public static int DevOnly() => -1;
#endif
#if NET20 && NET8_0
    public static int Never() => 0;
#endif
}

public static class Tagged
{
#if DEBUG
    [System.Diagnostics.DebuggerStepThrough]
#endif
    public static int Run(int x) => x + 1;

    public static int Score(int a, int b)
    {
        int s = 0;
#if MOCK_LIB
        for (int i = 0; i < a; i++)
        {
            if (i % 2 == 0)
            {
                if (b > i) s++;
                else if (b == i) s += 2;
                else s--;
            }
            else if (i % 3 == 0 && b > 0)
            {
                while (s > 100)
                {
                    if (s % 7 == 0) break;
                    s /= 2;
                }
            }
        }
        if (a > b && b > 0 || a < 0) s++;
#endif
        return s;
    }
}
