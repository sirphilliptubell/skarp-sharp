// JIT = AOT fixture (tools/ci/jit-aot-compare.sh): Lib.Orders and Lib.Billing depend on each other, a
// namespace cycle of two, since skarp's own code no longer has one.
namespace Lib.Orders;

public sealed class Order
{
    public Lib.Billing.Invoice? Invoice { get; set; }
    public decimal Total { get; set; }
}
