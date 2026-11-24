namespace Fixture.Export;

// T2: every literal changed.
public static class CsvRowsStrict
{
    public static List<Row> Parse(TextReader reader, char separator, ImportLog log)
    {
        var rows = new List<Row>();
        int number = 1;
        string? text;
        while ((text = reader.ReadLine()) != null)
        {
            number++;
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith(';')) continue;
            var cells = text.Split(separator);
            if (cells.Length < 4)
            {
                log.Warn(number, "too few cells: " + cells.Length);
                continue;
            }
            if (!int.TryParse(cells[2], out var amount))
            {
                log.Warn(number, "bad amount " + cells[2]);
                continue;
            }
            rows.Add(new Row(cells[1].Trim(), amount, cells[3].Trim()));
        }
        log.Info("rows: " + rows.Count + ".");
        return rows;
    }
}
