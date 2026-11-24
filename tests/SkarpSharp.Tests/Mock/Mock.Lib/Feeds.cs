// Pattern: async and iterator methods (compiled into state machine classes; the reports name MoveNext).
// Expected: CountAsync (tested) no finding; Evens (an iterator no test runs) untested-complex-method, notice.
// Seen: state machine lines map to the declaring member.
namespace Mock.Lib;

public static class Feeds
{
    public static async Task<int> CountAsync(IEnumerable<int> items, int limit)
    {
        await Task.Yield();
        int count = 0;
        foreach (var i in items)
        {
            if (i < 0) continue;
            if (i > limit) break;
            if (i % 2 == 0 && i % 3 == 0) count += 2;
            else count++;
        }
        return count;
    }

    public static IEnumerable<int> Evens(IEnumerable<int> source, int limit)
    {
        foreach (var i in source)
        {
            if (i > limit) yield break;
            if (i < 0 || i % 2 != 0) continue;
            if (i == 0) yield return 0;
            else yield return i;
        }
    }
}
