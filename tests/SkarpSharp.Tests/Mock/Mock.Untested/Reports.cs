// Pattern: a project no test references.
// Expected: no finding (no data is not untested); the file and the project read "no data", never 0%.
// Seen: projects with no data in real repositories: samples, benchmarks and CLI shells.
namespace Mock.Untested;

public static class Reports
{
    public static string Band(int value)
    {
        if (value < 0) return "negative";
        if (value == 0) return "zero";
        if (value < 10) return "small";
        if (value < 100) return "medium";
        if (value < 1000) return "large";
        return "huge";
    }
}
