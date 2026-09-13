using System.Runtime.CompilerServices;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// PDF helpers for <see cref="IPaddleOcrService"/>: OCR a PDF page by page (optionally reading a born-digital page's
/// embedded text instead), stream the pages as they finish, or produce a searchable PDF (the page images with an
/// invisible, selectable text layer). PDFium renders on one background thread a page ahead of OCR, and pages are
/// handed off one at a time to keep memory low.
/// </summary>
public static class PdfOcrExtensions
{
    // ---------------------------------------------------------------- ExtractTextFromPdfAsync

    /// <summary>
    /// OCRs every page of a PDF file and returns per-page results.
    /// </summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        string pdfPath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(pdfPath), cancellationToken).ConfigureAwait(false);
        return await ExtractTextFromPdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// OCRs every page of a PDF file and returns per-page results.
    /// </summary>
    public static Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        string pdfPath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfAsync(service, pdfPath, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs every page of an in-memory PDF and returns per-page results.
    /// </summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        pdfOptions = ValidateArguments(service, pdfBytes, languages, pdfOptions);

        var pages = new List<PdfPageResult>();
        await foreach (var page in ProcessPagesAsync(service, pdfBytes, languages, options, pdfOptions, builder: null, cancellationToken).ConfigureAwait(false))
            pages.Add(page);
        return new PdfOcrResult { Pages = pages };
    }

    /// <summary>
    /// OCRs every page of an in-memory PDF and returns per-page results.
    /// </summary>
    public static Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfAsync(service, pdfBytes, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs every page of a PDF read from <paramref name="pdfStream"/> (read to the end; not disposed) and returns
    /// per-page results.
    /// </summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        Stream pdfStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllBytesAsync(pdfStream, cancellationToken).ConfigureAwait(false);
        return await ExtractTextFromPdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// OCRs every page of a PDF read from <paramref name="pdfStream"/> (read to the end; not disposed) and returns
    /// per-page results.
    /// </summary>
    public static Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IPaddleOcrService service,
        Stream pdfStream,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfAsync(service, pdfStream, new[] { language }, options, pdfOptions, cancellationToken);

    // ---------------------------------------------------------------- ExtractTextFromPdfPagesAsync

    /// <summary>
    /// OCRs a PDF file and yields each page's result as soon as it is finished, instead of collecting the whole
    /// document. Honors <see cref="PdfOcrOptions.PageRange"/>, <see cref="PdfOcrOptions.Password"/> and
    /// <paramref name="cancellationToken"/> (or <c>WithCancellation</c>). Breaking out of the loop stops rendering.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        string pdfPath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        return PagesFromFileAsync(service, Path.GetFullPath(pdfPath), languages, options, pdfOptions, cancellationToken);
    }

    /// <summary>
    /// OCRs a PDF file and yields each page's result as soon as it is finished.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        string pdfPath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfPagesAsync(service, pdfPath, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs an in-memory PDF and yields each page's result as soon as it is finished. Honors
    /// <see cref="PdfOcrOptions.PageRange"/>, <see cref="PdfOcrOptions.Password"/> and
    /// <paramref name="cancellationToken"/> (or <c>WithCancellation</c>). Breaking out of the loop stops rendering.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        pdfOptions = ValidateArguments(service, pdfBytes, languages, pdfOptions);
        return ProcessPagesAsync(service, pdfBytes, languages, options, pdfOptions, builder: null, cancellationToken);
    }

    /// <summary>
    /// OCRs an in-memory PDF and yields each page's result as soon as it is finished.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfPagesAsync(service, pdfBytes, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs a PDF read from <paramref name="pdfStream"/> (read to the end when enumeration starts; not disposed) and
    /// yields each page's result as soon as it is finished.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        Stream pdfStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfStream);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        return PagesFromStreamAsync(service, pdfStream, languages, options, pdfOptions, cancellationToken);
    }

    /// <summary>
    /// OCRs a PDF read from <paramref name="pdfStream"/> and yields each page's result as soon as it is finished.
    /// </summary>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IPaddleOcrService service,
        Stream pdfStream,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromPdfPagesAsync(service, pdfStream, new[] { language }, options, pdfOptions, cancellationToken);

    // ---------------------------------------------------------------- CreateSearchablePdfAsync

    /// <summary>
    /// OCRs a PDF and writes a searchable PDF (page images + invisible selectable text) to
    /// <paramref name="outputPdfPath"/>. Returns the per-page OCR results. The output is written to a temporary file
    /// next to the destination and moved into place only on success.
    /// </summary>
    public static async Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        string inputPdfPath,
        string outputPdfPath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(inputPdfPath), cancellationToken).ConfigureAwait(false);
        pdfOptions = ValidateArguments(service, bytes, languages, pdfOptions);

        string destination = Path.GetFullPath(outputPdfPath);
        string temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            PdfOcrResult result;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1 << 16))
            {
                result = await CreateSearchablePdfCoreAsync(service, bytes, file, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, destination, overwrite: true);
            return result;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// OCRs a PDF and writes a searchable PDF (page images + invisible selectable text) to
    /// <paramref name="outputPdfPath"/>. Returns the per-page OCR results.
    /// </summary>
    public static Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        string inputPdfPath,
        string outputPdfPath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => CreateSearchablePdfAsync(service, inputPdfPath, outputPdfPath, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    public static async Task<(PdfOcrResult Result, byte[] Pdf)> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        pdfOptions = ValidateArguments(service, pdfBytes, languages, pdfOptions);
        using var output = new MemoryStream();
        var result = await CreateSearchablePdfCoreAsync(service, pdfBytes, output, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
        return (result, output.ToArray());
    }

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    public static Task<(PdfOcrResult Result, byte[] Pdf)> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        byte[] pdfBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => CreateSearchablePdfAsync(service, pdfBytes, new[] { language }, options, pdfOptions, cancellationToken);

    /// <summary>
    /// OCRs a PDF read from <paramref name="inputPdf"/> and writes the searchable PDF to <paramref name="outputPdf"/>
    /// page by page as each page finishes. Neither stream is disposed, and the output does not need to be seekable.
    /// Returns the per-page OCR results.
    /// </summary>
    public static async Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        Stream inputPdf,
        Stream outputPdf,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPdf);
        if (!outputPdf.CanWrite)
            throw new ArgumentException("The output stream must be writable.", nameof(outputPdf));
        var bytes = await ReadAllBytesAsync(inputPdf, cancellationToken).ConfigureAwait(false);
        pdfOptions = ValidateArguments(service, bytes, languages, pdfOptions);
        return await CreateSearchablePdfCoreAsync(service, bytes, outputPdf, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// OCRs a PDF read from <paramref name="inputPdf"/> and writes the searchable PDF to <paramref name="outputPdf"/>.
    /// Neither stream is disposed. Returns the per-page OCR results.
    /// </summary>
    public static Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IPaddleOcrService service,
        Stream inputPdf,
        Stream outputPdf,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
        => CreateSearchablePdfAsync(service, inputPdf, outputPdf, new[] { language }, options, pdfOptions, cancellationToken);

    // ---------------------------------------------------------------- core

    private static PdfOcrOptions ValidateArguments(IPaddleOcrService service, byte[] pdfBytes, IReadOnlyList<OcrLanguage> languages, PdfOcrOptions? pdfOptions)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        return pdfOptions;
    }

    private static async Task<PdfOcrResult> CreateSearchablePdfCoreAsync(
        IPaddleOcrService service,
        byte[] pdfBytes,
        Stream output,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        CancellationToken cancellationToken)
    {
        var builder = new SearchablePdfBuilder(output);
        var pages = new List<PdfPageResult>();
        await foreach (var page in ProcessPagesAsync(service, pdfBytes, languages, options, pdfOptions, builder, cancellationToken).ConfigureAwait(false))
            pages.Add(page);
        builder.Finish();
        return new PdfOcrResult { Pages = pages };
    }

    private static async IAsyncEnumerable<PdfPageResult> PagesFromFileAsync(
        IPaddleOcrService service,
        string fullPath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await foreach (var page in ProcessPagesAsync(service, bytes, languages, options, pdfOptions, builder: null, cancellationToken).ConfigureAwait(false))
            yield return page;
    }

    private static async IAsyncEnumerable<PdfPageResult> PagesFromStreamAsync(
        IPaddleOcrService service,
        Stream pdfStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var bytes = await ReadAllBytesAsync(pdfStream, cancellationToken).ConfigureAwait(false);
        await foreach (var page in ProcessPagesAsync(service, bytes, languages, options, pdfOptions, builder: null, cancellationToken).ConfigureAwait(false))
            yield return page;
    }

    /// <summary>
    /// The shared page pipeline: take each rendered page from <see cref="PdfRasterizer"/>, OCR it (or wrap its
    /// accepted embedded text), and write it to <paramref name="builder"/> when producing a searchable PDF. The JPEG
    /// encoding runs concurrently with that page's OCR, and both only read the image.
    /// </summary>
    private static async IAsyncEnumerable<PdfPageResult> ProcessPagesAsync(
        IPaddleOcrService service,
        byte[] pdfBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        SearchablePdfBuilder? builder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[]? codes = null;
        await foreach (var page in PdfRasterizer.ReadPagesAsync(pdfBytes, pdfOptions, imagesRequired: builder is not null, cancellationToken).ConfigureAwait(false))
        {
            PdfPageResult result;
            using (page)
            {
                OcrResult ocr;
                PdfPageSource source;
                byte[]? jpeg = null;

                if (page.EmbeddedLines is { } lines)
                {
                    codes ??= languages.ToCodes();
                    ocr = EmbeddedTextLayer.CreateResult(lines, page.PixelWidth, page.PixelHeight, codes, page.EmbeddedTextDuration);
                    source = PdfPageSource.EmbeddedText;
                    if (builder is not null)
                        jpeg = SearchablePdfBuilder.EncodeJpeg(page.Image!, pdfOptions.JpegQuality);
                }
                else
                {
                    var image = page.Image!;
                    int quality = pdfOptions.JpegQuality;
                    Task<byte[]>? jpegTask = builder is null
                        ? null
                        : Task.Run(() => SearchablePdfBuilder.EncodeJpeg(image, quality), CancellationToken.None);

                    Task<OcrResult> ocrTask;
                    try
                    {
                        ocrTask = OcrPageAsync(service, image, languages, options, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        ocrTask = Task.FromException<OcrResult>(ex);
                    }

                    if (jpegTask is not null)
                    {
                        // Let both finish before the page (and its image) is disposed; errors are observed below.
                        try
                        {
                            await Task.WhenAll(ocrTask, jpegTask).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Rethrown by the individual awaits, the OCR error first.
                        }
                    }

                    ocr = await ocrTask.ConfigureAwait(false);
                    if (jpegTask is not null)
                        jpeg = await jpegTask.ConfigureAwait(false);
                    source = PdfPageSource.Ocr;
                }

                builder?.AddPage(jpeg!, page.PixelWidth, page.PixelHeight, page.Dpi, ocr.Lines);

                result = new PdfPageResult
                {
                    PageNumber = page.PageNumber,
                    Ocr = ocr,
                    PixelWidth = page.PixelWidth,
                    PixelHeight = page.PixelHeight,
                    Dpi = page.Dpi,
                    Source = source,
                };
            }

            pdfOptions.Progress?.Report(new PdfPageProgress(result.PageNumber, page.PageCount));
            yield return result;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The PDF stream must be readable.", nameof(stream));
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is preferable to masking the original error.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// OCRs one rasterized page. The public <c>Image&lt;Rgb24&gt;</c> overload is <see cref="OcrLanguage"/>-only,
    /// and the PDF path now carries <see cref="OcrLanguage"/> values too; we convert them to string codes only
    /// for <see cref="PaddleOcrService"/>'s internal code-based image path (no re-encode) when available, falling
    /// back to the byte[] overload (passing the enum list) for custom <see cref="IPaddleOcrService"/> implementations.
    /// </summary>
    private static Task<OcrResult> OcrPageAsync(
        IPaddleOcrService service,
        Image<Rgb24> image,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        if (service is PaddleOcrService concrete)
            return concrete.OcrDecodedImageAsync(image, languages.ToCodes(), options, cancellationToken);

        using var ms = new MemoryStream();
        image.SaveAsBmp(ms);
        return service.ExtractTextFromImage(ms.ToArray(), languages, options, cancellationToken);
    }
}
