using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// PDF helpers for <see cref="IPaddleOcrService"/>: OCR a scanned PDF page-by-page, or produce a
/// searchable PDF (the original page images with an invisible, selectable OCR text layer).
/// Pages are rasterized with PDFium and processed one at a time to keep memory low.
/// </summary>
public static class PdfOcrExtensions
{
    /// <summary>OCRs every page of a PDF file and returns per-page results.</summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        string pdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(pdfPath), cancellationToken).ConfigureAwait(false);
        return await ExtractTextFromPdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>OCRs every page of an in-memory PDF and returns per-page results.</summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        var langs = languages as string[] ?? languages.ToArray();

        var pages = new List<PdfPageResult>();
        await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions, async (pageNumber, count, image) =>
        {
            var ocr = await service.ExtractTextFromImage(image, langs, options, cancellationToken).ConfigureAwait(false);
            pages.Add(new PdfPageResult
            {
                PageNumber = pageNumber,
                Ocr = ocr,
                PixelWidth = image.Width,
                PixelHeight = image.Height,
            });
            pdfOptions.Progress?.Report(new PdfPageProgress(pageNumber, count));
        }, cancellationToken).ConfigureAwait(false);

        return new PdfOcrResult { Pages = pages };
    }

    /// <summary>
    /// OCRs a PDF and writes a searchable PDF (page images + invisible selectable text) to
    /// <paramref name="outputPdfPath"/>. Returns the per-page OCR results.
    /// </summary>
    public static async Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        string inputPdfPath,
        string outputPdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(inputPdfPath), cancellationToken).ConfigureAwait(false);

        var (result, pdf) = await CreateSearchablePdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.GetFullPath(outputPdfPath), pdf, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    public static async Task<(PdfOcrResult Result, byte[] Pdf)> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        var langs = languages as string[] ?? languages.ToArray();

        var builder = new SearchablePdfBuilder();
        var pages = new List<PdfPageResult>();

        await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions, async (pageNumber, count, image) =>
        {
            var ocr = await service.ExtractTextFromImage(image, langs, options, cancellationToken).ConfigureAwait(false);
            builder.AddPage(image, ocr, pdfOptions.Dpi, pdfOptions.JpegQuality);
            pages.Add(new PdfPageResult
            {
                PageNumber = pageNumber,
                Ocr = ocr,
                PixelWidth = image.Width,
                PixelHeight = image.Height,
            });
            pdfOptions.Progress?.Report(new PdfPageProgress(pageNumber, count));
        }, cancellationToken).ConfigureAwait(false);

        return (new PdfOcrResult { Pages = pages }, builder.Build());
    }
}
