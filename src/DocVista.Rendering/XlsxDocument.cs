using System.Data;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace DocVista.Rendering;

public sealed record WorkbookSheet(string Name, string EntryPath);

public interface ISpreadsheetWorkbook : IDisposable
{
    IReadOnlyList<WorkbookSheet> Sheets { get; }
    TableDocument LoadSheet(WorkbookSheet sheet, CancellationToken cancellationToken = default);
}

public sealed class XlsxWorkbook : ISpreadsheetWorkbook
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyList<string> _sharedStrings;

    private XlsxWorkbook(FileStream stream, ZipArchive archive, IReadOnlyList<WorkbookSheet> sheets, IReadOnlyList<string> sharedStrings)
    {
        _stream = stream;
        _archive = archive;
        Sheets = sheets;
        _sharedStrings = sharedStrings;
    }

    public IReadOnlyList<WorkbookSheet> Sheets { get; }

    public static XlsxWorkbook Open(string path, CancellationToken cancellationToken = default)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ZipArchive? archive = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            var workbook = LoadXml(archive, "xl/workbook.xml");
            var relationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

            var targets = relationships.Root?.Elements(packageRel + "Relationship")
                .Where(node => node.Attribute("Id") is not null && node.Attribute("Target") is not null)
                .ToDictionary(node => (string)node.Attribute("Id")!, node => NormalizeTarget((string)node.Attribute("Target")!))
                ?? new Dictionary<string, string>();

            var sheets = workbook.Root?.Element(main + "sheets")?.Elements(main + "sheet")
                .Select(node => new { Name = (string?)node.Attribute("name"), Id = (string?)node.Attribute(rel + "id") })
                .Where(sheet => sheet.Name is not null && sheet.Id is not null && targets.ContainsKey(sheet.Id))
                .Select(sheet => new WorkbookSheet(sheet.Name!, targets[sheet.Id!]))
                .ToList() ?? [];

            return new XlsxWorkbook(stream, archive, sheets, LoadSharedStrings(archive, cancellationToken));
        }
        catch
        {
            archive?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    public TableDocument LoadSheet(WorkbookSheet sheet, CancellationToken cancellationToken = default)
    {
        const int maxRows = 50_000;
        const int maxColumns = 256;
        const int maxCells = 2_000_000;
        var entry = _archive.GetEntry(sheet.EntryPath.Replace('\\', '/')) ?? throw new InvalidDataException($"工作簿缺少 {sheet.EntryPath}");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true });
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var table = new DataTable(sheet.Name);
        var rowsRead = 0;
        var truncated = false;
        table.BeginLoadData();
        reader.MoveToContent();
        try
        {
            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "dimension" && table.Columns.Count == 0)
                {
                    var dimensionWidth = DimensionWidth(reader.GetAttribute("ref"));
                    if (dimensionWidth is > 0 and <= maxColumns)
                        while (table.Columns.Count < dimensionWidth) table.Columns.Add(ColumnName(table.Columns.Count), typeof(string));
                    reader.Read();
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                {
                    reader.Read();
                    continue;
                }
                if (rowsRead >= maxRows || table.Columns.Count > 0 && (long)table.Columns.Count * (rowsRead + 1) > maxCells) { truncated = true; break; }
                var sourceRow = (XElement)XNode.ReadFrom(reader);
                var values = new List<(int Index, string Value)>();
                var requiredWidth = table.Columns.Count;
                foreach (var cell in sourceRow.Elements(main + "c"))
                {
                    var index = ColumnIndex((string?)cell.Attribute("r"));
                    if (index < 0 || index >= maxColumns) continue;
                    requiredWidth = Math.Max(requiredWidth, index + 1);
                    var type = (string?)cell.Attribute("t");
                    var value = type == "inlineStr"
                        ? string.Concat(cell.Descendants(main + "t").Select(node => node.Value))
                        : cell.Element(main + "v")?.Value ?? string.Empty;
                    if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < _sharedStrings.Count)
                        value = _sharedStrings[sharedIndex];
                    if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    values.Add((index, value));
                }
                requiredWidth = Math.Max(1, requiredWidth);
                if ((long)requiredWidth * (rowsRead + 1) > maxCells) { truncated = true; break; }
                while (table.Columns.Count < requiredWidth) table.Columns.Add(ColumnName(table.Columns.Count), typeof(string));
                var row = table.NewRow();
                foreach (var value in values) row[value.Index] = value.Value;
                table.Rows.Add(row);
                rowsRead++;
            }
        }
        finally { table.EndLoadData(); }

        if (table.Columns.Count == 0) table.Columns.Add("A", typeof(string));
        return new TableDocument(table, truncated, $"{table.Rows.Count:N0} 行 · {table.Columns.Count:N0} 列");
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path.Replace('\\', '/')) ?? throw new InvalidDataException($"工作簿缺少 {path}");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true });
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var strings = new List<string>();
        reader.MoveToContent();
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                var item = (XElement)XNode.ReadFrom(reader);
                strings.Add(string.Concat(item.Descendants(main + "t").Select(node => node.Value)));
            }
            else reader.Read();
        }
        return strings;
    }

    private static int DimensionWidth(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return 0;
        var lastCell = reference[(reference.LastIndexOf(':') + 1)..];
        return ColumnIndex(lastCell) + 1;
    }

    private static string NormalizeTarget(string target)
    {
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target.TrimStart('.', '/');
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return -1;
        var value = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter)) value = value * 26 + char.ToUpperInvariant(character) - 'A' + 1;
        return value - 1;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        do
        {
            name = (char)('A' + index % 26) + name;
            index = index / 26 - 1;
        } while (index >= 0);
        return name;
    }
}
