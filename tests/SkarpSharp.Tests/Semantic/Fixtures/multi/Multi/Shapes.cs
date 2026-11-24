namespace Fx.Multi;
public class Shape { public double Area() => 0; }
public class Circle : Shape { public double Radius { get; set; } }
public class Canvas { public List<Shape> Shapes { get; } = new(); public double Total() => Shapes.Sum(s => s.Area()); }
