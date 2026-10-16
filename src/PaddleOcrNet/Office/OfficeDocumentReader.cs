using PaddleOcrNet.Office.Internal;

namespace PaddleOcrNet.Office;

/// <summary>Extracts native DOCX/XLSX/PPTX content without loading OCR models or rendering Office files.</summary>
public static class OfficeDocumentReader
{
    /// <summary>Reads native content from a supported package. Embedded images are identified but not OCR'd.</summary>
    public static OfficeDocumentResult Read(string path, OfficeDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        return Read(stream, options, cancellationToken);
    }

    /// <summary>Reads from the current stream position to its end; leaves the caller's stream open.</summary>
    public static OfficeDocumentResult Read(Stream stream, OfficeDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var package = new OfficePackage(stream, options ?? new(), cancellationToken);
        return Read(package, cancellationToken);
    }

    internal static OfficeDocumentResult Read(OfficePackage package, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (format, main) = package.Identify();
        var sections = format switch
        {
            OfficeDocumentFormat.Docx => WordPresentationReader.ReadWord(package, main, ct),
            OfficeDocumentFormat.Xlsx => SpreadsheetReader.Read(package, main, ct),
            OfficeDocumentFormat.Pptx => WordPresentationReader.ReadPresentation(package, main, ct),
            _ => throw new NotSupportedException(),
        };
        if (sections.SelectMany(s => s.Blocks).Any(b => b.Kind == OfficeContentKind.Image))
            package.Warn("Embedded images are identified; image text is absent unless image OCR is requested through the service extension.");
        return new OfficeDocumentResult { Format = format, Sections = sections, Warnings = package.Warnings };
    }
}
