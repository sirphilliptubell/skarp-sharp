namespace Fixture.Billing;

// T2: method, parameters and locals renamed consistently.
public sealed class InvoiceSums
{
    public decimal Sum(IReadOnlyList<OrderLine> items, Customer buyer)
    {
        decimal net = 0;
        foreach (var item in items)
        {
            if (item.Quantity <= 0) continue;
            var amount = item.UnitPrice * item.Quantity;
            if (item.Discount > 0) amount -= amount * item.Discount / 100;
            net += amount;
        }
        decimal freight = net > 250 ? 0 : 12.5m;
        if (buyer.IsWholesale) freight = 0;
        var vat = Math.Round(net * buyer.TaxRate, 2);
        var gross = net + freight + vat;
        if (buyer.Credit > 0)
        {
            var applied = Math.Min(buyer.Credit, gross);
            buyer.Credit -= applied;
            gross -= applied;
        }
        return gross;
    }
}
