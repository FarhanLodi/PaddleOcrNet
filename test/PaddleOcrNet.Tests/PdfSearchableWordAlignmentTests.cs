using Docnet.Core;
using Docnet.Core.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Model-dependent check of per-word placement: a searchable PDF built from a test image is re-read with PDFium, and
/// every OCR word's extracted characters must sit inside that word's box. Also measures the same alignment for the
/// old one-run-per-line layer (mean IoU of re-extracted word boxes vs OCR word boxes), and confirms that requesting
/// word boxes leaves line text, confidence and boxes unchanged. Gated behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </summary>
[Trait("Category", "Integration")]
public class PdfSearchableWordAlignmentTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";

    private readonly ITestOutputHelper _output;

    public PdfSearchableWordAlignmentTests(ITestOutputHelper output) => _output = output;

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaddleOcrNet.sln")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new InvalidOperationException("Could not locate repo root (PaddleOcrNet.sln not found).");
        }
    }

    [SkippableTheory]
    [InlineData("paddleocrnet_100_test_dataset/057_receipts_invoices.png", "en")]
    [InlineData("lang_zh_typed.png", "ch")]
    public async Task Searchable_pdf_characters_fall_inside_their_ocr_word_boxes(string imageFile, string language)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run (downloads models).");

        // An image-only PDF at 72 DPI, so page points equal image pixels; OCR it at 72 DPI too.
        int width, height;
        byte[] jpeg, imagePdf;
        using (var image = await Image.LoadAsync<Rgb24>(Path.Combine(RepoRoot, "test", "Assets", imageFile)))
        {
            (width, height) = (image.Width, image.Height);
            jpeg = SearchablePdfBuilder.EncodeJpeg(image, 95);
            imagePdf = BuildPdf(jpeg, width, height, Array.Empty<OcrLine>());
        }

        var lang = OcrLanguageExtensions.FromCode(language);
        var pdfOptions = new PdfOcrOptions { Dpi = 72 };
        await using var service = new PaddleOcrService();

        var plain = await service.ExtractTextFromPdfAsync(imagePdf, lang, pdfOptions: pdfOptions);
        var (result, searchable) = await service.CreateSearchablePdfAsync(imagePdf, lang, pdfOptions: pdfOptions);

        // Word boxes are on by default for searchable output and change nothing else.
        var plainLines = Assert.Single(plain.Pages).Ocr.Lines;
        var lines = Assert.Single(result.Pages).Ocr.Lines;
        Assert.Equal(plainLines.Count, lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            Assert.Empty(plainLines[i].Words);
            Assert.Equal(plainLines[i].Text, lines[i].Text);
            Assert.Equal(plainLines[i].Confidence, lines[i].Confidence);
            Assert.Equal(plainLines[i].BoundingBox, lines[i].BoundingBox);
            Assert.Equal(plainLines[i].BoundingPolygon, lines[i].BoundingPolygon);
        }
        Assert.Contains(lines, l => l.Words.Count > 0);

        // The same OCR written the old way: one run per line.
        byte[] perLine = BuildPdf(jpeg, width, height, lines.Select(l => l with { Words = Array.Empty<OcrWord>() }).ToList());

        var after = Measure(searchable, lines);
        var before = Measure(perLine, lines);

        _output.WriteLine($"{imageFile}: lines={lines.Count} words={after.Words} matchedLines={after.MatchedLines}/{lines.Count}");
        _output.WriteLine($"  per-line runs: meanWordIoU={before.MeanIoU:F3} charsInsideWordBox={before.Inside:P1}");
        _output.WriteLine($"  per-word runs: meanWordIoU={after.MeanIoU:F3} charsInsideWordBox={after.Inside:P1}");

        Assert.True(after.Inside >= 0.95, $"only {after.Inside:P1} of characters sit inside their word box");
        Assert.True(after.MeanIoU >= 0.8, $"mean word IoU {after.MeanIoU:F3}");
        Assert.True(after.MeanIoU >= before.MeanIoU, $"per-word IoU {after.MeanIoU:F3} < per-line IoU {before.MeanIoU:F3}");
    }

    private static byte[] BuildPdf(byte[] jpeg, int width, int height, IReadOnlyList<OcrLine> lines)
    {
        using var ms = new MemoryStream();
        var builder = new SearchablePdfBuilder(ms);
        builder.AddPage(jpeg, width, height, 72, lines);
        builder.Finish();
        return ms.ToArray();
    }

    /// <summary>
    /// Re-extracts the characters of <paramref name="pdf"/> and assigns them, in content order, to the OCR words of
    /// the lines whose words spell out their text (the lines the builder writes word by word). Returns the mean IoU
    /// of each word's extracted character hull vs its OCR box, and the share of characters centred inside their box.
    /// </summary>
    private static (double MeanIoU, double Inside, int Words, int MatchedLines) Measure(byte[] pdf, IReadOnlyList<OcrLine> lines)
    {
        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        using var page = reader.GetPageReader(0);
        var glyphs = page.GetCharacters().Where(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char)).ToList();

        int expected = lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));
        Assert.Equal(expected, glyphs.Count);

        var ious = new List<double>();
        int inside = 0, counted = 0, matchedLines = 0, index = 0;
        foreach (var line in lines)
        {
            int lineChars = line.Text.Count(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
            var words = line.Words;
            var spaces = new bool[Math.Max(1, words.Count)];
            bool forward = words.Count > 0 && SearchablePdfBuilder.MatchWords(line.Text, words, reversed: false, spaces);
            bool backward = !forward && words.Count > 0 && SearchablePdfBuilder.MatchWords(line.Text, words, reversed: true, spaces);
            if (!forward && !backward)
            {
                index += lineChars;
                continue;
            }

            matchedLines++;
            for (int k = 0; k < words.Count; k++)
            {
                var word = words[backward ? words.Count - 1 - k : k];
                var box = word.BoundingBox;
                var own = glyphs.Skip(index).Take(word.Text.Length).ToList();
                index += word.Text.Length;

                foreach (var g in own)
                {
                    double cx = (g.Box.Left + g.Box.Right) / 2.0, cy = (g.Box.Top + g.Box.Bottom) / 2.0;
                    counted++;
                    if (cx >= box.MinX - 2 && cx <= box.MaxX + 2 && cy >= box.MinY - 0.25 * box.Height && cy <= box.MaxY + 0.25 * box.Height)
                        inside++;
                }

                // Glyphless glyphs are drawn from the baseline up to the font size, so compare horizontal extents
                // against the OCR box's vertical extent.
                var hull = new OcrBoundingBox(own.Min(g => g.Box.Left), box.MinY, own.Max(g => g.Box.Right), box.MaxY);
                ious.Add(IoU(hull, box));
            }
        }

        return (ious.Count == 0 ? 0 : ious.Average(), counted == 0 ? 0 : inside / (double)counted, ious.Count, matchedLines);
    }

    private static double IoU(OcrBoundingBox a, OcrBoundingBox b)
    {
        double w = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        double h = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        if (w <= 0 || h <= 0) return 0;
        double inter = w * h;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }
}
