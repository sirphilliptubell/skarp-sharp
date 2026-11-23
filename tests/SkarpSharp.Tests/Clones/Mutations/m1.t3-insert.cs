namespace Fixture.Billing;

// T3: one statement inserted.
public sealed class InvoiceTotalsAudited
{
    public decimal Total(IReadOnlyList<OrderLine> lines, Customer customer)
    {
        decimal subtotal = 0;
        foreach (var line in lines)
        {
            if (line.Quantity <= 0) continue;
            var price = line.UnitPrice * line.Quantity;
            if (line.Discount > 0) price -= price * line.Discount / 100;
            subtotal += price;
        }
        decimal shipping = subtotal > 250 ? 0 : 12.5m;
        if (customer.IsWholesale) shipping = 0;
        Audit.Log(customer.Id, subtotal);
        var tax = Math.Round(subtotal * customer.TaxRate, 2);
        var total = subtotal + shipping + tax;
        if (customer.Credit > 0)
        {
            var used = Math.Min(customer.Credit, total);
            customer.Credit -= used;
            total -= used;
        }
        return total;
    }
}
