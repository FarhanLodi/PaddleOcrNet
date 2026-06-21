using System.Collections.Generic;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Table;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Unit tests for the SLANeXt "table recognition v2" router: it must dispatch each crop to the wired or
/// wireless recognizer per the table classifier, and dispose everything it owns. The real ONNX models are
/// exercised by the gated <see cref="TableV2IntegrationTests"/>.
/// </summary>
public class TableV2Tests
{
    [Fact]
    public void Router_sends_wired_tables_to_the_wired_recognizer()
    {
        var wired = new MarkerRecognizer("WIRED");
        var wireless = new MarkerRecognizer("WIRELESS");
        var router = new SlaNeXtTableRouter(new FakeClassifier(wireless: false), wired, wireless);

        using var crop = new Image<Rgb24>(8, 8);
        var result = router.Recognize(crop, System.Array.Empty<OcrLine>());

        Assert.Equal("WIRED", result.Html);
    }

    [Fact]
    public void Router_sends_wireless_tables_to_the_wireless_recognizer()
    {
        var wired = new MarkerRecognizer("WIRED");
        var wireless = new MarkerRecognizer("WIRELESS");
        var router = new SlaNeXtTableRouter(new FakeClassifier(wireless: true), wired, wireless);

        using var crop = new Image<Rgb24>(8, 8);
        var result = router.Recognize(crop, System.Array.Empty<OcrLine>());

        Assert.Equal("WIRELESS", result.Html);
    }

    [Fact]
    public void Router_disposes_classifier_and_both_recognizers()
    {
        var classifier = new FakeClassifier(wireless: false);
        var wired = new MarkerRecognizer("WIRED");
        var wireless = new MarkerRecognizer("WIRELESS");
        var router = new SlaNeXtTableRouter(classifier, wired, wireless);

        router.Dispose();

        Assert.True(classifier.Disposed);
        Assert.True(wired.Disposed);
        Assert.True(wireless.Disposed);
    }

    [Fact]
    public void Router_rejects_null_dependencies()
    {
        var rec = new MarkerRecognizer("X");
        Assert.Throws<System.ArgumentNullException>(() => new SlaNeXtTableRouter(null!, rec, rec));
        Assert.Throws<System.ArgumentNullException>(() => new SlaNeXtTableRouter(new FakeClassifier(false), null!, rec));
        Assert.Throws<System.ArgumentNullException>(() => new SlaNeXtTableRouter(new FakeClassifier(false), rec, null!));
    }

    private sealed class FakeClassifier : ITableClassifier
    {
        private readonly bool _wireless;
        public bool Disposed { get; private set; }
        public FakeClassifier(bool wireless) => _wireless = wireless;
        public bool IsWireless(Image<Rgb24> tableCrop) => _wireless;
        public void Dispose() => Disposed = true;
    }

    private sealed class MarkerRecognizer : ITableRecognizer
    {
        private readonly string _marker;
        public bool Disposed { get; private set; }
        public MarkerRecognizer(string marker) => _marker = marker;
        public TableResult Recognize(Image<Rgb24> tableCrop, IReadOnlyList<OcrLine> ocrLines)
            => new(_marker, System.Array.Empty<OcrBoundingBox>());
        public void Dispose() => Disposed = true;
    }
}
