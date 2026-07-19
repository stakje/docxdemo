using DocVista.Core;
using DocVista.Rendering;
using NPOI.HSSF.UserModel;
using NPOI.XWPF.UserModel;
using System.IO.Compression;
using System.Text;

var failures = new List<string>();
var testCount = 0;
var keepFixtures = Environment.GetEnvironmentVariable("DOCVISTA_WRITE_FIXTURES") == "1";
var fixtureDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "fixtures");
if (keepFixtures) Directory.CreateDirectory(fixtureDirectory);
await Run("格式识别", TestDocumentKinds);
await Run("设置归一化", TestSettings);
await Run("CSV 解析", TestCsvAsync);
await Run("XLSX 解析", TestXlsxAsync);
await Run("DOCX 解析", TestDocxAsync);
await Run("PPTX 解析", TestPptxAsync);
await Run("XLS 解析", TestXlsAsync);
await Run("旧版 Office 兼容模式", TestLegacyOfficeAsync);
await Run("PDF 文件源", TestPdfDocumentSourceAsync);
await Run("CSV 渐进列探测", TestCsvLateWideRowAsync);
await Run("解析取消", TestParsingCancellationAsync);

if (failures.Count == 0)
{
    Console.WriteLine($"全部 {testCount} 项测试通过。");
    return 0;
}

Task TestSettings()
{
    var settings = new AppSettings
    {
        DefaultZoomPercent = 10,
        CurrentZoomPercent = 500,
        ZoomStepPercent = 2,
        RecentDocumentLimit = 100,
        SpreadsheetRowHeight = 8
    };
    settings.Normalize();
    Equal(50, settings.DefaultZoomPercent);
    Equal(200, settings.CurrentZoomPercent);
    Equal(5, settings.ZoomStepPercent);
    Equal(30, settings.RecentDocumentLimit);
    Equal(24d, settings.SpreadsheetRowHeight);
    return Task.CompletedTask;
}

foreach (var failure in failures) Console.Error.WriteLine(failure);
return 1;

async Task Run(string name, Func<Task> test)
{
    testCount++;
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
            var title = document.CreateParagraph();
            title.Alignment = ParagraphAlignment.CENTER;
            var titleRun = title.CreateRun();
            titleRun.IsBold = true;
            titleRun.FontSize = 18;
            titleRun.SetText("DocVista 文档标题");
            var table = document.CreateTable(1, 2);
            table.GetRow(0).GetCell(0).SetText("项目");
            table.GetRow(0).GetCell(1).SetText("状态");
            var imageParagraph = document.CreateParagraph();
            using (var imageStream = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")))
                imageParagraph.CreateRun().AddPicture(imageStream, (int)PictureType.PNG, "pixel.png", 914400, 914400);
            document.CreateParagraph().CreateRun().SetText("这是用于验证内置 Word 查看器的正文。");
            using var stream = File.Create(path);
            document.Write(stream);
        }
        var result = OfficeDocumentLoader.Load(path);
        Equal(OfficeViewMode.Document, result.Mode);
        var blocks = result.Pages.SelectMany(page => page.Blocks).ToList();
        Equal("DocVista 文档标题", blocks[0].Text);
        Equal(true, blocks[0].IsBold);
        Equal(18d, blocks[0].FontSize);
        Equal(OfficeTextAlignment.Center, blocks[0].Alignment);
        Equal(true, blocks[1].IsTableRow);
        Equal("项目", blocks[1].Cells![0]);
        Equal(true, blocks[2].ImageData is { Length: > 0 });
        Equal(72d, blocks[2].ImageWidth);
        Equal(72d, blocks[2].ImageHeight);
        Equal(true, blocks[3].Text.Contains("内置 Word 查看器"));
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

Task TestPdfDocumentSourceAsync()
{
    var validPath = keepFixtures ? FixturePath("pdf") : Path.Combine(Path.GetTempPath(), $"docvista #{Guid.NewGuid():N}.pdf");
    var invalidPath = Path.Combine(Path.GetTempPath(), $"docvista-{Guid.NewGuid():N}.pdf");
    try
    {
        File.WriteAllBytes(validPath, CreateMinimalPdf());
        File.WriteAllText(invalidPath, "not a pdf");
        PdfDocumentSource.Validate(validPath);
        var uri = PdfDocumentSource.CreateUri(validPath);
        Equal(true, uri.IsFile);
        Equal(Path.GetFullPath(validPath), Path.GetFullPath(uri.LocalPath));
        Throws<InvalidDataException>(() => PdfDocumentSource.Validate(invalidPath));
        return Task.CompletedTask;
    }
    finally
    {
        if (!keepFixtures) File.Delete(validPath);
        File.Delete(invalidPath);
    }
}

async Task TestCsvLateWideRowAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"docvista-{Guid.NewGuid():N}.csv");
    try
    {
        var content = new StringBuilder("first,second\r\n");
        for (var index = 0; index < 300; index++) content.Append($"row {index},value\r\n");
        content.Append("wide,extra,tail\r\n");
        await File.WriteAllTextAsync(path, content.ToString(), new UTF8Encoding(false));
        var document = await CsvDocument.LoadAsync(path);
        Equal(301, document.Table.Rows.Count);
        Equal(3, document.Table.Columns.Count);
        Equal("tail", document.Table.Rows[300][2]);
    }
    finally { File.Delete(path); }
}

async Task TestParsingCancellationAsync()
{
    var path = Path.Combine(Path.GetTempPath(), $"docvista-{Guid.NewGuid():N}.csv");
    try
    {
        await File.WriteAllTextAsync(path, "a,b\r\n1,2");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => CsvDocument.LoadAsync(path, cancellation.Token));
        Throws<OperationCanceledException>(() => OfficeDocumentLoader.Load(Path.ChangeExtension(path, ".docx"), cancellation.Token));
    }
    finally { File.Delete(path); }
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

static byte[] CreateMinimalPdf()
{
    using var stream = new MemoryStream();
    static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
    void Write(string value) => stream.Write(Bytes(value));

    Write("%PDF-1.4\n");
    var content = "BT /F1 18 Tf 72 720 Td (DocVista PDF) Tj ET\n";
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        $"<< /Length {Bytes(content).Length} >>\nstream\n{content}endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    };
    var offsets = new List<long> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(stream.Position);
        Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
    }

    var xrefOffset = stream.Position;
    Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
    Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
    return stream.ToArray();
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected '{expected}', actual '{actual}'");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"expected exception '{typeof(TException).Name}'");
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"expected exception '{typeof(TException).Name}'");
}
