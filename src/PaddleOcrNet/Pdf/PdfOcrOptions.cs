using System.Globalization;

namespace PaddleOcrNet.Pdf;

/// <summary>
/// Options for rasterizing and OCR-ing PDFs.
/// </summary>
public sealed class PdfOcrOptions
{
    /// <summary>
    /// Rendering resolution. Higher = better OCR accuracy but slower and larger searchable PDFs.
    /// 200–300 is a good range for scanned documents. Default 200.
    /// </summary>
    public int Dpi { get; set; } = 200;

    /// <summary>
    /// JPEG quality (1–100) for the page images embedded in a <i>searchable</i> PDF. Lower = smaller
    /// file. Default 75. Ignored when only extracting text.
    /// </summary>
    public int JpegQuality { get; set; } = 75;

    /// <summary>
    /// Maximum number of pages to process. When a <see cref="PageRange"/> is set, this caps how many of
    /// the <i>selected</i> pages are rendered (the first <see cref="MaxPages"/> in document order); when no
    /// range is set it caps the whole document. A document/selection with more pages than this is rejected
    /// before any page is rendered — a guard against a malicious document forcing unbounded CPU/time.
    /// Default 5000. Set to 0 for no limit.
    /// </summary>
    public int MaxPages { get; set; } = 5000;

    /// <summary>
    /// Maximum rendered megapixels per page (width × height at the chosen <see cref="Dpi"/>). A page that
    /// would exceed this is rejected before its bitmap is allocated — a guard against a large page box at
    /// high DPI exhausting memory. Default 200 (≈ an A3 page at 600 DPI). Set to 0 for no limit.
    /// </summary>
    public int MaxPageMegapixels { get; set; } = 200;

    /// <summary>
    /// Password for an encrypted (password-protected) PDF. Leave <see langword="null"/> (the default) for an
    /// unprotected document. If the PDF is encrypted and this is <see langword="null"/>, blank, or wrong, a
    /// <see cref="PdfProcessingException"/> is thrown when the document is opened. Both the user/open password
    /// and (for some documents) the owner password are accepted, as supported by the underlying PDF engine.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Optional 1-based page selection, e.g. <c>"1-3,5,8-"</c>. When <see langword="null"/> or blank
    /// (the default) every page is processed.
    /// <para>
    /// Syntax: a comma-separated list of <i>terms</i>, each one of:
    /// <list type="bullet">
    /// <item><description><c>N</c> — a single page (e.g. <c>5</c>).</description></item>
    /// <item><description><c>A-B</c> — an inclusive range (e.g. <c>1-3</c> = pages 1, 2, 3).</description></item>
    /// <item><description><c>A-</c> — open-ended: page <c>A</c> through the last page (e.g. <c>8-</c>).</description></item>
    /// <item><description><c>-B</c> — from page 1 through <c>B</c> (e.g. <c>-3</c> = pages 1, 2, 3).</description></item>
    /// </list>
    /// Whitespace around terms and dashes is ignored. Pages are de-duplicated and processed in ascending
    /// document order regardless of the order they appear in the string. Page numbers beyond the document's
    /// page count are clamped/ignored gracefully (a range partly outside the document keeps its in-range
    /// part; a term entirely outside contributes nothing). Pages are 1-based.
    /// </para>
    /// <para>
    /// Malformed syntax (non-numeric terms, a reversed range such as <c>5-2</c>, a page number <c>&lt; 1</c>,
    /// an empty term such as a trailing/leading comma, or a bare <c>-</c>) throws
    /// <see cref="ArgumentException"/> when the options are validated.
    /// </para>
    /// <para>
    /// Interacts with <see cref="MaxPages"/>: the selection is computed first, then at most
    /// <see cref="MaxPages"/> of the selected pages (in document order) are processed; if the selection
    /// exceeds that cap the whole call is rejected with <see cref="PdfProcessingException"/>.
    /// </para>
    /// </summary>
    public string? PageRange { get; set; }

    /// <summary>
    /// Per-page progress callback. <see cref="PdfPageProgress.PageNumber"/> is the original 1-based PDF page.
    /// </summary>
    public IProgress<PdfPageProgress>? Progress { get; set; }

    /// <summary>
    /// Per-page pixel budget derived from <see cref="MaxPageMegapixels"/> (0 = unlimited).
    /// </summary>
    internal long MaxPagePixels => MaxPageMegapixels <= 0 ? 0 : (long)MaxPageMegapixels * 1_000_000L;

    /// <summary>
    /// <see langword="true"/> when an explicit <see cref="PageRange"/> selection has been supplied.
    /// </summary>
    internal bool HasPageRange => !string.IsNullOrWhiteSpace(PageRange);

    internal void Validate()
    {
        if (Dpi is < 36 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(Dpi), Dpi, "Dpi must be between 36 and 1200.");
        if (JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "JpegQuality must be between 1 and 100.");
        if (MaxPages < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPages), MaxPages, "MaxPages must be 0 (unlimited) or positive.");
        if (MaxPageMegapixels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPageMegapixels), MaxPageMegapixels, "MaxPageMegapixels must be 0 (unlimited) or positive.");

        // Fail fast on clearly-invalid range syntax, independent of any document. ParseTerms validates the
        // syntax without expanding ranges (the page count is unknown here); clamping/expansion to the real
        // count happens at render time.
        if (HasPageRange)
            _ = PdfPageRange.ParseTerms(PageRange);
    }
}
