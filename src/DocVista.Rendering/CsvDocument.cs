using System.Data;
using System.Text;

namespace DocVista.Rendering;

public sealed record TableDocument(DataTable Table, bool WasTruncated, string Summary);

public static class CsvDocument
{
    private const int MaximumRows = 100_000;

    public static async Task<TableDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var encoding = DetectEncoding(path);
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        var sample = new List<string>();
        for (var i = 0; i < 12 && await reader.ReadLineAsync(cancellationToken) is { } line; i++) sample.Add(line);

        var delimiter = DetectDelimiter(sample);
        reader.BaseStream.Position = 0;
        reader.DiscardBufferedData();

        var rows = new List<string[]>();
        string? current;
        var logical = new StringBuilder();
        while ((current = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (logical.Length > 0) logical.AppendLine();
            logical.Append(current);
            if (HasOpenQuote(logical)) continue;

            rows.Add(ParseLine(logical.ToString(), delimiter));
            logical.Clear();
            if (rows.Count > MaximumRows) break;
        }

        if (rows.Count == 0) return new TableDocument(new DataTable(), false, "空 CSV 文件");
        var width = Math.Min(rows.Max(row => row.Length), 512);
        var table = new DataTable(Path.GetFileName(path));
        var header = rows[0];
        for (var i = 0; i < width; i++)
        {
            var proposed = i < header.Length && !string.IsNullOrWhiteSpace(header[i]) ? header[i].Trim() : $"列 {i + 1}";
            var name = proposed;
            var suffix = 2;
            while (table.Columns.Contains(name)) name = $"{proposed} ({suffix++})";
            table.Columns.Add(name, typeof(string));
        }

        foreach (var values in rows.Skip(1).Take(MaximumRows))
        {
            var row = table.NewRow();
            for (var i = 0; i < Math.Min(width, values.Length); i++) row[i] = values[i];
            table.Rows.Add(row);
        }

        var truncated = rows.Count > MaximumRows;
        return new TableDocument(table, truncated, $"{table.Rows.Count:N0} 行 · {table.Columns.Count:N0} 列 · {EncodingLabel(encoding)}");
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = new byte[4];
        using var stream = File.OpenRead(path);
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

    private static bool HasOpenQuote(StringBuilder text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++;
            else quoted = !quoted;
        }
        return quoted;
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
