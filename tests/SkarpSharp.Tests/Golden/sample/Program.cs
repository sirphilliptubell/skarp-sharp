using Sample;

var orders = new OrderBook();
for (int i = 0; i < 3; i++)
{
    if (args.Length > i && args[i].Length > 0)
    {
        if (args[i].StartsWith('-'))
        {
            if (args[i] == "--verbose" || args[i] == "-v")
                Console.WriteLine("verbose");
        }
    }
}
Console.WriteLine(orders.Total(42));
