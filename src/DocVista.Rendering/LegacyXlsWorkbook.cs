using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using System.Data;

namespace DocVista.Rendering;

public sealed class LegacyXlsWorkbook : ISpreadsheetWorkbook
{
    private readonly FileStream _stream;
    private readonly HSSFWorkbook _workbook;
    private readonly DataFormatter _formatter = new();

    private LegacyXlsWorkbook(FileStream stream, HSSFWorkbook workbook)
    {
        _stream = stream;
        _workbook = workbook;
        Sheets = Enumerable.Range(0, workbook.NumberOfSheets)
            .Select(index => new WorkbookSheet(workbook.GetSheetName(index), index.ToString()))
            .ToList();
    }

    public IReadOnlyList<WorkbookSheet> Sheets { get; }

    public static LegacyXlsWorkbook Open(string path)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { return new LegacyXlsWorkbook(stream, new HSSFWorkbook(stream)); }
        catch { stream.Dispose(); throw; }
    }

    public TableDocument LoadSheet(WorkbookSheet sheet)
    {
        const int maxRows = 50_000;
        const int maxColumns = 256;
        if (!int.TryParse(sheet.EntryPath, out var index)) throw new InvalidDataException("工作表索引无效");
        var source = _workbook.GetSheetAt(index);
        var width = 1;
        for (var rowIndex = source.FirstRowNum; rowIndex <= Math.Min(source.LastRowNum, source.FirstRowNum + maxRows); rowIndex++)
            width = Math.Max(width, Math.Min(maxColumns, (int)(source.GetRow(rowIndex)?.LastCellNum ?? 0)));

        var table = new DataTable(sheet.Name);
        for (var column = 0; column < width; column++) table.Columns.Add(ColumnName(column), typeof(string));

        var rowsRead = 0;
        for (var rowIndex = source.FirstRowNum; rowIndex <= source.LastRowNum && rowsRead < maxRows; rowIndex++)
        {
            var sourceRow = source.GetRow(rowIndex);
            if (sourceRow is null) continue;
            var row = table.NewRow();
            for (var column = 0; column < Math.Min(width, (int)sourceRow.LastCellNum); column++)
            {
                var cell = sourceRow.GetCell(column, MissingCellPolicy.RETURN_BLANK_AS_NULL);
                if (cell is not null) row[column] = _formatter.FormatCellValue(cell);
            }
            table.Rows.Add(row);
            rowsRead++;
        }

        var truncated = source.LastRowNum - source.FirstRowNum + 1 > maxRows;
        return new TableDocument(table, truncated, $"{table.Rows.Count:N0} 行 · {table.Columns.Count:N0} 列");
    }

    public void Dispose()
    {
        _workbook.Close();
        _stream.Dispose();
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        do { name = (char)('A' + index % 26) + name; index = index / 26 - 1; } while (index >= 0);
        return name;
    }
}
