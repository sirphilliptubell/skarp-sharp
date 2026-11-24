// Pattern: a file linked into two projects compiled with different symbols (MOCK_LIB in Mock.Lib, UNTESTED_COPY in
// Mock.Untested); the tests run the Mock.Lib copy, whose branch skarp parses as inactive.
// Expected: no finding; an inactive-code warning names this file; the member's facts carry inactiveRegions.
// Seen: Open-XML-SDK links files from src/common/ into its generator project.
namespace Mock.Shared;

public static class Linked
{
    public static string Origin(int n)
    {
#if MOCK_LIB
        if (n > 100) return "lib large";
        if (n > 0) return "lib positive";
        return "lib";
#else
        if (n > 100) return "copy large";
        if (n > 0) return "copy positive";
        return "copy";
#endif
    }
}
