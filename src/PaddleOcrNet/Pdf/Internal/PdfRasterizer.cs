using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Pdf.Internal;

/// <summary>
/// Rasterizes PDF pages to images via PDFium (Docnet.Core). Pages are rendered and handed off one at
/// a time so peak memory stays at roughly a single page regardless of document length.
/// </summary>
internal static class PdfRasterizer
{
    // PDFium load-error code for a password-protected document (FPDF_ERR_PASSWORD). Surfaced on
    // DocnetLoadDocumentException.ErrorCode when the supplied password is missing or wrong.
    private const uint FpdfErrPassword = 4;

    /// <summary>
    /// Renders the pages selected by <paramref name="options"/> (its <see cref="PdfOcrOptions.PageRange"/>,
    /// or every page when none is set) at the configured DPI and invokes <paramref name="handler"/> with the
    /// <b>original 1-based PDF page number</b>, the total number of pages being processed, and the rendered
    /// image (disposed automatically after the handler completes — do not keep a reference to it).
    /// </summary>
    /// <exception cref="PdfProcessingException">
    /// The input is empty; the PDF is encrypted and the supplied <see cref="PdfOcrOptions.Password"/> is
    /// missing/blank/wrong; the page count or a selected page exceeds a configured guard; or the PDF cannot
    /// be opened/rendered (corrupt or not a PDF). Exceptions thrown by <paramref name="handler"/> itself
    /// (e.g. OCR failures) are propagated unchanged.
    /// </exception>
    public static async Task ForEachPageAsync(
        byte[] pdfBytes,
        PdfOcrOptions options,
        Func<int, int, Image<Rgb24>, Task> handler,
        CancellationToken cancellationToken)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new PdfProcessingException("The PDF input is empty. Provide the bytes of a valid PDF document.");

        int dpi = options.Dpi;
        long maxPagePixels = options.MaxPagePixels;

        // PDF user space is 72 dpi; scale up to the requested rendering resolution.
        double scale = dpi / 72.0;

        // DocLib.Instance is a process-wide singleton — never dispose it here. Opening can fail on a
        // corrupt, truncated, non-PDF, or encrypted document; surface those as a typed, clear error
        // instead of leaking a PDFium/Docnet exception to the caller. A password is always passed
        // (string.Empty when none) so the same code path handles protected and unprotected documents.
        IDocReader docReader;
        try
        {
            docReader = DocLib.Instance.GetDocReader(pdfBytes, options.Password ?? string.Empty, new PageDimensions(scale));
        }
        catch (DocnetLoadDocumentException ex)
        {
            throw new PdfProcessingException(DescribeLoadFailure(ex, options.Password), ex);
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException)
        {
            throw new PdfProcessingException(
                "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.", ex);
        }

        using (docReader)
        {
            int count;
            try
            {
                count = docReader.GetPageCount();
            }
            catch (DocnetException ex)
            {
                throw new PdfProcessingException("The PDF page count could not be read; the document may be corrupt.", ex);
            }

            // Resolve which 1-based pages to render. With no range this is 1..count; with a range it is the
            // clamped, de-duplicated selection. Syntax was already validated in PdfOcrOptions.Validate().
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

            int total = pages.Count;
            foreach (int pageNumber in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int pageIndex = pageNumber - 1; // Docnet/PDFium page indices are 0-based.

                // Render this page. Any failure here is a document/PDFium problem (kept separate from the
                // handler call below, so genuine OCR errors are never mislabeled as a PDF-rendering error).
                Image<Rgb24> image;
                try
                {
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();

                    if (maxPagePixels > 0 && (long)width * height > maxPagePixels)
                    {
                        throw new PdfProcessingException(
                            $"Page {pageNumber} renders to {width}x{height} ({(long)width * height:N0} px) at {dpi} DPI, " +
                            $"exceeding the per-page limit of {maxPagePixels:N0} px (PdfOcrOptions.MaxPageMegapixels). " +
                            "Lower the DPI or raise the limit.");
                    }

                    // PDFium emits BGRA with a transparent background; flatten onto white so OCR sees a clean page.
                    byte[] bgra = pageReader.GetImage(new NaiveTransparencyRemover());
                    image = ConvertToRgb24(bgra, width, height);
                }
                catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
                {
                    throw new PdfProcessingException($"Page {pageNumber} of the PDF could not be rendered; the document may be corrupt.", ex);
                }

                using (image)
                {
                    await handler(pageNumber, total, image).ConfigureAwait(false);
                }
            }
        }
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

    private static Image<Rgb24> ConvertToRgb24(byte[] bgra, int width, int height)
    {
        using var bgraImage = Image.LoadPixelData<Bgra32>(bgra, width, height);
        return bgraImage.CloneAs<Rgb24>();
    }
}
