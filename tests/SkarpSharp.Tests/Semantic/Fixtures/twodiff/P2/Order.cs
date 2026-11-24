namespace Fx.P2;
public class Order { public List<Line> Lines { get; } = new(); public decimal Total() => Lines.Sum(l => l.Price * l.Qty); }
public record Line(string Sku, decimal Price, int Qty);
public class Printer { public string Print(Order o) => string.Join(",", o.Lines.Select(l => l.Sku)) + o.Total(); }
