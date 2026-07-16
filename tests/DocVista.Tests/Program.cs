using DocVista.Core;
using DocVista.Rendering;
using NPOI.HSSF.UserModel;
using NPOI.XWPF.UserModel;
using System.IO.Compression;
using System.Text;

var failures = new List<string>();
var keepFixtures = Environment.GetEnvironmentVariable("DOCVISTA_WRITE_FIXTURES") == "1";
var fixtureDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "fixtures");
if (keepFixtures) Directory.CreateDirectory(fixtureDirectory);
await Run("格式识别", TestDocumentKinds);
await Run("CSV 解析", TestCsvAsync);
await Run("XLSX 解析", TestXlsxAsync);
await Run("DOCX 解析", TestDocxAsync);
await Run("PPTX 解析", TestPptxAsync);
await Run("XLS 解析", TestXlsAsync);
await Run("旧版 Office 兼容模式", TestLegacyOfficeAsync);

if (failures.Count == 0)
{
    Console.WriteLine("全部 7 项测试通过。");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures.Add($"FAIL {name}: {exception.Message}"); }
}

Task TestDocumentKinds()
{
    Equal(DocumentKind.Pdf, DocumentInfo.FromPath("report.pdf").Kind);
    Equal(DocumentKind.Word, DocumentInfo.FromPath("report.DOCX").Kind);
    Equal(DocumentKind.PowerPoint, DocumentInfo.FromPath("deck.ppt").Kind);
    Equal(DocumentKind.Excel, DocumentInfo.FromPath("data.xlsx").Kind);
    Equal(DocumentKind.Csv, DocumentInfo.FromPath("data.csv").Kind);
    Equal(DocumentKind.Unknown, DocumentInfo.FromPath("data.txt").Kind);
    return Task.CompletedTask;
}

async Task TestCsvAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"docvista-{Guid.NewGuid():N}.csv");
    try
    {
        await File.WriteAllTextAsync(path, "name;note;amount\r\nAlpha;\"line 1\r\nline 2\";12\r\nBeta;\"quoted \"\"value\"\"\";8", new UTF8Encoding(true));
        var document = await CsvDocument.LoadAsync(path);
        Equal(2, document.Table.Rows.Count);
        Equal(3, document.Table.Columns.Count);
        Equal("line 1\r\nline 2", document.Table.Rows[0]["note"]);
        Equal("quoted \"value\"", document.Table.Rows[1]["note"]);
    }
    finally { File.Delete(path); }
}

Task TestXlsxAsync()
{
    var path = FixturePath("xlsx");
    try
    {
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="数据" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            Add(archive, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>名称</t></si><si><t>DocVista</t></si></sst>
                """);
            Add(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>42</v></c></row><row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2" t="b"><v>1</v></c></row></sheetData></worksheet>
                """);
        }

        using var workbook = XlsxWorkbook.Open(path);
        Equal(1, workbook.Sheets.Count);
        Equal("数据", workbook.Sheets[0].Name);
        var sheet = workbook.LoadSheet(workbook.Sheets[0]);
        Equal("名称", sheet.Table.Rows[0][0]);
        Equal("DocVista", sheet.Table.Rows[1][0]);
        Equal("TRUE", sheet.Table.Rows[1][1]);
        return Task.CompletedTask;
    }
    finally { if (!keepFixtures) File.Delete(path); }
}

Task TestDocxAsync()
{
    var path = FixturePath("docx");
    try
    {
        using (var document = new XWPFDocument())
        {
            document.CreateParagraph().CreateRun().SetText("DocVista 文档标题");
            document.CreateParagraph().CreateRun().SetText("这是用于验证内置 Word 查看器的正文。");
            using var stream = File.Create(path);
            document.Write(stream);
        }
        var result = OfficeDocumentLoader.Load(path);
        Equal(OfficeViewMode.Document, result.Mode);
        Equal(true, result.Pages.SelectMany(page => page.Blocks).Any(block => block.Text.Contains("内置 Word 查看器")));
        return Task.CompletedTask;
    }
    finally { if (!keepFixtures) File.Delete(path); }
}

Task TestPptxAsync()
{
    var path = FixturePath("pptx");
    try
    {
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            Add(archive, "ppt/slides/slide1.xml", """
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>产品路线</a:t></a:r></a:p><a:p><a:r><a:t>第一阶段完成</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>
                """);
        var result = OfficeDocumentLoader.Load(path);
        Equal(OfficeViewMode.Presentation, result.Mode);
        Equal("产品路线", result.Pages[0].Title);
        Equal("第一阶段完成", result.Pages[0].Blocks[0].Text);
        return Task.CompletedTask;
    }
    finally { if (!keepFixtures) File.Delete(path); }
}

Task TestXlsAsync()
{
    var path = FixturePath("xls");
    try
    {
        using (var workbook = new HSSFWorkbook())
        {
            var sheet = workbook.CreateSheet("数据");
            sheet.CreateRow(0).CreateCell(0).SetCellValue("项目");
            sheet.CreateRow(1).CreateCell(0).SetCellValue("DocVista");
            using var stream = File.Create(path);
            workbook.Write(stream, true);
        }
        using var result = LegacyXlsWorkbook.Open(path);
        var table = result.LoadSheet(result.Sheets[0]);
        Equal("DocVista", table.Table.Rows[1][0]);
        return Task.CompletedTask;
    }
    finally { if (!keepFixtures) File.Delete(path); }
}

async Task TestLegacyOfficeAsync()
{
    var path = FixturePath("doc");
    try
    {
        var prefix = Enumerable.Repeat((byte)0x01, 64).ToArray();
        var text = Encoding.Unicode.GetBytes("旧版文档兼容内容测试");
        await File.WriteAllBytesAsync(path, prefix.Concat(text).Concat(new byte[16]).ToArray());
        var result = OfficeDocumentLoader.Load(path);
        Equal(OfficeViewMode.CompatibilityText, result.Mode);
        Equal(true, result.Pages.SelectMany(page => page.Blocks).Any(block => block.Text.Contains("旧版文档兼容内容")));
    }
    finally { if (!keepFixtures) File.Delete(path); }
}

string FixturePath(string extension) => keepFixtures
    ? Path.Combine(fixtureDirectory, $"sample.{extension}")
    : Path.Combine(Path.GetTempPath(), $"docvista-{Guid.NewGuid():N}.{extension}");

static void Add(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected '{expected}', actual '{actual}'");
}
