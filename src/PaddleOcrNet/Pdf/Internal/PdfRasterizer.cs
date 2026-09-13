using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// Rasterizes PDF pages via PDFium (Docnet.Core) and optionally reads their embedded text layer.
/// <para>
/// A single background task owns every PDFium call, which keeps PDFium single-threaded. It renders one page ahead
/// into a bounded channel while the consumer processes the previous page, so at most a few page bitmaps are alive
/// at once regardless of document length.
/// </para>
/// </summary>
internal static class PdfRasterizer
{
    // PDFium load-error code for a password-protected document (FPDF_ERR_PASSWORD). Surfaced on
    // DocnetLoadDocumentException.ErrorCode when the supplied password is missing or wrong.
    private const uint FpdfErrPassword = 4;

    /// <summary>Pages rendered ahead and waiting for the consumer.</summary>
    private const int RenderAheadCapacity = 1;

    /// <summary>
    /// Pixels-per-point of the size probe used for auto DPI. Docnet truncates page sizes to whole pixels, so a large
    /// factor keeps 0.01 pt precision; the probe never renders.
    /// </summary>
    private const double ProbeScale = 100.0;

    /// <summary>Longest rendered side auto DPI aims for; text detection caps its input at this size.</summary>
    internal const double AutoDpiTargetLongSidePixels = 4000.0;

    /// <summary>Lower clamp of auto DPI (6 pt text is borderline at 200 DPI).</summary>
    internal const int AutoDpiMin = 150;

    /// <summary>Upper clamp of auto DPI.</summary>
    internal const int AutoDpiMax = 400;

    // (c * 255 + 255 * 0) >> 8 for a fully opaque pixel, exactly as Docnet's NaiveTransparencyRemover computes it.
    private static readonly byte[] s_opaque = BuildOpaqueTable();

    /// <summary>
    /// Yields the pages selected by <paramref name="options"/> (its <see cref="PdfOcrOptions.PageRange"/>, or every
    /// page) in document order. Each page is rendered at the configured DPI, or at its own auto DPI when
    /// <see cref="PdfOcrOptions.Dpi"/> is <see cref="PdfOcrOptions.AutoDpi"/>. When
    /// <see cref="PdfOcrOptions.TextLayer"/> accepts a page's embedded text, the page carries
    /// <see cref="RenderedPage.EmbeddedLines"/> and is only rendered if <paramref name="imagesRequired"/>.
    /// The consumer must dispose every yielded page.
    /// </summary>
    /// <exception cref="PdfProcessingException">
    /// The input is empty; the PDF is encrypted and the supplied <see cref="PdfOcrOptions.Password"/> is
    /// missing/blank/wrong; the page count or a selected page exceeds a configured guard; or the PDF cannot
    /// be opened/rendered (corrupt or not a PDF).
    /// </exception>
    public static async IAsyncEnumerable<RenderedPage> ReadPagesAsync(
        byte[] pdfBytes,
        PdfOcrOptions options,
        bool imagesRequired,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new PdfProcessingException("The PDF input is empty. Provide the bytes of a valid PDF document.");
        cancellationToken.ThrowIfCancellationRequested();

        bool autoDpi = options.Dpi == PdfOcrOptions.AutoDpi;

        // The first reader renders at the fixed DPI, or (auto DPI) only probes page sizes. DocLib.Instance is a
        // process-wide singleton and is never disposed here.
        IDocReader primary = Open(pdfBytes, options.Password, autoDpi ? ProbeScale : options.Dpi / 72.0);
        IDocReader? renderReader = autoDpi ? null : primary;
        int renderReaderDpi = autoDpi ? 0 : options.Dpi;

        try
        {
            IReadOnlyList<int> pages = SelectPages(primary, options);
            int total = pages.Count;

            var channel = Channel.CreateBounded<RenderedPage>(new BoundedChannelOptions(RenderAheadCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken renderToken = renderCts.Token;

            Task renderTask = Task.Run(async () =>
            {
                try
                {
                    byte[]? buffer = null;
                    foreach (int pageNumber in pages)
                    {
                        renderToken.ThrowIfCancellationRequested();

                        int dpi = options.Dpi;
                        if (autoDpi)
                        {
                            var (widthPt, heightPt) = ReadPageSizePoints(primary, pageNumber);
                            dpi = ResolveAutoDpi(widthPt, heightPt);
                            if (renderReader is null || renderReaderDpi != dpi)
                            {
                                // A Docnet reader has one fixed scale; keep only the reader for the current DPI.
                                renderReader?.Dispose();
                                renderReader = null;
                                renderReader = Open(pdfBytes, options.Password, dpi / 72.0);
                                renderReaderDpi = dpi;
                            }
                        }

                        RenderedPage page;
                        (page, buffer) = RenderPage(renderReader!, pageNumber, total, dpi, options, imagesRequired, buffer);
                        try
                        {
                            await channel.Writer.WriteAsync(page, renderToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            page.Dispose();
                            throw;
                        }
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, CancellationToken.None);

            bool completed = false;
            try
            {
                while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var page))
                        yield return page;
                }

                // The writer completes normally even when rendering failed; awaiting the task surfaces that error.
                await renderTask.ConfigureAwait(false);
                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    // The consumer stopped early, failed, or was cancelled: stop rendering and free queued pages.
                    renderCts.Cancel();
                    try
                    {
                        await renderTask.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Already unwinding (or abandoned); the original exception, if any, wins.
                    }
                    while (channel.Reader.TryRead(out var leftover))
                        leftover.Dispose();
                }
            }
        }
        finally
        {
            if (renderReader is not null && !ReferenceEquals(renderReader, primary))
                renderReader.Dispose();
            primary.Dispose();
        }
    }

    /// <summary>
    /// The per-page auto DPI: <c>clamp(4000 / longestSideInches, 150, 400)</c>, rounded down.
    /// </summary>
    internal static int ResolveAutoDpi(double widthPt, double heightPt)
    {
        double longestInches = Math.Max(widthPt, heightPt) / 72.0;
        if (!(longestInches > 0)) return AutoDpiMax;
        double dpi = Math.Floor(AutoDpiTargetLongSidePixels / longestInches);
        return (int)Math.Clamp(dpi, AutoDpiMin, AutoDpiMax);
    }

    /// <summary>
    /// Converts PDFium's BGRA output straight into an <see cref="Rgb24"/> image, flattening alpha onto white with
    /// the same integer formula as Docnet's <c>NaiveTransparencyRemover</c>, so the pixels are byte-identical to
    /// the former remove-transparency-then-<c>CloneAs</c> path.
    /// </summary>
    internal static Image<Rgb24> ConvertBgraToRgb24(ReadOnlySpan<byte> bgra, int width, int height)
    {
        int pixels = checked(width * height);
        if (bgra.Length < pixels * 4)
            throw new ArgumentException($"The BGRA buffer holds {bgra.Length} bytes but {width}x{height} requires {pixels * 4}.", nameof(bgra));

        var image = new Image<Rgb24>(width, height);
        image.DangerousTryGetSinglePixelMemory(out Memory<Rgb24> memory);
        Span<byte> dst = MemoryMarshal.AsBytes(memory.Span);
        byte[] opaque = s_opaque;

        for (int s = 0, d = 0; d < pixels * 3; s += 4, d += 3)
        {
            int a = bgra[s + 3];
            if (a == 255)
            {
                dst[d] = opaque[bgra[s + 2]];
                dst[d + 1] = opaque[bgra[s + 1]];
                dst[d + 2] = opaque[bgra[s]];
            }
            else
            {
                int background = 255 * (255 - a);
                dst[d] = (byte)((bgra[s + 2] * a + background) >> 8);
                dst[d + 1] = (byte)((bgra[s + 1] * a + background) >> 8);
                dst[d + 2] = (byte)((bgra[s] * a + background) >> 8);
            }
        }
        return image;
    }

    private static byte[] BuildOpaqueTable()
    {
        var table = new byte[256];
        for (int c = 0; c < 256; c++) table[c] = (byte)((c * 255) >> 8);
        return table;
    }

    private static IDocReader Open(byte[] pdfBytes, string? password, double scale)
    {
        // Opening can fail on a corrupt, truncated, non-PDF, or encrypted document; surface those as a typed, clear
        // error. A password is always passed (string.Empty when none) so one code path handles both cases.
        try
        {
            return DocLib.Instance.GetDocReader(pdfBytes, password ?? string.Empty, new PageDimensions(scale));
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw new PdfProcessingException(DescribeLoadFailure(ex, password), ex);
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException)
        {
            throw new PdfProcessingException(
                "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.", ex);
        }
    }

    private static IReadOnlyList<int> SelectPages(IDocReader reader, PdfOcrOptions options)
    {
        int count;
        try
        {
            count = reader.GetPageCount();
        }
        catch (DocnetException ex)
        {
            throw new PdfProcessingException("The PDF page count could not be read; the document may be corrupt.", ex);
        }

        // With no range this is 1..count; with a range it is the clamped, de-duplicated selection. Syntax was
        // already validated in PdfOcrOptions.Validate().
        IReadOnlyList<int> pages = PdfPageRange.Parse(options.PageRange, count);

        if (options.HasPageRange && pages.Count == 0)
        {
            throw new PdfProcessingException(
                $"PageRange '{options.PageRange}' selected no pages within this {count}-page document. " +
                "Use 1-based page numbers within the document's range.");
        }

        // MaxPages caps how many of the SELECTED pages are processed (the whole document when no range).
        if (options.MaxPages > 0 && pages.Count > options.MaxPages)
        {
            string scope = options.HasPageRange
                ? $"PageRange '{options.PageRange}' selects {pages.Count} pages"
                : $"The PDF has {pages.Count} pages";
            throw new PdfProcessingException(
                $"{scope}, exceeding the limit of {options.MaxPages} (PdfOcrOptions.MaxPages). " +
                "Raise the limit, narrow the page range, or split the document.");
        }

        return pages;
    }

    private static (double WidthPt, double HeightPt) ReadPageSizePoints(IDocReader probe, int pageNumber)
    {
        try
        {
            using var pageReader = probe.GetPageReader(pageNumber - 1);
            return (pageReader.GetPageWidth() / ProbeScale, pageReader.GetPageHeight() / ProbeScale);
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
        {
            throw new PdfProcessingException($"Page {pageNumber} of the PDF could not be read; the document may be corrupt.", ex);
        }
    }

    private static (RenderedPage Page, byte[]? Buffer) RenderPage(
        IDocReader reader, int pageNumber, int total, int dpi, PdfOcrOptions options, bool imagesRequired, byte[]? buffer)
    {
        // Any failure here is a document/PDFium problem, kept apart from the consumer's OCR so genuine OCR errors
        // are never mislabeled as a PDF-rendering error.
        try
        {
            using var pageReader = reader.GetPageReader(pageNumber - 1); // Docnet/PDFium page indices are 0-based.
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();

            long maxPagePixels = options.MaxPagePixels;
            if (maxPagePixels > 0 && (long)width * height > maxPagePixels)
            {
                throw new PdfProcessingException(
                    $"Page {pageNumber} renders to {width}x{height} ({(long)width * height:N0} px) at {dpi} DPI, " +
                    $"exceeding the per-page limit of {maxPagePixels:N0} px (PdfOcrOptions.MaxPageMegapixels). " +
                    "Lower the DPI or raise the limit.");
            }

            IReadOnlyList<OcrLine>? embeddedLines = null;
            TimeSpan embeddedDuration = TimeSpan.Zero;
            if (options.TextLayer != PdfTextLayerMode.Ignore)
            {
                var sw = Stopwatch.StartNew();
                embeddedLines = EmbeddedTextLayer.SelectLines(ReadCharacters(pageReader, dpi / 72.0), options.TextLayer, width, height);
                embeddedDuration = sw.Elapsed;
            }

            Image<Rgb24>? image = null;
            if (embeddedLines is null || imagesRequired)
            {
                // PDFium renders BGRA (stride = 4 x width) with a transparent background; reuse one buffer across
                // pages and flatten onto white during the Rgb24 conversion.
                int byteCount = checked(width * height * 4);
                if (buffer is null || buffer.Length < byteCount)
                    buffer = new byte[byteCount];
                pageReader.WriteImageToBuffer((RenderFlags)0, buffer);
                image = ConvertBgraToRgb24(buffer.AsSpan(0, byteCount), width, height);
            }

            var page = new RenderedPage
            {
                PageNumber = pageNumber,
                PageCount = total,
                Dpi = dpi,
                PixelWidth = width,
                PixelHeight = height,
                Image = image,
                EmbeddedLines = embeddedLines,
                EmbeddedTextDuration = embeddedDuration,
            };
            return (page, buffer);
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new PdfProcessingException($"Page {pageNumber} of the PDF could not be rendered; the document may be corrupt.", ex);
        }
    }

    /// <summary>
    /// Reads the page's characters. Docnet reports boxes via <c>FPDF_PageToDevice</c> at the reader's scale, i.e.
    /// already in rendered-page pixels (verified: the same glyph sits at x=163 at 72 DPI and x=453 at 200 DPI);
    /// only the font size (points) needs converting.
    /// </summary>
    private static List<PdfTextChar> ReadCharacters(IPageReader pageReader, double pixelsPerPoint)
    {
        var chars = new List<PdfTextChar>();
        foreach (var c in pageReader.GetCharacters())
        {
            var box = c.Box;
            chars.Add(new PdfTextChar(
                c.Char,
                Math.Min(box.Left, box.Right),
                Math.Min(box.Top, box.Bottom),
                Math.Max(box.Left, box.Right),
                Math.Max(box.Top, box.Bottom),
                c.FontSize > 0 ? c.FontSize * pixelsPerPoint : 0));
        }
        return chars;
    }

    /// <summary>
    /// Maps a Docnet load failure to a clear, actionable message — distinguishing a password problem (so the
    /// caller knows to set/fix <see cref="PdfOcrOptions.Password"/>) from generic corruption.
    /// </summary>
    private static string DescribeLoadFailure(DocnetLoadDocumentException ex, string? password)
    {
        bool passwordProblem = ex.ErrorCode == FpdfErrPassword
            || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

        if (passwordProblem)
        {
            return string.IsNullOrEmpty(password)
                ? "The PDF is encrypted and requires a password. Set PdfOcrOptions.Password to open it."
                : "The PDF is encrypted and the supplied PdfOcrOptions.Password is incorrect. Check the password and try again.";
        }

        return "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.";
    }
}
