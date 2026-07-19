using NPOI.XWPF.UserModel;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DocVista.Rendering;

public enum OfficeViewMode { Document, Presentation, CompatibilityText }
public enum OfficeTextAlignment { Left, Center, Right, Justify }

public sealed record OfficeTextBlock(
    string Text,
    bool IsHeading = false,
    bool IsTableRow = false,
    IReadOnlyList<string>? Cells = null,
    bool IsBold = false,
    bool IsItalic = false,
    double FontSize = 0,
    OfficeTextAlignment Alignment = OfficeTextAlignment.Left,
    double LeftIndent = 0,
    double FirstLineIndent = 0,
    double SpaceBefore = 0,
    double SpaceAfter = 0,
    byte[]? ImageData = null,
    double ImageWidth = 0,
    double ImageHeight = 0);
public sealed record OfficePage(string? Title, IReadOnlyList<OfficeTextBlock> Blocks);
public sealed record OfficeDocument(OfficeViewMode Mode, IReadOnlyList<OfficePage> Pages, string Summary);

public static partial class OfficeDocumentLoader
{
    public static OfficeDocument Load(string path, CancellationToken cancellationToken = default)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => LoadDocx(path, cancellationToken),
            ".pptx" => LoadPptx(path, cancellationToken),
            ".doc" or ".ppt" => LoadLegacyText(path, cancellationToken),
            _ => throw new NotSupportedException("不支持此 Office 格式")
        };
    }

    private static OfficeDocument LoadDocx(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = new XWPFDocument(stream);
        var blocks = new List<OfficeTextBlock>();
        var paragraphCount = 0;
        var tableCount = 0;
        foreach (var element in document.BodyElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is XWPFParagraph paragraph)
            {
                var text = Clean(paragraph.Text);
                var style = paragraph.Style ?? string.Empty;
                var runs = paragraph.Runs.Where(run => !string.IsNullOrWhiteSpace(run.Text)).ToList();
                var fontSizes = runs.Select(run => run.FontSize).Where(size => size > 0).ToList();
                if (text.Length > 0)
                {
                    blocks.Add(new OfficeTextBlock(
                        text,
                        style.Contains("heading", StringComparison.OrdinalIgnoreCase) || style.Contains("title", StringComparison.OrdinalIgnoreCase),
                        IsBold: runs.Any(run => run.IsBold),
                        IsItalic: runs.Any(run => run.IsItalic),
                        FontSize: fontSizes.Count == 0 ? 0 : fontSizes.Average(),
                        Alignment: ConvertAlignment(paragraph.Alignment),
                        LeftIndent: TwipsToPixels(paragraph.IndentationLeft),
                        FirstLineIndent: TwipsToPixels(paragraph.IndentationFirstLine),
                        SpaceBefore: TwipsToPixels(paragraph.SpacingBefore),
                        SpaceAfter: TwipsToPixels(paragraph.SpacingAfter)));
                    paragraphCount++;
                }

                foreach (var picture in paragraph.Runs.SelectMany(run => run.GetEmbeddedPictures()))
                {
                    var pictureData = picture.GetPictureData()?.Data;
                    if (pictureData is not { Length: > 0 }) continue;
                    blocks.Add(new OfficeTextBlock(
                        string.Empty,
                        Alignment: ConvertAlignment(paragraph.Alignment),
                        ImageData: pictureData.ToArray(),
                        ImageWidth: EmusToPoints(picture.Width),
                        ImageHeight: EmusToPoints(picture.Height)));
                }
            }
            else if (element is XWPFTable table)
            {
                foreach (var row in table.Rows)
                {
                    var cells = row.GetTableCells()
                        .Select(cell => Clean(string.Join(Environment.NewLine, cell.Paragraphs.Select(cellParagraph => cellParagraph.Text))))
                        .ToList();
                    if (cells.Any(text => text.Length > 0))
                        blocks.Add(new OfficeTextBlock(string.Join("  ", cells), IsTableRow: true, Cells: cells));
                }
                tableCount++;
            }
        }

        var pages = new List<OfficePage> { new(null, blocks) };
        return new OfficeDocument(OfficeViewMode.Document, pages, $"连续兼容视图 · {paragraphCount:N0} 段 · {tableCount:N0} 个表格");
    }

    private static OfficeDocument LoadPptx(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var entries = archive.Entries
            .Where(entry => SlidePath().IsMatch(entry.FullName))
            .OrderBy(entry => SlideNumber(entry.FullName))
            .ToList();
        var pages = new List<OfficePage>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var slideStream = entry.Open();
            var xml = XDocument.Load(slideStream, LoadOptions.None);
            var texts = xml.Descendants(drawing + "t").Select(node => Clean(node.Value)).Where(text => text.Length > 0).ToList();
            var title = texts.FirstOrDefault();
            var blocks = texts.Skip(title is null ? 0 : 1).Select(text => new OfficeTextBlock(text)).ToList();
            pages.Add(new OfficePage(title, blocks));
        }
        if (pages.Count == 0) throw new InvalidDataException("演示文稿中没有可读取的幻灯片");
        return new OfficeDocument(OfficeViewMode.Presentation, pages, $"{pages.Count:N0} 张幻灯片");
    }

    private static OfficeDocument LoadLegacyText(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("旧版 Office 文件超过 128 MB，无法使用兼容文本模式打开");
        var bytes = ReadAllBytes(path, cancellationToken);
        var texts = new List<string>();
        ExtractUnicodeRuns(bytes, texts, cancellationToken);
        ExtractAnsiRuns(bytes, texts, cancellationToken);
        var unique = texts.Select(Clean)
            .Where(text => text.Length >= 4 && MeaningfulRatio(text) >= 0.72)
            .Distinct(StringComparer.Ordinal)
            .Take(2_000)
            .Select(text => new OfficeTextBlock(text))
            .ToList();
        if (unique.Count == 0) throw new InvalidDataException("无法从旧版二进制文档中提取可显示内容");
        var pages = Paginate(unique, 28);
        return new OfficeDocument(OfficeViewMode.CompatibilityText, pages, $"兼容文本模式 · {unique.Count:N0} 段");
    }

    private static IReadOnlyList<OfficePage> Paginate(IReadOnlyList<OfficeTextBlock> blocks, int pageSize)
    {
        if (blocks.Count == 0) return [new OfficePage(null, [new OfficeTextBlock("文档没有可显示的文本内容")])];
        return blocks.Chunk(pageSize).Select(chunk => new OfficePage(null, chunk)).ToList();
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(1024 * 1024, bytes.Length - offset));
            if (read == 0) throw new EndOfStreamException("Office 文件读取不完整");
            offset += read;
        }
        return bytes;
    }

    private static void ExtractUnicodeRuns(byte[] bytes, List<string> output, CancellationToken cancellationToken)
    {
        for (var parity = 0; parity < 2; parity++)
        {
            var run = new StringBuilder();
            for (var index = parity; index + 1 < bytes.Length; index += 2)
            {
                if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                var character = (char)(bytes[index] | bytes[index + 1] << 8);
                if (IsTextCharacter(character)) run.Append(character);
                else { Flush(run, output, 4); run.Clear(); }
            }
            Flush(run, output, 4);
        }
    }

    private static void ExtractAnsiRuns(byte[] bytes, List<string> output, CancellationToken cancellationToken)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(936);
        var run = new List<byte>();
        for (var index = 0; index < bytes.Length; index++)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = bytes[index];
            if (value is >= 32 and <= 126 or >= 0x81) run.Add(value);
            else
            {
                if (run.Count >= 8) output.Add(encoding.GetString(run.ToArray()));
                run.Clear();
            }
        }
        if (run.Count >= 8) output.Add(encoding.GetString(run.ToArray()));
    }

    private static void Flush(StringBuilder run, List<string> output, int minimum)
    {
        if (run.Length >= minimum) output.Add(run.ToString());
    }

    private static bool IsTextCharacter(char character) => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || char.IsPunctuation(character) || character is >= '\u4E00' and <= '\u9FFF';
    private static double MeaningfulRatio(string text) => text.Length == 0 ? 0 : text.Count(IsTextCharacter) / (double)text.Length;
    private static double TwipsToPixels(int value) => value <= 0 ? 0 : Math.Min(160, value / 15d);
    private static double EmusToPoints(long value) => value <= 0 ? 0 : value / 12_700d;
    private static OfficeTextAlignment ConvertAlignment(ParagraphAlignment alignment) => alignment switch
    {
        ParagraphAlignment.CENTER => OfficeTextAlignment.Center,
        ParagraphAlignment.RIGHT => OfficeTextAlignment.Right,
        ParagraphAlignment.BOTH or ParagraphAlignment.DISTRIBUTE => OfficeTextAlignment.Justify,
        _ => OfficeTextAlignment.Left
    };
    private static string Clean(string text) => Whitespace().Replace(text.Replace('\0', ' '), " ").Trim();
    private static int SlideNumber(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(5), out var number) ? number : int.MaxValue;

    [GeneratedRegex(@"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlidePath();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
