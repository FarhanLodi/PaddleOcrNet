using System.Text.RegularExpressions;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace PaddleOcrNet.Intelligence.Offline;

/// <summary>
/// Default <see cref="IOfflineKeyInformationExtractor"/> implementation: a geometry-only field extractor that
/// resolves each requested key from the OCR layout with no model or network call. For every key (in request
/// order) it tries, in precedence order, an inline same-line value, then a value to the right of a standalone
/// label cell, then a value directly below it; the first hit wins and the rest fall through to <c>null</c>.
/// </summary>
public sealed partial class OfflineKeyInformationExtractor : IOfflineKeyInformationExtractor
{
    private readonly IPaddleOcrService _ocr;

    /// <summary>Fraction of the label's height that two boxes must overlap vertically to count as the same row.</summary>
    private const double RowOverlapFraction = 0.5;

    /// <summary>Fraction of the label's width that two boxes must overlap horizontally to count as the same column.</summary>
    private const double ColumnOverlapFraction = 0.5;

    /// <summary>Matches the first inline separator (colon, dash, or run of whitespace) after a label.</summary>
    [GeneratedRegex(@"\s*[:\-]\s*|\s+")]
    private static partial Regex SeparatorRegex();

    /// <summary>Matches any run of whitespace (collapsed to a single space during normalization).</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Creates the extractor.
    /// </summary>
    /// <param name="ocr">
    /// The OCR service used by the <see cref="ExtractAsync(string, IReadOnlyList{string}, CancellationToken)"/>
    /// and image overloads. <see cref="Extract"/> never touches it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ocr"/> is <c>null</c>.</exception>
    public OfflineKeyInformationExtractor(IPaddleOcrService ocr)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        _ocr = ocr;
    }

    /// <inheritdoc />
    public KeyInformationResult Extract(OcrResult ocr, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ValidateKeys(keys);

        // Pre-normalize every line once so the per-key passes are cheap.
        var cells = new List<Cell>(ocr.Lines.Count);
        foreach (var line in ocr.Lines)
        {
            cells.Add(new Cell(line.Text, Normalize(line.Text), line.BoundingBox));
        }

        var fields = new List<ExtractedField>(keys.Count);
        foreach (var key in keys)
        {
            fields.Add(new ExtractedField(key, ResolveValue(cells, key)));
        }

        return new KeyInformationResult
        {
            Fields = fields,
            RawJson = null,
            Usage = null,
            Model = null,
        };
    }

    /// <inheritdoc />
    public async Task<KeyInformationResult> ExtractAsync(string imagePath, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ValidateKeys(keys);

        var result = await _ocr.ExtractTextFromImage(imagePath, OcrLanguage.Auto, options: null, cancellationToken).ConfigureAwait(false);
        return Extract(result, keys);
    }

    /// <inheritdoc />
    public async Task<KeyInformationResult> ExtractAsync(Image<Rgb24> image, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateKeys(keys);

        var result = await _ocr.ExtractTextFromImage(image, OcrLanguage.Auto, options: null, cancellationToken).ConfigureAwait(false);
        return Extract(result, keys);
    }

    /// <summary>
    /// Resolves a single key's value by trying, in precedence order: (1) an inline same-line value, (2) the
    /// nearest value to the right of a standalone label cell, (3) the nearest value below it. Returns the first
    /// non-empty hit, or <c>null</c> when none of the strategies match.
    /// </summary>
    private static string? ResolveValue(IReadOnlyList<Cell> cells, string key)
    {
        string normalizedKey = NormalizeLabel(key);
        if (normalizedKey.Length == 0)
            return null;

        return TryInline(cells, normalizedKey)
            ?? TryRight(cells, normalizedKey)
            ?? TryBelow(cells, normalizedKey);
    }

    /// <summary>
    /// Strategy 1 — inline same-line: finds a cell whose normalized text starts with the key followed by a
    /// separator (<c>:</c>, <c>-</c>, or whitespace) and returns the trimmed remainder of that cell's ORIGINAL
    /// text after the separator. Requires the remainder to be non-empty.
    /// </summary>
    private static string? TryInline(IReadOnlyList<Cell> cells, string normalizedKey)
    {
        foreach (var cell in cells)
        {
            if (!cell.Normalized.StartsWith(normalizedKey, StringComparison.Ordinal))
                continue;

            // The character right after the key must be a separator (else it's a different, longer label).
            string rest = cell.Normalized[normalizedKey.Length..];
            if (rest.Length == 0 || !IsSeparatorStart(rest[0]))
                continue;

            string? value = ValueAfterSeparator(cell.OriginalText, normalizedKey.Length);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Strategy 2 — value to the right: among cells whose normalized text EQUALS the key (a standalone label
    /// cell), picks the nearest cell on the same row (vertical overlap ≥ <see cref="RowOverlapFraction"/> of the
    /// label height, starting to the right of the label) by smallest horizontal gap. Returns its original text.
    /// </summary>
    private static string? TryRight(IReadOnlyList<Cell> cells, string normalizedKey)
    {
        string? best = null;
        double bestGap = double.PositiveInfinity;

        for (int li = 0; li < cells.Count; li++)
        {
            Cell label = cells[li];
            if (!IsStandaloneLabel(label, normalizedKey))
                continue;

            for (int ci = 0; ci < cells.Count; ci++)
            {
                if (ci == li)
                    continue;

                Cell candidate = cells[ci];
                if (candidate.OriginalText.Length == 0)
                    continue;
                if (candidate.Box.MinX <= label.Box.MaxX)
                    continue;
                if (!RowsOverlap(label.Box, candidate.Box))
                    continue;

                double gap = candidate.Box.MinX - label.Box.MaxX;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = candidate.OriginalText.Trim();
                }
            }
        }

        return string.IsNullOrEmpty(best) ? null : best;
    }

    /// <summary>
    /// Strategy 3 — value below: among cells whose normalized text EQUALS the key, picks the nearest cell
    /// directly beneath it (horizontal overlap ≥ <see cref="ColumnOverlapFraction"/> of the label width, starting
    /// below the label) by smallest vertical gap. Returns its original text.
    /// </summary>
    private static string? TryBelow(IReadOnlyList<Cell> cells, string normalizedKey)
    {
        string? best = null;
        double bestGap = double.PositiveInfinity;

        for (int li = 0; li < cells.Count; li++)
        {
            Cell label = cells[li];
            if (!IsStandaloneLabel(label, normalizedKey))
                continue;

            for (int ci = 0; ci < cells.Count; ci++)
            {
                if (ci == li)
                    continue;

                Cell candidate = cells[ci];
                if (candidate.OriginalText.Length == 0)
                    continue;
                if (candidate.Box.MinY <= label.Box.MaxY)
                    continue;
                if (!ColumnsOverlap(label.Box, candidate.Box))
                    continue;

                double gap = candidate.Box.MinY - label.Box.MaxY;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = candidate.OriginalText.Trim();
                }
            }
        }

        return string.IsNullOrEmpty(best) ? null : best;
    }

    /// <summary>
    /// True when <paramref name="cell"/> is a standalone label cell for <paramref name="normalizedKey"/> — its
    /// normalized text equals the key exactly (so the value lives in a neighbouring cell, not inline).
    /// </summary>
    private static bool IsStandaloneLabel(Cell cell, string normalizedKey)
        => string.Equals(cell.Normalized, normalizedKey, StringComparison.Ordinal);

    /// <summary>
    /// True when the two boxes share at least <see cref="RowOverlapFraction"/> of the label's height vertically,
    /// i.e. they sit on the same row.
    /// </summary>
    private static bool RowsOverlap(OcrBoundingBox label, OcrBoundingBox other)
    {
        double overlap = Math.Min(label.MaxY, other.MaxY) - Math.Max(label.MinY, other.MinY);
        return overlap >= RowOverlapFraction * Math.Max(label.Height, 1.0);
    }

    /// <summary>
    /// True when the two boxes share at least <see cref="ColumnOverlapFraction"/> of the label's width
    /// horizontally, i.e. they sit in the same column.
    /// </summary>
    private static bool ColumnsOverlap(OcrBoundingBox label, OcrBoundingBox other)
    {
        double overlap = Math.Min(label.MaxX, other.MaxX) - Math.Max(label.MinX, other.MinX);
        return overlap >= ColumnOverlapFraction * Math.Max(label.Width, 1.0);
    }

    /// <summary>
    /// Returns the trimmed remainder of <paramref name="original"/> after the first inline separator that follows
    /// the label, or <c>null</c> when no separator/value is present. Works on the ORIGINAL (un-lowercased) text so
    /// the returned value preserves its casing.
    /// </summary>
    private static string? ValueAfterSeparator(string original, int normalizedKeyLength)
    {
        // Re-collapse the original to align with the normalized prefix length, then locate the first separator.
        string collapsed = WhitespaceRegex().Replace(original.Trim(), " ");
        if (normalizedKeyLength >= collapsed.Length)
            return null;

        string remainder = collapsed[normalizedKeyLength..];
        var match = SeparatorRegex().Match(remainder);
        if (!match.Success || match.Index != 0)
            return null;

        return remainder[(match.Index + match.Length)..].Trim();
    }

    /// <summary>True when <paramref name="c"/> can begin an inline label/value separator.</summary>
    private static bool IsSeparatorStart(char c) => c is ':' or '-' || char.IsWhiteSpace(c);

    /// <summary>
    /// Normalizes a line for matching: trims, collapses internal whitespace to single spaces, and lowercases.
    /// </summary>
    private static string Normalize(string text)
        => WhitespaceRegex().Replace(text.Trim(), " ").ToLowerInvariant();

    /// <summary>
    /// Normalizes a requested key the same way as a line, additionally stripping a single trailing <c>:</c> so
    /// callers can pass either <c>"Total"</c> or <c>"Total:"</c>.
    /// </summary>
    private static string NormalizeLabel(string key)
    {
        string normalized = Normalize(key);
        if (normalized.EndsWith(':'))
            normalized = normalized[..^1].TrimEnd();
        return normalized;
    }

    /// <summary>
    /// Validates the requested key list (non-null, non-empty, every key non-blank) — mirrors
    /// <c>DocumentIntelligenceEngine.ValidateKeys</c>.
    /// </summary>
    private static void ValidateKeys(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            throw new ArgumentException("At least one key must be requested.", nameof(keys));

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Requested keys must not be null or blank.", nameof(keys));
        }
    }

    /// <summary>
    /// A single OCR line paired with its normalized text and bounding box, computed once per extraction.
    /// </summary>
    private readonly record struct Cell(string OriginalText, string Normalized, OcrBoundingBox Box);
}
