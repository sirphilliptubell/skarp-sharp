namespace Lib.Billing;

public sealed class Invoice
{
    public Lib.Orders.Order? Order { get; set; }
    public decimal Amount => Order?.Total ?? 0;
}
