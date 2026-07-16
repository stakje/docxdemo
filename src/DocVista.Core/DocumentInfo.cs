namespace DocVista.Core;

public enum DocumentKind
{
    Pdf,
    Csv,
    Excel,
    Word,
    PowerPoint,
    Unknown
}

public sealed record DocumentInfo(string Path, string Name, DocumentKind Kind, long Size)
{
    public string Extension => System.IO.Path.GetExtension(Path).ToUpperInvariant();

    public static DocumentInfo FromPath(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
        var kind = extension switch
        {
            ".pdf" => DocumentKind.Pdf,
            ".csv" => DocumentKind.Csv,
            ".xls" or ".xlsx" => DocumentKind.Excel,
            ".doc" or ".docx" => DocumentKind.Word,
            ".ppt" or ".pptx" => DocumentKind.PowerPoint,
            _ => DocumentKind.Unknown
        };

        var file = new FileInfo(fullPath);
        return new DocumentInfo(fullPath, file.Name, kind, file.Exists ? file.Length : 0);
    }

    public static bool IsSupported(string path) => FromPath(path).Kind != DocumentKind.Unknown;
}
