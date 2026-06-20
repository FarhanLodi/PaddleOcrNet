using System.Globalization;

namespace PaddleOcrNet.Pdf;

/// <summary>Progress for PDF processing, reported per page via <see cref="PdfOcrOptions.Progress"/>.</summary>
/// <param name="PageNumber">1-based <i>original</i> PDF page number being processed.</param>
/// <param name="PageCount">Total pages in the document.</param>
public readonly record struct PdfPageProgress(int PageNumber, int PageCount)
{
    /// <summary>Completion fraction (0–1).</summary>
    public double Fraction => PageCount > 0 ? (double)PageNumber / PageCount : 0;
}

/// <summary>Options for rasterizing and OCR-ing PDFs.</summary>
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

    /// <summary>Per-page progress callback. <see cref="PdfPageProgress.PageNumber"/> is the original 1-based PDF page.</summary>
    public IProgress<PdfPageProgress>? Progress { get; set; }

    /// <summary>Per-page pixel budget derived from <see cref="MaxPageMegapixels"/> (0 = unlimited).</summary>
    internal long MaxPagePixels => MaxPageMegapixels <= 0 ? 0 : (long)MaxPageMegapixels * 1_000_000L;

    /// <summary><see langword="true"/> when an explicit <see cref="PageRange"/> selection has been supplied.</summary>
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

/// <summary>
/// Parser for the 1-based <see cref="PdfOcrOptions.PageRange"/> mini-syntax (e.g. <c>"1-3,5,8-"</c>).
/// Pure and document-independent: given the raw string and the document's page count it returns the
/// selected pages as a sorted, de-duplicated list of 1-based page numbers. Separated out so it is
/// unit-testable without a PDF.
/// <para>
/// Parsing happens in two phases: <see cref="ParseTerms"/> turns the string into structured terms and
/// validates syntax <i>without</i> materializing any pages (so syntax can be checked at options-validation
/// time, before the document — and its page count — is known, with no risk of expanding an open-ended range
/// into billions of entries). <see cref="Parse"/> then expands those terms against the real page count.
/// </para>
/// </summary>
internal static class PdfPageRange
{
    /// <summary>One parsed term: an inclusive <c>[Start, End]</c> page span. <see cref="End"/> is
    /// <see langword="null"/> for an open-ended <c>"A-"</c> term (meaning "to the last page").</summary>
    internal readonly record struct Term(int Start, int? End);

    /// <summary>
    /// Validates the syntax of <paramref name="pageRange"/> and returns its structured terms, without
    /// referencing any document. Open-ended terms keep a <see langword="null"/> end (not expanded). Returns an
    /// empty list when <paramref name="pageRange"/> is <see langword="null"/>/blank (meaning "all pages").
    /// </summary>
    /// <exception cref="ArgumentException">The syntax is malformed (see <see cref="PdfOcrOptions.PageRange"/>).</exception>
    public static IReadOnlyList<Term> ParseTerms(string? pageRange)
    {
        if (string.IsNullOrWhiteSpace(pageRange))
            return Array.Empty<Term>();

        var terms = new List<Term>();
        foreach (var rawTerm in pageRange.Split(','))
        {
            var term = rawTerm.Trim();
            if (term.Length == 0)
                throw new ArgumentException(
                    $"Invalid PageRange '{pageRange}': empty term (a stray, leading, or trailing comma).", nameof(pageRange));

            int dash = term.IndexOf('-');
            if (dash < 0)
            {
                // Single page "N" => [N, N].
                int page = ParsePageNumber(term, pageRange);
                terms.Add(new Term(page, page));
                continue;
            }

            if (term.IndexOf('-', dash + 1) >= 0)
                throw new ArgumentException(
                    $"Invalid PageRange '{pageRange}': term '{term}' has more than one '-'.", nameof(pageRange));

            string startText = term[..dash].Trim();
            string endText = term[(dash + 1)..].Trim();

            if (startText.Length == 0 && endText.Length == 0)
                throw new ArgumentException(
                    $"Invalid PageRange '{pageRange}': term '{term}' is a bare '-' with no page numbers.", nameof(pageRange));

            // "-B" => 1..B ;  "A-" => A..end (open) ;  "A-B" => A..B
            int start = startText.Length == 0 ? 1 : ParsePageNumber(startText, pageRange);
            int? end = endText.Length == 0 ? null : ParsePageNumber(endText, pageRange);

            if (end is int e && e < start)
                throw new ArgumentException(
                    $"Invalid PageRange '{pageRange}': range '{term}' ends ({e}) before it starts ({start}).", nameof(pageRange));

            terms.Add(new Term(start, end));
        }

        return terms;
    }

    /// <summary>
    /// Parses <paramref name="pageRange"/> against a document of <paramref name="pageCount"/> pages and
    /// expands it to the concrete selection.
    /// </summary>
    /// <param name="pageRange">
    /// The range string, or <see langword="null"/>/blank to select every page (1..<paramref name="pageCount"/>).
    /// </param>
    /// <param name="pageCount">Total pages in the document (1-based upper bound). Pages above this are clamped/ignored.</param>
    /// <returns>
    /// The selected 1-based page numbers in ascending order, de-duplicated. Empty when the selection lands
    /// entirely outside <c>1..pageCount</c>.
    /// </returns>
    /// <exception cref="ArgumentException">The syntax is malformed (see <see cref="PdfOcrOptions.PageRange"/>).</exception>
    public static IReadOnlyList<int> Parse(string? pageRange, int pageCount)
    {
        if (pageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pageCount), pageCount, "pageCount must be non-negative.");

        var terms = ParseTerms(pageRange);

        // No selection -> all pages.
        if (terms.Count == 0)
        {
            var all = new List<int>(pageCount);
            for (int p = 1; p <= pageCount; p++) all.Add(p);
            return all;
        }

        var selected = new SortedSet<int>();
        foreach (var term in terms)
        {
            int lo = Math.Max(1, term.Start);
            int hi = Math.Min(term.End ?? pageCount, pageCount); // open end => last page
            for (int p = lo; p <= hi; p++) selected.Add(p);
        }

        return selected.Count == 0 ? Array.Empty<int>() : new List<int>(selected);
    }

    private static int ParsePageNumber(string text, string fullRange)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException(
                $"Invalid PageRange '{fullRange}': '{text}' is not a valid page number.", nameof(fullRange));
        if (value < 1)
            throw new ArgumentException(
                $"Invalid PageRange '{fullRange}': page numbers are 1-based and must be >= 1 (saw {value}).", nameof(fullRange));
        return value;
    }
}
