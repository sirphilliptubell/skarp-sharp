namespace Fixture.Billing;

// T3 mixed: a statement deleted, one inserted, literals changed and locals renamed.
public sealed class InvoiceTotalsMixed
{
    public decimal Total(IReadOnlyList<OrderLine> items, Customer customer)
    {
        decimal net = 0;
        foreach (var item in items)
        {
            if (item.Quantity <= 0) continue;
            var price = item.UnitPrice * item.Quantity;
            if (item.Discount > 0) price -= price * item.Discount / 100;
            net += price;
        }
        decimal shipping = net > 400 ? 0 : 7.5m;
        var tax = Math.Round(net * customer.TaxRate, 2);
        var total = net + shipping + tax;
        if (customer.Credit > 0)
        {
            var used = Math.Min(customer.Credit, total);
            customer.Credit -= used;
            Audit.Log(customer.Id, used);
            total -= used;
        }
        return total;
    }
}
