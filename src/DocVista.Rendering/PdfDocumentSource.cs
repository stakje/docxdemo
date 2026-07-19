namespace DocVista.Rendering;

public static class PdfDocumentSource
{
    private static readonly byte[] Header = "%PDF-"u8.ToArray();

    public static void Validate(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) throw new InvalidDataException("PDF 文件为空。");

        Span<byte> probe = stackalloc byte[(int)Math.Min(1024, stream.Length)];
        stream.ReadExactly(probe);
        if (probe.IndexOf(Header) < 0)
            throw new InvalidDataException("文件没有有效的 PDF 标识，可能已损坏或扩展名不正确。");
    }

    public static Uri CreateUri(string path) => new(Path.GetFullPath(path), UriKind.Absolute);
}
