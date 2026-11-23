namespace Fixture.Export;

// T3 mixed: renamed locals, changed literals, a check removed and a statement added.
public static class CsvRowsMixed
{
    public static List<Row> Parse(TextReader reader, char separator, ImportLog log)
    {
        var result = new List<Row>();
        int lineNo = 0;
        string? text;
        while ((text = reader.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var cells = text.Split(separator);
            if (cells.Length < 2)
            {
                log.Warn(lineNo, "short line " + cells.Length);
                continue;
            }
            if (!int.TryParse(cells[1], out var amount))
            {
                log.Warn(lineNo, "amount is not a number: " + cells[1]);
                continue;
            }
            var name = cells[0].Trim();
            result.Add(new Row(name, amount, cells.Length > 2 ? cells[2].Trim() : ""));
        }
        log.Info("read " + result.Count + " rows");
        return result;
    }
}
