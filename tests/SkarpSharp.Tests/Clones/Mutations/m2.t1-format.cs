namespace Fixture.Export;

// T1: reformatted, with comments.
public static class CsvRowsCopy
{
    public static List<Row> Parse(TextReader reader, char separator, ImportLog log)
    {
        var rows = new List<Row>();
        int number = 0;
        string? text;
        // one row per line
        while ((text = reader.ReadLine()) != null) {
            number++;
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith('#'))
                continue;
            var cells = text.Split(separator);
            if (cells.Length < 3) {
                log.Warn(number, "expected 3 cells, found " + cells.Length);
                continue;
            }
            if (!int.TryParse(cells[1], out var amount)) {
                log.Warn(number, "amount is not a number: " + cells[1]);
                continue;
            }
            rows.Add(new Row(cells[0].Trim(), amount, cells[2].Trim()));
        }
        log.Info("read " + rows.Count + " rows");
        return rows;
    }
}
