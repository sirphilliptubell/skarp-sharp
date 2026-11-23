namespace Fx.Err;
public class UsesMissing { public MissingType? Field; public int M() => Helper.Compute(1); }   // CS0246, CS0103
public class TypeError { public int N() { int x = "text"; return x; } }                       // CS0029 (not an unresolved symbol)
public class Clean { public Order Make() => new Order(); }
