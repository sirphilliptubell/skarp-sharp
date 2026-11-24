namespace Gen.App;
public class UsesGenerated { public string Greet() => Generated.Hello.World(); public Generated.Widget Make() => new() { Size = 3 }; }
public class Plain { public int Twice(int x) => x * 2; }
