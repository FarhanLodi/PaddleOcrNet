using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Export;

/// <summary>Explicit text presentation transformations which preserve the original recognition evidence.</summary>
public static partial class OcrNormalizedExportExtensions
{
    /// <summary>Creates a requested view of FullText without changing lines, words, geometry, or the source result.</summary>
    public static NormalizedTextResult ToNormalizedText(this OcrResult result, TextNormalizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.SuppressLineIndices);
        string text = result.FullText;
        var changes = new List<TextTransformation>();
        void Apply(string kind, string next)
        {
            if (string.Equals(text, next, StringComparison.Ordinal)) return;
            changes.Add(new TextTransformation { Kind = kind, Before = text, After = next });
            text = next;
        }

        if (options.SuppressLineIndices.Count > 0)
        {
            string[] lines = PhysicalLineBreaks().Split(text);
            var suppressed = options.SuppressLineIndices.ToHashSet();
            if (suppressed.Any(i => i < 0 || i >= lines.Length)) throw new ArgumentOutOfRangeException(nameof(options), "Suppressed line index is outside the original physical text lines.");
            // Suppression explicitly includes canonical LF line separators and records that change in the log.
            Apply("suppress-selected-lines", string.Join("\n", lines.Where((_, i) => !suppressed.Contains(i))));
        }
        if (options.CanonicalUnicode) Apply("unicode-nfc", text.Normalize(NormalizationForm.FormC));
        if (options.CleanWhitespace)
            Apply("clean-horizontal-whitespace", string.Join("\n", PhysicalLineBreaks().Split(text).Select(line => HorizontalWhitespace().Replace(line, " ").Trim())));
        if (options.JoinHyphenatedLines) Apply("join-hyphenated-lines", HyphenatedBreaks().Replace(text, "$1$2"));
        return new NormalizedTextResult { RawText = result.FullText, Text = text, Transformations = changes };
    }

    /// <summary>Renders the requested text view as escaped HTML preformatted text. Source markup is always data.</summary>
    public static string ToNormalizedHtml(this OcrResult result, TextNormalizationOptions? options = null)
        => "<!doctype html><html><head><meta charset=\"utf-8\"></head><body><pre>"
            + WebUtility.HtmlEncode(result.ToNormalizedText(options).Text) + "</pre></body></html>";

    [GeneratedRegex("\\r\\n|[\\r\\n\\v\\f\\u0085\\u2028\\u2029]", RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalLineBreaks();
    [GeneratedRegex("[^\\S\\r\\n\\v\\f\\u0085\\u2028\\u2029]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();
    [GeneratedRegex("(\\p{L})-\\r?\\n(\\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenatedBreaks();
}
