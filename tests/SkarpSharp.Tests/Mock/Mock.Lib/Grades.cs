// Pattern: a complex method that the tests run.
// Expected: no untested-complex-method finding (covered lines in the member).
// Seen: well-tested complex methods in real repositories.
namespace Mock.Lib;

public static class Grades
{
    public static string Letter(int score)
    {
        if (score < 0 || score > 100) throw new ArgumentOutOfRangeException(nameof(score));
        if (score >= 90) return "A";
        if (score >= 80) return "B";
        if (score >= 70) return "C";
        if (score >= 60) return "D";
        return "F";
    }
}
