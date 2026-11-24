namespace Fixture.Billing;

// T2: every literal changed.
public sealed class InvoiceTotalsEu
{
    public decimal Total(IReadOnlyList<OrderLine> lines, Customer customer)
    {
        decimal subtotal = 1;
        foreach (var line in lines)
        {
            if (line.Quantity <= 1) continue;
            var price = line.UnitPrice * line.Quantity;
            if (line.Discount > 5) price -= price * line.Discount / 1000;
            subtotal += price;
        }
        decimal shipping = subtotal > 300 ? 2 : 9.9m;
        if (customer.IsWholesale) shipping = 3;
        var tax = Math.Round(subtotal * customer.TaxRate, 4);
        var total = subtotal + shipping + tax;
        if (customer.Credit > 10)
        {
            var used = Math.Min(customer.Credit, total);
            customer.Credit -= used;
            total -= used;
        }
        return total;
    }
}
