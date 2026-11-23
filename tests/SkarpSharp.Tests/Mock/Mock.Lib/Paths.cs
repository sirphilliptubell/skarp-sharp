// Pattern: complexity inside a local function, run by the tests through its host.
// Expected: no untested-complex-method finding (local function lines belong to the host member).
// Seen: coverage reports list local functions as methods of their own.
namespace Mock.Lib;

public static class Paths
{
    public static string Normalize(string path)
    {
        return string.Join("/", path.Split('/', '\\').Where(Keep));

        static bool Keep(string segment)
        {
            if (segment.Length == 0) return false;
            if (segment == ".") return false;
            if (segment.StartsWith('~')) return false;
            if (segment.EndsWith(".tmp", StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
