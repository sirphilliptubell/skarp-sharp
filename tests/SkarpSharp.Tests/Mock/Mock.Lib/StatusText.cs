// Pattern: an expression-bodied member whose body is a multi-line switch expression.
// Expected: no finding, and no staleness warning: producers report every line of a multi-line sequence point.
// Seen: staleness checks must count multi-line statements.
namespace Mock.Lib;

public static class StatusText
{
    public static string Describe(int code) =>
        code switch
        {
            200 => "ok",
            301 or 302 => "moved",
            404 => "missing",
            >= 500 and < 600 => "server error",
            < 0 => "invalid",
            _ => "other",
        };
}
