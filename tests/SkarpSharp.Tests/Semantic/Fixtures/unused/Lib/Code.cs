namespace Fx.Unused;

// Fixture for the IDE0051 oracle comparison, test-only use and multi-targeting.
public class A1
{
    private int _a;
    private void Helper() { }
    private int Prop => 1;
    private A1(int x) { }
    public A1() { _b = 1; }
    private int _b;
    public void P() { Used(); _ = _b; }
    private void Used() { }
    private int Rec(int n) => n == 0 ? 0 : Rec(n - 1);
    [System.Obsolete] private void WithNeutral() { }
    [System.ComponentModel.Description("x")] private void WithAttribute() { }
    internal void OnlyTests() { }
    internal void Nowhere() { }
#if NET9_0
    public void Old() => ForOld();
#endif
    private void ForOld() { }
    internal void ViaOldFile() { }
}

internal class Unreferenced { }

public class Holder
{
    private class PrivateNested { }
}
