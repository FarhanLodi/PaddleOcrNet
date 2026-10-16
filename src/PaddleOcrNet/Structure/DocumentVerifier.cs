using System.Globalization;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>Deterministic caller-bound relationship checks. Contradictions request review; values are never corrected.</summary>
public static class DocumentVerifier
{
    /// <summary>Verify bounded rules and propose bounded source-region rereads. Decimal operands must be complete
    /// values in the explicitly supplied culture; no label, unit or currency extraction is attempted.</summary>
    public static DocumentVerificationReport Verify(DocumentStructure document, IEnumerable<DocumentVerificationRule> rules,
        DocumentVerificationPolicy? policy = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rules);
        policy ??= new();
        if (policy.MaxRules is < 1 or > 10000 || policy.MaxReferences is < 1 or > 100000 ||
            policy.MaxRereadRequests is < 0 or > 10000 || policy.MaxRereadPixels < 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "Verification admission limits are out of range.");
        cancellationToken.ThrowIfCancellationRequested();
        var pages = document.Pages.ToDictionary(p => p.PageNumber);
        var checks = new List<DocumentVerificationCheck>();
        var requests = new List<DocumentRereadRequest>();
        var skips = new List<DocumentRereadSkip>();
        var handled = new HashSet<DocumentBlockReference>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int references = 0;
        long pixels = 0;
        bool truncated = false;
        using var enumerator = rules.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext()) break;
            if (checks.Count >= policy.MaxRules) { truncated = true; break; }
            var rule = enumerator.Current;
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id) || rule.Inputs is null ||
                rule.Inputs.Count == 0 || !Enum.IsDefined(rule.Kind) || rule.AbsoluteTolerance < 0)
                throw new ArgumentException("Rules require unique nonblank IDs, inputs, a defined kind and nonnegative tolerance.", nameof(rules));
            if ((long)rule.Inputs.Count + 1 > policy.MaxReferences - references)
            {
                checks.Add(new() { RuleId = rule.Id, Status = DocumentVerificationStatus.NotEvaluated, Reason = "reference-budget" });
                continue;
            }
            var sources = rule.Inputs.Append(rule.Expected).ToArray();
            if (sources.Distinct().Count() != sources.Length)
                throw new ArgumentException("Rule operands and expected values must reference distinct source blocks.", nameof(rules));
            references += sources.Length;
            var readings = sources.Select(Read).ToArray();
            var (status, reason) = Compare(rule, readings);
            checks.Add(new() { RuleId = rule.Id, Status = status, Reason = reason, Readings = readings });
            if (status is DocumentVerificationStatus.Consistent) continue;
            foreach (var reading in readings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!handled.Add(reading.Source)) continue;
                if (!reading.Found) { Skip("missing-source"); continue; }
                var page = pages[reading.Source.PageNumber].Structure;
                if (page.Coordinates?.IsOriginalFrame != true) { Skip("source-frame-unavailable"); continue; }
                var b = reading.Bounds;
                if (!double.IsFinite(b.MinX) || !double.IsFinite(b.MinY) || !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxY) ||
                    b.Width <= 0 || b.Height <= 0 || b.MinX < 0 || b.MinY < 0 || b.MaxX > page.SourceWidth || b.MaxY > page.SourceHeight)
                { Skip("invalid-source-geometry"); continue; }
                var bounds = new OcrBoundingBox(Math.Floor(b.MinX), Math.Floor(b.MinY), Math.Ceiling(b.MaxX), Math.Ceiling(b.MaxY));
                // Source dimensions are ints, so each individual rectangle's area fits in Int64.
                long area = (long)bounds.Width * (long)bounds.Height;
                if (requests.Count >= policy.MaxRereadRequests || area > policy.MaxRereadPixels - pixels)
                { Skip("reread-budget"); continue; }
                requests.Add(new() { Source = reading.Source, Bounds = bounds, RuleId = rule.Id });
                pixels += area;
                void Skip(string why) => skips.Add(new(reading.Source, rule.Id, why));
            }
        }
        return new() { Checks = checks.ToArray(), RequestedRereads = requests.ToArray(), SkippedRereads = skips.ToArray(),
            RequestedPixels = pixels, RulesTruncated = truncated };

        DocumentVerificationReading Read(DocumentBlockReference source)
        {
            if (!pages.TryGetValue(source.PageNumber, out var page) || source.BlockIndex < 0 || source.BlockIndex >= page.Structure.Blocks.Count)
                return new() { Source = source };
            var block = page.Structure.Blocks[source.BlockIndex];
            return new() { Source = source, Found = true, RawText = block.Text, Bounds = block.Bounds, Coordinates = page.Structure.Coordinates };
        }
    }

    private static (DocumentVerificationStatus Status, string Reason) Compare(DocumentVerificationRule rule,
        IReadOnlyList<DocumentVerificationReading> readings)
    {
        if (readings.Any(r => !r.Found)) return (DocumentVerificationStatus.Unverifiable, "missing-source");
        if (readings.Any(r => string.IsNullOrWhiteSpace(r.RawText))) return (DocumentVerificationStatus.Unverifiable, "empty-reading");
        if (rule.Kind == DocumentVerificationKind.ExactText)
            return readings.Take(readings.Count - 1).All(r => string.Equals(r.RawText, readings[^1].RawText, StringComparison.Ordinal))
                ? (DocumentVerificationStatus.Consistent, "exact-text-agreement")
                : (DocumentVerificationStatus.Contradiction, "text-disagreement");
        var culture = CultureInfo.GetCultureInfo(rule.CultureName);
        const NumberStyles style = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        decimal sum = 0;
        try
        {
            for (int i = 0; i < readings.Count; i++)
            {
                if (!decimal.TryParse(readings[i].RawText, style, culture, out var value) ||
                    CanonicalDecimal(readings[i].RawText!, culture) != CanonicalDecimal(value.ToString("0.############################", culture), culture))
                    return (DocumentVerificationStatus.Unverifiable, "not-a-complete-decimal");
                if (i < readings.Count - 1) sum = checked(sum + value);
                else
                {
                    decimal difference = Math.Abs(checked(sum - value));
                    return difference <= rule.AbsoluteTolerance
                        ? (DocumentVerificationStatus.Consistent, "decimal-sum-agreement")
                        : (DocumentVerificationStatus.Contradiction, "decimal-total-disagreement");
                }
            }
        }
        catch (OverflowException) { return (DocumentVerificationStatus.Unverifiable, "decimal-overflow"); }
        return (DocumentVerificationStatus.Unverifiable, "empty-reading");
    }

    // Decimal.TryParse can round excessive fractional precision. Never let that turn different printed
    // values into an apparent agreement: require an exact representable decimal after lexical normalization.
    private static string CanonicalDecimal(string text, CultureInfo culture)
    {
        text = text.Trim();
        bool negative = text.StartsWith(culture.NumberFormat.NegativeSign, StringComparison.Ordinal);
        if (negative) text = text[culture.NumberFormat.NegativeSign.Length..];
        else if (text.StartsWith(culture.NumberFormat.PositiveSign, StringComparison.Ordinal))
            text = text[culture.NumberFormat.PositiveSign.Length..];
        var parts = text.Split(culture.NumberFormat.NumberDecimalSeparator, StringSplitOptions.None);
        string integer = parts[0].TrimStart('0');
        string fraction = parts.Length > 1 ? parts[1].TrimEnd('0') : "";
        if (integer.Length == 0 && fraction.Length == 0) return "0";
        return (negative ? "-" : "") + integer + "." + fraction;
    }
}
