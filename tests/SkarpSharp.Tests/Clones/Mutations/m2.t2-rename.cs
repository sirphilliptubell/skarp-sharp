namespace Fixture.Export;

// T2: consistent rename of the method, parameters and locals.
public static class TsvRecords
{
    public static List<Row> Read(TextReader input, char delimiter, ImportLog journal)
    {
        var records = new List<Row>();
        int lineNo = 0;
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split(delimiter);
            if (fields.Length < 3)
            {
                journal.Warn(lineNo, "expected 3 cells, found " + fields.Length);
                continue;
            }
            if (!int.TryParse(fields[1], out var value))
            {
                journal.Warn(lineNo, "amount is not a number: " + fields[1]);
                continue;
            }
            records.Add(new Row(fields[0].Trim(), value, fields[2].Trim()));
        }
        journal.Info("read " + records.Count + " rows");
        return records;
    }
}
