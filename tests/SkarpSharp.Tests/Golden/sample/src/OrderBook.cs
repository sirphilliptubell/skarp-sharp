namespace Sample;

public record OrderLine(string Sku, int Quantity, decimal Price, decimal Discount, string Currency, bool Taxable);

public class OrderBook
{
    readonly List<OrderLine> _lines = [];
    readonly Func<OrderLine, decimal> _net = l => l.Quantity > 0 && l.Price > 0 && !l.Taxable ? l.Price * l.Quantity : 0;

    public decimal Total(int customer)
    {
        decimal total = 0;
        foreach (var line in _lines)
        {
            if (line.Quantity > 0)
            {
                if (line.Taxable)
                {
                    total += line.Price * line.Quantity * 1.25m;
                }
                else
                {
                    total += _net(line);
                }
            }
        }
        if (customer > 1000 && total > 500 || customer == 7)
            total -= 15;
        return total;
    }

    public decimal Total(string currency) => _lines.Where(l => l.Currency == currency).Sum(l => l.Price);

    public void Add(string sku, int quantity, decimal price, decimal discount, string currency, bool taxable, bool merge, string note)
    {
        try
        {
            _lines.Add(new OrderLine(sku, quantity, price, discount, currency, taxable));
        }
        catch
        {
        }
    }

    public string Describe(int code)
    {
        switch (code)
        {
            case 1: return "one";
            case 2: return "two";
            case 3: return "three";
            case 4: return "four";
            case 5: return "five";
            case 6: return "six";
            case 7: return "seven";
            default:
                var parts = new List<string>();
                for (int i = 0; i < code; i++)
                {
                    if (i % 2 == 0)
                    {
                        if (i % 3 == 0) parts.Add("fizz");
                        else parts.Add("even");
                    }
                    else parts.Add("odd");
                }
                return string.Join(",", parts);
        }
    }
}
