using System.Reflection;
using Docnet.Core;
using Docnet.Core.Models;
using PaddleOcrNet.Models;
using PaddleOcrNet.Pdf;
using PaddleOcrNet.Pdf.Internal;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

public class PdfHybridTests
{
    [Fact]
    public void Native_validation_accepts_supplementary_letters_but_not_private_use()
    {
        Assert.True(HybridTextLayer.IsReliable(Line("\U00020000", 0, 0), 200, 200));
        Assert.False(HybridTextLayer.IsReliable(Line("A\U000F0000", 0, 0), 200, 200));
    }

    private static string InvoicePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PaddleOcrNet.sln"))) root = root.Parent;
        return Path.Combine(root!.FullName, "test", "Assets", "pdf", "invoice_778899.pdf");
    }
    private static OcrLine Line(string text, double x, double y, double w = 100, double h = 10) => new()
    {
        Text = text, Confidence = 0.8, BoundingBox = new(x, y, x + w, y + h),
        BoundingPolygon = new[] { new OcrPoint(x, y), new OcrPoint(x + w, y),
            new OcrPoint(x + w, y + h), new OcrPoint(x, y + h) },
    };

    [Fact]
    public void Sparse_native_header_does_not_hide_scan_and_conflicts_are_retained()
    {
        var native = new[] { Line("Invoice", 0, 0) };
        var fused = HybridTextLayer.Fuse(native, new[] { Line("Inv0ice", 0, 0), Line("Scanned total 123", 0, 30) }, 200, 200);
        Assert.Equal(2, fused.Count);
        Assert.True(fused[0].HasConflict);
        Assert.Equal("Inv0ice", Assert.Single(fused[0].Alternatives).Text);
        Assert.Equal(PdfPageSource.Ocr, fused[1].Source);
    }

    [Fact]
    public void Duplicate_layers_and_matching_visual_text_produce_one_selected_line()
    {
        var native = Line("Total 123", 10, 10);
        var fused = HybridTextLayer.Fuse(new[] { native, native }, new[] { Line("Total  123", 11, 10) }, 200, 200);
        Assert.False(Assert.Single(fused).HasConflict);
        Assert.Single(fused[0].Alternatives);
    }

    [Fact]
    public void Coverage_uses_clipped_union_and_rejects_invalid_geometry()
    {
        var lines = new[] { Line("a", -10, 0, 60, 50), Line("b", 0, 0, 50, 50),
            Line("c", 50, 0, 50, 50), Line("d", double.NaN, 0) };
        Assert.Equal(0.5, HybridTextLayer.UnionCoverage(lines, 100, 100), 8);
    }

    [Fact]
    public void Invalid_native_reading_cannot_suppress_visual_text()
    {
        var fused = HybridTextLayer.Fuse(new[] { Line("broken\ufffd", 0, 0) }, new[] { Line("Readable", 0, 0) }, 200, 200);
        Assert.Equal(PdfPageSource.Ocr, Assert.Single(fused).Source);
    }

    [Fact]
    public void Rotated_region_is_preserved_and_large_visual_region_is_not_suppressed()
    {
        var native = Line("Vertical", 40, 0, 10, 100);
        var fused = HybridTextLayer.Fuse(new[] { native }, new[] { native, Line("Other content", 0, 0, 200, 200) }, 200, 200);
        Assert.Equal(2, fused.Count);
        Assert.Equal(native.BoundingPolygon, fused.First(e => e.Source == PdfPageSource.EmbeddedText).Selected.BoundingPolygon);
    }

    [Fact]
    public async Task Public_hybrid_pipeline_renders_and_merges_native_and_ocr()
    {
        var service = DispatchProxy.Create<IPaddleOcrService, OcrProxy>();
        var proxy = (OcrProxy)(object)service;
        proxy.Result = OcrResult.Empty with { Lines = new[] { Line("Raster only appendix", 10, 2200) }, FullText = "Raster only appendix" };
        var result = await service.ExtractTextFromPdfAsync(InvoicePath(),
            OcrLanguage.English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Hybrid });
        var page = Assert.Single(result.Pages);
        Assert.Equal(1, proxy.Calls);
        Assert.Equal(PdfPageSource.Mixed, page.Source);
        Assert.Contains("INVOICE NUMBER 778899", page.Ocr.FullText);
        Assert.Contains("Raster only appendix", page.Ocr.FullText);
        Assert.Equal(page.Ocr.Lines.Count, page.LineEvidence.Count);
        Assert.Equal("rendered-page-pixels", page.CoordinateFrame);
        Assert.InRange(page.NativeCoverageRatio, 0.001, 1);
    }

    [Theory]
    [InlineData("Total", "TOTAL")]
    [InlineData("1", "\u2460")]
    [InlineData("ab", "a b")]
    public void Meaningful_case_compatibility_and_word_boundary_differences_are_conflicts(string native, string visual)
    {
        var fused = HybridTextLayer.Fuse(new[] { Line(native, 0, 0) }, new[] { Line(visual, 0, 0) }, 200, 200);
        Assert.True(Assert.Single(fused).HasConflict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmapped_coordinates_are_rejected_for_fusion_and_searchable_overlay(bool hybrid)
    {
        var service = DispatchProxy.Create<IPaddleOcrService, OcrProxy>();
        ((OcrProxy)(object)service).Result = OcrResult.Empty with
        {
            Coordinates = new OcrCoordinateProvenance { SourceMappingAvailable = false, Frame = OcrCoordinateFrame.DewarpedPixels },
        };
        if (hybrid)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractTextFromPdfAsync(InvoicePath(), OcrLanguage.English,
                pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Hybrid }));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CreateSearchablePdfAsync(
                await File.ReadAllBytesAsync(InvoicePath()), OcrLanguage.English));
    }

    [Fact]
    public async Task Searchable_pdf_emits_selected_native_text_once_and_preserves_visual_alternative()
    {
        var service = DispatchProxy.Create<IPaddleOcrService, OcrProxy>();
        var bytes = await File.ReadAllBytesAsync(InvoicePath());
        var native = await service.ExtractTextFromPdfAsync(bytes, OcrLanguage.English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });
        var title = native.Pages[0].Ocr.Lines.Single(l => l.Text == "INVOICE NUMBER 778899");
        ((OcrProxy)(object)service).Result = OcrResult.Empty with { Lines = new[] { title }, FullText = title.Text };
        var (result, pdf) = await service.CreateSearchablePdfAsync(bytes, OcrLanguage.English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Hybrid });
        var evidence = Assert.Single(result.Pages[0].LineEvidence, e => e.Selected.Text == title.Text);
        Assert.Single(evidence.Alternatives);
        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        using var page = reader.GetPageReader(0);
        Assert.Equal(1, page.GetText().Split(title.Text, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Conflicting_native_layers_select_one_line_and_record_each_alternative_source()
    {
        var fused = HybridTextLayer.Fuse(new[] { Line("Total123", 0, 0), Line("Total456", 0, 0) },
            new[] { Line("Total789", 0, 0) }, 200, 200);
        var selected = Assert.Single(fused);
        Assert.Equal("Total123", selected.Selected.Text);
        Assert.True(selected.HasConflict);
        Assert.Equal(new[] { "Total456", "Total789" }, selected.Alternatives.Select(l => l.Text));
        Assert.Equal(new[] { PdfPageSource.EmbeddedText, PdfPageSource.Ocr }, selected.AlternativeSources);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Malformed_utf16_native_is_rejected_and_visual_remains_reviewable(bool high)
    {
        string malformed = high ? "a\ud800" : "\udc00a";
        var nativeRejected = HybridTextLayer.Fuse(new[] { Line(malformed, 0, 0) }, new[] { Line("Valid", 0, 0) }, 200, 200);
        Assert.Equal(PdfPageSource.Ocr, Assert.Single(nativeRejected).Source);
        var visual = Assert.Single(HybridTextLayer.Fuse(new[] { Line("Valid", 0, 0) }, new[] { Line(malformed, 0, 0) }, 200, 200));
        Assert.True(visual.HasConflict);
        Assert.Equal(malformed, Assert.Single(visual.Alternatives).Text);
    }

    [Fact]
    public async Task Hybrid_paragraph_grouping_fails_explicitly_before_rendering()
    {
        var service = DispatchProxy.Create<IPaddleOcrService, OcrProxy>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExtractTextFromPdfAsync(new byte[] { 1 }, OcrLanguage.English,
            options: new RecognitionOptions { Grouping = TextGrouping.Paragraph },
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Hybrid }));
        Assert.Equal(0, ((OcrProxy)(object)service).Calls);
    }

    [Fact]
    public async Task Plain_pdf_extraction_declares_unmapped_coordinate_frame()
    {
        var service = DispatchProxy.Create<IPaddleOcrService, OcrProxy>();
        ((OcrProxy)(object)service).Result = OcrResult.Empty with
        {
            Coordinates = new OcrCoordinateProvenance { SourceMappingAvailable = false, Frame = OcrCoordinateFrame.DewarpedPixels },
        };
        var result = await service.ExtractTextFromPdfAsync(InvoicePath(), OcrLanguage.English);
        Assert.Equal("dewarped-page-pixels", Assert.Single(result.Pages).CoordinateFrame);
    }

    public class OcrProxy : DispatchProxy
    {
        public OcrResult Result { get; set; } = OcrResult.Empty;
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IPaddleOcrService.ExtractTextFromImage))
            {
                Calls++;
                Assert.NotEmpty((byte[])args![0]!);
                return Task.FromResult(Result);
            }
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
