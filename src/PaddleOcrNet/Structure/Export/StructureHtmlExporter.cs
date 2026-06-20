using System.Globalization;
using System.Text;
using PaddleOcrNet.Models;

namespace PaddleOcrNet.Structure;

/// <summary>
/// Renders a full parsed <see cref="StructureResult"/> document to a single, self-contained, semantic
/// HTML5 document — the C# parity for Python PP-StructureV3's <c>save_to_html()</c>. The exporter walks
/// <see cref="StructureResult.Blocks"/> in reading order and maps each block kind to the most natural
/// HTML element (headings, paragraphs, tables, MathJax-wrapped formulas, figure placeholders), escaping
/// all recovered text while emitting pre-built table markup verbatim. Both a string-returning
/// <see cref="ToHtml"/> and a file-writing <see cref="SaveAsHtml"/> are provided. The exporter is pure
/// (apart from <see cref="SaveAsHtml"/>'s single write) and Native-AOT friendly.
/// </summary>
public static class StructureHtmlExporter
{
    // Derived from the assembly version so the generated "generator" meta tag never drifts from the package version.
    private static readonly string Generator =
        "PaddleOcrNet " + (typeof(StructureHtmlExporter).Assembly.GetName().Version?.ToString(3) ?? "");

    /// <summary>
    /// Renders the analyzed document as a single, well-formed HTML5 document. The structure is
    /// <c>&lt;!DOCTYPE html&gt;&lt;html&gt;&lt;head&gt;…&lt;/head&gt;&lt;body&gt;…&lt;/body&gt;&lt;/html&gt;</c>
    /// with a UTF-8 <c>&lt;meta charset&gt;</c>. Blocks are walked in reading order
    /// (<see cref="StructureBlock.Order"/>) and rendered by kind:
    /// <list type="bullet">
    ///   <item>document title → <c>&lt;h1&gt;</c>, section/paragraph titles → <c>&lt;h2&gt;</c>, abstract → <c>&lt;h2&gt;</c>.</item>
    ///   <item>body text / lists / captions / references → <c>&lt;p&gt;</c> (text is HTML-escaped).</item>
    ///   <item>tables → the block's recovered <c>&lt;table&gt;</c> HTML, emitted verbatim when well-formed (otherwise escaped as a fallback).</item>
    ///   <item>formulas → a MathJax display block <c>\[ … \]</c> with the raw LaTeX preserved in a <c>data-latex</c> attribute.</item>
    ///   <item>figures / charts / seals → a <c>&lt;figure&gt;</c> placeholder carrying the region label and bbox in <c>data-*</c> attributes.</item>
    /// </list>
    /// The MathJax CDN <c>&lt;script&gt;</c> is included in the head (behind a comment) so formulas render when
    /// the document is opened online; it is optional and the document is valid without it.
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="title">Optional document title used for the <c>&lt;title&gt;</c> element; defaults to the first title block's text, or "Document".</param>
    /// <returns>The complete HTML document as a UTF-8-encodable string.</returns>
    public static string ToHtml(this StructureResult result, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var ordered = result.Blocks.OrderBy(b => b.Order).ToList();
        string docTitle = title ?? FirstTitle(ordered) ?? "Document";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("  <meta name=\"generator\" content=\"").Append(Esc(Generator)).AppendLine("\">");
        sb.Append("  <title>").Append(Esc(docTitle)).AppendLine("</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; line-height: 1.5; margin: 2rem auto; max-width: 50rem; padding: 0 1rem; }");
        sb.AppendLine("    table { border-collapse: collapse; margin: 1rem 0; }");
        sb.AppendLine("    table, th, td { border: 1px solid #bbb; }");
        sb.AppendLine("    th, td { padding: 0.3rem 0.6rem; text-align: left; }");
        sb.AppendLine("    figure { border: 1px dashed #bbb; padding: 1rem; color: #666; text-align: center; }");
        sb.AppendLine("    .formula { overflow-x: auto; }");
        sb.AppendLine("  </style>");
        // MathJax is optional: it renders the \( … \) / \[ … \] formula markup when the document is opened
        // online. The document remains valid HTML (formulas show as raw LaTeX) when offline or if removed.
        sb.AppendLine("  <!-- MathJax (optional): renders LaTeX formulas when online. Remove if not needed. -->");
        sb.AppendLine("  <script>window.MathJax = { tex: { inlineMath: [['\\\\(', '\\\\)']], displayMath: [['\\\\[', '\\\\]']] } };</script>");
        sb.AppendLine("  <script async src=\"https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-mml-chtml.js\"></script>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        foreach (var block in ordered)
        {
            string chunk = RenderHtml(block);
            if (chunk.Length == 0) continue;
            sb.Append("  ").AppendLine(chunk);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the document via <see cref="ToHtml"/> and writes it to <paramref name="path"/> as UTF-8
    /// (without a byte-order mark, so the file is a clean, portable HTML document).
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="path">Destination file path. Must not be <c>null</c> or empty.</param>
    /// <param name="title">Optional document title; see <see cref="ToHtml"/>.</param>
    public static void SaveAsHtml(this StructureResult result, string path, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, result.ToHtml(title), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Renders a single block to its HTML fragment (no surrounding whitespace); empty when nothing to emit.</summary>
    private static string RenderHtml(StructureBlock block)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                return Heading(block.Text, level: 1);

            case StructureBlockType.Title:
            case StructureBlockType.Abstract:
                return Heading(block.Text, level: 2);

            case StructureBlockType.Table:
                return RenderTable(block);

            case StructureBlockType.Formula:
                return RenderFormula(block);

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
                return RenderFigure(block);

            default:
                // Text, Paragraph, List, captions, headers/footers, references, etc.: plain paragraph text.
                var text = block.Text?.Trim();
                return string.IsNullOrEmpty(text) ? string.Empty : $"<p>{Esc(text)}</p>";
        }
    }

    /// <summary>Formats an <c>&lt;hN&gt;</c> heading; empty when the text is blank.</summary>
    private static string Heading(string? text, int level)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return $"<h{level}>{Esc(text.Trim())}</h{level}>";
    }

    /// <summary>
    /// Emits the table block's recovered HTML verbatim when it is a well-formed, table-rooted fragment;
    /// otherwise falls back to the recovered text (escaped) or, failing that, an empty-table placeholder.
    /// The pre-built markup is NOT escaped — it is already markup — but it is validated first.
    /// </summary>
    private static string RenderTable(StructureBlock block)
    {
        var html = block.TableHtml?.Trim();
        if (!string.IsNullOrEmpty(html) && IsWellFormedTable(html))
        {
            return html;
        }

        // Not a usable <table> fragment: degrade gracefully rather than inject broken markup.
        var text = block.Text?.Trim();
        return string.IsNullOrEmpty(text)
            ? "<table></table>"
            : $"<p>{Esc(text)}</p>";
    }

    /// <summary>
    /// Renders a formula as a MathJax display block (<c>\[ … \]</c>), keeping the raw LaTeX in a
    /// <c>data-latex</c> attribute so the source survives even when MathJax is unavailable. Empty when no LaTeX.
    /// </summary>
    private static string RenderFormula(StructureBlock block)
    {
        var latex = block.Latex?.Trim();
        if (string.IsNullOrEmpty(latex)) return string.Empty;
        // The visible text uses \[ … \] (MathJax display math). The data-latex attribute keeps the raw source.
        return $"<div class=\"formula\" data-latex=\"{Esc(latex)}\">\\[ {Esc(latex)} \\]</div>";
    }

    /// <summary>
    /// Renders a figure / chart / seal as a <c>&lt;figure&gt;</c> placeholder (we have no inline image bytes),
    /// carrying the region label and source-image bbox in <c>data-*</c> attributes and any recovered
    /// caption/text in a <c>&lt;figcaption&gt;</c>.
    /// </summary>
    private static string RenderFigure(StructureBlock block)
    {
        var label = block.Type.ToString();
        var caption = block.Text?.Trim();
        var b = block.Bounds;
        var sb = new StringBuilder();
        sb.Append("<figure data-type=\"").Append(Esc(label)).Append("\" data-bbox=\"")
          .Append(Num(b.MinX)).Append(' ').Append(Num(b.MinY)).Append(' ')
          .Append(Num(b.MaxX)).Append(' ').Append(Num(b.MaxY)).Append("\">");
        sb.Append("<figcaption>").Append(Esc(string.IsNullOrEmpty(caption) ? label : caption)).Append("</figcaption>");
        sb.Append("</figure>");
        return sb.ToString();
    }

    // ---- helpers ----

    /// <summary>Returns the text of the first heading-like block, used to seed the document <c>&lt;title&gt;</c>.</summary>
    private static string? FirstTitle(IEnumerable<StructureBlock> ordered)
    {
        foreach (var b in ordered)
        {
            if ((b.Type is StructureBlockType.DocTitle or StructureBlockType.Title)
                && !string.IsNullOrWhiteSpace(b.Text))
            {
                return b.Text!.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Cheap structural validation that <paramref name="html"/> is a single <c>&lt;table&gt;</c>-rooted
    /// fragment with balanced <c>&lt;table&gt;</c>/<c>&lt;/table&gt;</c> tags. We avoid a full HTML parse
    /// (no dependency) but reject markup that would obviously break the surrounding document.
    /// </summary>
    private static bool IsWellFormedTable(string html)
    {
        // Must start at a <table ...> tag (allow leading whitespace, already trimmed by the caller).
        if (!html.StartsWith("<table", StringComparison.OrdinalIgnoreCase)) return false;
        if (!html.EndsWith("</table>", StringComparison.OrdinalIgnoreCase)) return false;

        int open = CountOccurrences(html, "<table");
        int close = CountOccurrences(html, "</table>");
        if (open == 0 || open != close) return false;

        // Angle brackets must balance overall (a coarse guard against truncated/garbled fragments).
        int lt = CountOccurrences(html, "<");
        int gt = CountOccurrences(html, ">");
        return lt == gt;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string Num(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

    /// <summary>HTML-escapes text content / attribute values (the five XML-significant characters).</summary>
    private static string Esc(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
