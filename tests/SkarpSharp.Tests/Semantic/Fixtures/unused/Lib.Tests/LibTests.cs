namespace Fx.Unused.Tests;

public class LibTests
{
    public void T() => new A1().OnlyTests();
    private void NeverReported() { }
    public int R(UnreadCases c) => c.ReadByTests;
}
