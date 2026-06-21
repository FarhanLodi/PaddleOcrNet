using System.Globalization;

namespace PaddleOcrNet.Pdf;

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
