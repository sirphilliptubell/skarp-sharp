// Pattern: tested code inside an #if block (active in the build, inactive for skarp, which parses without symbols).
// Expected: no finding; an inactive-code warning names this file; the member's facts carry inactiveRegions.
// Seen: in real libraries, a large share of members sit in #if blocks that are invisible without the build's symbols.
namespace Mock.Lib;

public static class Features
{
    public static int Flags(int input)
    {
        int flags = 0;
#if NET
        if (input > 0) flags |= 1;
        if (input > 10) flags |= 2;
        if (input > 100) flags |= 4;
        if (input % 2 == 0) flags |= 8;
        if (input % 5 == 0) flags |= 16;
#endif
        return flags;
    }
}
