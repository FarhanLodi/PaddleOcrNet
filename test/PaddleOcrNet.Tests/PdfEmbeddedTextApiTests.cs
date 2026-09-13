using Docnet.Core;
using Docnet.Core.Models;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// End-to-end tests of the PDF API that need PDFium but no OCR models: every page read here passes the text-layer
/// quality gate, so the engine is never invoked. They cover the embedded-text path, streaming pages, Stream inputs and
/// outputs, auto DPI, and building a searchable PDF from a born-digital document.
/// </summary>
public class PdfEmbeddedTextApiTests
{
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

    private static string Pdf(string name) => Path.Combine(RepoRoot, "test", "Assets", "pdf", name);

    private static PdfOcrOptions Embedded(string? range = null, int dpi = 200)
        => new() { TextLayer = PdfTextLayerMode.PreferEmbedded, PageRange = range, Dpi = dpi };

    [Fact]
    public async Task Born_digital_page_is_read_from_its_text_layer_in_render_pixel_space()
    {
        await using var service = new PaddleOcrService();

        var result = await service.ExtractTextFromPdfAsync(Pdf("invoice_778899.pdf"), OcrLanguage.English, pdfOptions: Embedded());

        var page = Assert.Single(result.Pages);
        Assert.Equal(PdfPageSource.EmbeddedText, page.Source);
        Assert.Equal(200, page.Dpi);
        Assert.Equal(1653, page.PixelWidth);
        Assert.Contains("INVOICE NUMBER 778899", page.Ocr.FullText);
        Assert.All(page.Ocr.Lines, l => Assert.Equal(1.0, l.Confidence));

        // Docnet reports the first 'I' of "INVOICE" at x = 716 px at 200 DPI.
        var title = page.Ocr.Lines.First(l => l.Text == "INVOICE");
        Assert.InRange(title.BoundingBox.MinX, 710, 722);
        Assert.InRange(title.BoundingBox.MaxX, title.BoundingBox.MinX + 100, 1653);
    }

    [Fact]
    public async Task Auto_dpi_renders_each_page_from_its_size()
    {
        await using var service = new PaddleOcrService();

        var result = await service.ExtractTextFromPdfAsync(Pdf("invoice_778899.pdf"), OcrLanguage.English, pdfOptions: Embedded(dpi: PdfOcrOptions.AutoDpi));

        var page = Assert.Single(result.Pages);
        Assert.Equal(342, page.Dpi);            // A4: floor(4000 / 11.69 in)
        Assert.InRange(page.PixelWidth, 2826, 2828);
        var title = page.Ocr.Lines.First(l => l.Text == "INVOICE");
        Assert.InRange(title.BoundingBox.MinX, 716 * 342 / 200.0 - 6, 716 * 342 / 200.0 + 6);
    }

    [Theory]
    [InlineData("invoice_778899.pdf")]
    [InlineData("Monthly_Report_With_Image.pdf")]
    [InlineData("Quantum_Harvest_Magazine_Article.pdf")]
    [InlineData("README.pdf")]
    public async Task Auto_mode_reads_ordinary_born_digital_pages_from_the_text_layer(string file)
    {
        await using var service = new PaddleOcrService();

        var result = await service.ExtractTextFromPdfAsync(Pdf(file), OcrLanguage.English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        Assert.All(result.Pages, p => Assert.Equal(PdfPageSource.EmbeddedText, p.Source));
    }

    [Fact]
    public async Task Page_stream_yields_only_the_selected_pages_in_order()
    {
        await using var service = new PaddleOcrService();
        var progress = new List<int>();
        var options = Embedded("2,4");
        options.Progress = new SynchronousProgress(p => progress.Add(p.PageNumber));

        var pages = new List<PdfPageResult>();
        await foreach (var page in service.ExtractTextFromPdfPagesAsync(Pdf("PrinceCatalogue.pdf"), OcrLanguage.English, pdfOptions: options))
            pages.Add(page);

        Assert.Equal(new[] { 2, 4 }, pages.Select(p => p.PageNumber));
        Assert.All(pages, p => Assert.Equal(PdfPageSource.EmbeddedText, p.Source));
        Assert.Equal(new[] { 2, 4 }, progress);
    }

    [Fact]
    public async Task Breaking_out_of_the_page_stream_stops_cleanly()
    {
        await using var service = new PaddleOcrService();
        int seen = 0;
        await foreach (var page in service.ExtractTextFromPdfPagesAsync(await File.ReadAllBytesAsync(Pdf("PrinceCatalogue.pdf")), OcrLanguage.English, pdfOptions: Embedded()))
        {
            seen++;
            Assert.Equal(1, page.PageNumber);
            break;
        }
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Page_stream_honors_cancellation()
    {
        await using var service = new PaddleOcrService();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var page in service.ExtractTextFromPdfPagesAsync(Pdf("PrinceCatalogue.pdf"), OcrLanguage.English, pdfOptions: Embedded(), cancellationToken: cts.Token))
            {
                cts.Cancel(); // cancel after the first page; the next MoveNext must observe it
            }
        });
    }

    [Fact]
    public async Task Page_stream_validates_options_eagerly()
    {
        await using var service = new PaddleOcrService();
        Assert.Throws<ArgumentException>(() =>
            service.ExtractTextFromPdfPagesAsync(Pdf("README.pdf"), OcrLanguage.English, pdfOptions: new PdfOcrOptions { PageRange = "5-2" }));
    }

    [Fact]
    public async Task Stream_input_reads_cyrillic_embedded_text()
    {
        await using var service = new PaddleOcrService();
        await using var input = File.OpenRead(Pdf("bilingual_welcome.pdf"));

        var result = await service.ExtractTextFromPdfAsync(input, OcrLanguage.English, pdfOptions: Embedded());

        Assert.Contains("WELCOME GUESTS", result.FullText);
        Assert.Contains("ДОБРО", result.FullText);
    }

    [Fact]
    public async Task Searchable_pdf_streams_to_a_non_seekable_output_and_round_trips_its_text()
    {
        await using var service = new PaddleOcrService();
        await using var input = File.OpenRead(Pdf("invoice_778899.pdf"));
        using var buffer = new MemoryStream();

        var result = await service.CreateSearchablePdfAsync(input, new PdfNonSeekableStream(buffer), OcrLanguage.English, pdfOptions: Embedded(dpi: 100));

        Assert.Equal(PdfPageSource.EmbeddedText, Assert.Single(result.Pages).Source);
        using var reader = DocLib.Instance.GetDocReader(buffer.ToArray(), new PageDimensions(1.0));
        Assert.Equal(1, reader.GetPageCount());
        using var page = reader.GetPageReader(0);
        Assert.InRange(page.GetPageWidth(), 594, 596); // A4 width in points survives the 100 DPI round trip
        Assert.Contains("INVOICE NUMBER 778899", page.GetText());
    }

    [Fact]
    public async Task Searchable_pdf_path_overload_writes_the_file_atomically()
    {
        await using var service = new PaddleOcrService();
        string dir = Path.Combine(Path.GetTempPath(), "PdfEmbeddedTextApiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string output = Path.Combine(dir, "out.pdf");
            await service.CreateSearchablePdfAsync(Pdf("bilingual_welcome.pdf"), output, OcrLanguage.English, pdfOptions: Embedded(dpi: 72));

            Assert.Equal(new[] { output }, Directory.GetFiles(dir)); // no temp file left behind
            using var reader = DocLib.Instance.GetDocReader(await File.ReadAllBytesAsync(output), new PageDimensions(1.0));
            using var page = reader.GetPageReader(0);
            Assert.Contains("ДОБРО", page.GetText());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SynchronousProgress(Action<PdfPageProgress> report) : IProgress<PdfPageProgress>
    {
        public void Report(PdfPageProgress value) => report(value);
    }
}
