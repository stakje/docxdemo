using System.Data;
using System.Text;

namespace DocVista.Rendering;

public sealed record TableDocument(DataTable Table, bool WasTruncated, string Summary);

public static class CsvDocument
{
    private const int MaximumRows = 100_000;
    private const int MaximumCells = 2_000_000;

    public static async Task<TableDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var encoding = DetectEncoding(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        var sample = new List<string>();
        for (var i = 0; i < 12 && await reader.ReadLineAsync(cancellationToken) is { } line; i++) sample.Add(line);

        var delimiter = DetectDelimiter(sample);
        reader.BaseStream.Position = 0;
        reader.DiscardBufferedData();

        const int probeRows = 256;
        var bufferedRows = new List<string[]>(probeRows + 1);
        for (var index = 0; index <= probeRows; index++)
        {
            var record = await ReadRecordAsync(reader, cancellationToken);
            if (record is null) break;
            bufferedRows.Add(ParseLine(record, delimiter));
        }

        if (bufferedRows.Count == 0) return new TableDocument(new DataTable(), false, "空 CSV 文件");
        var width = Math.Min(bufferedRows.Max(row => row.Length), 512);
        var table = new DataTable(Path.GetFileName(path));
        var header = bufferedRows[0];
        AddColumns(table, header, width);

        var rowsRead = 0;
        var truncated = false;
        table.BeginLoadData();
        try
        {
            foreach (var values in bufferedRows.Skip(1))
            {
                AddRow(table, values);
                rowsRead++;
            }

            while (rowsRead < MaximumRows && (long)table.Columns.Count * (rowsRead + 1) <= MaximumCells)
            {
                var record = await ReadRecordAsync(reader, cancellationToken);
                if (record is null) break;
                var values = ParseLine(record, delimiter);
                var requiredWidth = Math.Min(values.Length, 512);
                if ((long)Math.Max(requiredWidth, table.Columns.Count) * (rowsRead + 1) > MaximumCells)
                {
                    truncated = true;
                    break;
                }
                if (requiredWidth > table.Columns.Count) AddColumns(table, header, requiredWidth);
                AddRow(table, values);
                rowsRead++;
            }

            if (!truncated && (rowsRead >= MaximumRows || (long)table.Columns.Count * (rowsRead + 1) > MaximumCells))
                truncated = await ReadRecordAsync(reader, cancellationToken) is not null;
        }
        finally { table.EndLoadData(); }

        return new TableDocument(table, truncated, $"{table.Rows.Count:N0} 行 · {table.Columns.Count:N0} 列 · {EncodingLabel(encoding)}");
    }

    private static void AddColumns(DataTable table, IReadOnlyList<string> header, int requiredWidth)
    {
        while (table.Columns.Count < requiredWidth)
        {
            var index = table.Columns.Count;
            var proposed = index < header.Count && !string.IsNullOrWhiteSpace(header[index]) ? header[index].Trim() : $"列 {index + 1}";
            var name = proposed;
            var suffix = 2;
            while (table.Columns.Contains(name)) name = $"{proposed} ({suffix++})";
            table.Columns.Add(name, typeof(string));
        }
    }

    private static void AddRow(DataTable table, IReadOnlyList<string> values)
    {
        var row = table.NewRow();
        for (var index = 0; index < Math.Min(table.Columns.Count, values.Count); index++) row[index] = values[index];
        table.Rows.Add(row);
    }

    private static async Task<string?> ReadRecordAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var record = new StringBuilder();
        var quoted = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (record.Length > 0) record.AppendLine();
            record.Append(line);
            UpdateQuoteState(line, ref quoted);
            if (!quoted) return record.ToString();
        }
        return record.Length == 0 ? null : record.ToString();
    }

    private static void UpdateQuoteState(string text, ref bool quoted)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"') continue;
            if (quoted && index + 1 < text.Length && text[index + 1] == '"') index++;
            else quoted = !quoted;
        }
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = new byte[4];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var count = stream.Read(bytes, 0, bytes.Length);
        if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

        try
        {
            stream.Position = 0;
            var probe = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
            var read = stream.Read(probe, 0, probe.Length);
            _ = new UTF8Encoding(false, true).GetString(probe, 0, read);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
    }

    private static char DetectDelimiter(IEnumerable<string> lines)
    {
        var candidates = new[] { ',', '\t', ';', '|' };
        return candidates
            .Select(candidate => new { Candidate = candidate, Score = lines.Sum(line => CountOutsideQuotes(line, candidate)) })
            .OrderByDescending(result => result.Score)
            .First().Candidate;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var quoted = false;
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted && line[i] == delimiter) count++;
        }
        return count;
    }

    private static string[] ParseLine(string line, char delimiter)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }
        values.Add(value.ToString());
        return values.ToArray();
    }

    private static string EncodingLabel(Encoding encoding) => encoding.CodePage == 936 ? "GBK" : encoding.WebName.ToUpperInvariant();
}
