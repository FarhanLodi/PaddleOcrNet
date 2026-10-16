using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Diagnostics;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using PaddleOcrNet.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public sealed class LongLineRecognitionIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Reusable_workspace_preserves_mixed_batch_text_and_character_geometry()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        using var wide = new Image<Rgb24>(crop.Width * 2, crop.Height, new Rgb24(255, 255, 255));
        var crops = new[] { crop, wide, crop, crop, wide, crop, crop };
        var options = new RecognitionOptions { BatchSize = 2, SpaceRecoveryThreshold = .15 };
        var baseline = recognizer.RecognizeDetailed(crops, options, 2, true);
        var reused = recognizer.RecognizeDetailed(crops, options with { ReuseTensorBuffers = true }, 2, true);
        for (int i = 0; i < crops.Length; i++)
        {
            Assert.Equal(baseline[i].Text, reused[i].Text);
            Assert.Equal(baseline[i].Confidence, reused[i].Confidence);
            Assert.Equal(baseline[i].Characters, reused[i].Characters);
            Assert.Equal(baseline[i].StepWidth, reused[i].StepWidth);
        }
    }

    [SkippableFact]
    public void PreCanceledRecognitionStopsBeforeOptionsOrInferenceAndSessionRemainsReusable()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => recognizer.RecognizeDetailed([crop],
            new RecognitionOptions { SegmentLongLines = true, LongLineOverlapFraction = double.NaN }, 1, true, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotEmpty(recognizer.Recognize([crop], new RecognitionOptions())[0].Text);
    }

    [SkippableFact]
    public void CancellationAfterFirstBatchPreventsRemainingInferenceRuns()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        using var cancellation = new CancellationTokenSource();
        using var parent = new Activity("recognition-cancellation-test").Start();
        int completed = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PaddleOcrDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.RootId != parent.RootId || activity.OperationName != "paddleocr.stage.recognition_batch") return;
                Interlocked.Increment(ref completed);
                cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);
        Assert.ThrowsAny<OperationCanceledException>(() => recognizer.RecognizeDetailed([crop, crop, crop],
            new RecognitionOptions { BatchSize = 1 }, 1, true, cancellation.Token));
        Assert.Equal(1, completed);
    }

    private static (SvtrRecognizer Recognizer, Image<Rgb24> Crop) Load()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 for real-model segmentation checks.");
        string root = OcrDatasetBenchmarkTests.RepoRoot;
        string model = Path.Combine(root, "onnx_models", "rec", "PP-OCRv5_mobile_rec_infer.onnx");
        string dict = Path.Combine(root, "onnx_models", "rec", "ppocrv5_dict.txt");
        Skip.IfNot(File.Exists(model) && File.Exists(dict), "Local PP-OCRv5 recognition model/dictionary unavailable.");
        Assert.Equal("d253c3cbee6e507828a5271a30ab0ec8ae7c2a99d0cc8e6f844fe380809d22b3",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model))));
        Assert.Equal("9dfc80c50b6cb07399a47a7cf25d11db475fb4ad0e1fc96b2eff6467c8166ff3",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dict))));
        using var sessionOptions = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        var recognizer = new SvtrRecognizer(new InferenceSession(model, sessionOptions), CharacterDictionary.LoadLines(dict));
        return (recognizer, Image.Load<Rgb24>(Path.Combine(root, "test", "Assets", "recognition", "mixed_space_issue8.png")));
    }

    [SkippableFact]
    public void PixelBudgetSplitsEqualWidthCropsWithoutChangingTheirText()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        var crops = Enumerable.Repeat(crop, 5).ToArray();
        var options = new RecognitionOptions { SpaceRecoveryThreshold = .15 };
        var baseline = recognizer.RecognizeDetailed(crops, options, 1, true);
        var bounded = recognizer.RecognizeDetailed(crops, options with { MaxBatchPixels = 48 * 320 }, 2, true);
        for (int i = 0; i < crops.Length; i++)
        {
            Assert.Equal("结算 Pay 21190", bounded[i].Text);
            Assert.Equal(baseline[i].Text, bounded[i].Text);
            Assert.InRange(Math.Abs(baseline[i].Confidence - bounded[i].Confidence), 0, .0001);
        }
        Assert.Throws<ArgumentException>(() => recognizer.Recognize(crops, options with { MaxBatchPixels = 1 }));
    }

    [SkippableFact]
    public void SegmentationOptInPreservesShortLineReadingsAndCharacterPositions()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        var options = new RecognitionOptions { SpaceRecoveryThreshold = .15 };
        var baseline = recognizer.RecognizeDetailed([crop], options, 1, true)[0];
        var enabled = recognizer.RecognizeDetailed([crop], options with { SegmentLongLines = true }, 1, true)[0];
        Assert.Equal(baseline.Text, enabled.Text);
        Assert.Equal(baseline.Confidence, enabled.Confidence);
        Assert.Equal(baseline.StepWidth, enabled.StepWidth);
        Assert.Equal(baseline.Characters, enabled.Characters);
    }

    [SkippableFact]
    public void OverlappingLongLineWindowsRetainRepeatedNumericEvidenceAndSourceGeometry()
    {
        var (recognizer, crop) = Load();
        using var ownedRecognizer = recognizer;
        using var ownedCrop = crop;
        const int repeats = 20;
        const int gap = 20;
        var pixels = new Rgb24[crop.Width * crop.Height];
        crop.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < crop.Height; y++) rows.GetRowSpan(y).CopyTo(pixels.AsSpan(y * crop.Width, crop.Width));
        });
        using var line = new Image<Rgb24>((crop.Width + gap) * repeats, crop.Height, new Rgb24(255, 255, 255));
        line.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < line.Height; y++)
                for (int i = 0; i < repeats; i++)
                    pixels.AsSpan(y * crop.Width, crop.Width).CopyTo(rows.GetRowSpan(y).Slice(i * (crop.Width + gap), crop.Width));
        });
        Assert.True(line.Width * 48d / line.Height > 3200);
        var options = new RecognitionOptions
        {
            SegmentLongLines = true, MaxBatchPixels = 48 * 3200,
            SpaceRecoveryThreshold = .15, MaxDegreeOfParallelism = 1,
        };
        var reading = recognizer.RecognizeDetailed([line], options, 1, true)[0];
        output.WriteLine(reading.Text);
        int numericOccurrences = Regex.Matches(reading.Text, "21190").Count;
        // Smoke tolerance allows two damaged seam tokens. It does not establish a release accuracy gate.
        Assert.InRange(numericOccurrences, repeats - 2, repeats);
        Assert.InRange(Regex.Matches(reading.Text, "Pay").Count, repeats - 2, repeats);
        Assert.NotEmpty(reading.Characters!);
        Assert.Equal(1, reading.StepWidth);
        foreach (var character in reading.Characters!)
        {
            Assert.InRange(character.FirstStep, 0, line.Width - 1);
            Assert.InRange(character.LastStep, character.FirstStep, line.Width - 1);
        }
        var words = WordBoxBuilder.SplitWords(reading.Characters!, reading.StepWidth, line.Width);
        Assert.NotEmpty(words);
        Assert.All(words, word =>
        {
            Assert.InRange(word.Start, 0, line.Width);
            Assert.InRange(word.End, word.Start, line.Width);
        });
        var plain = recognizer.Recognize([line], options)[0];
        Assert.Equal(reading.Text, plain.Text);
        Assert.Equal(reading.Confidence, plain.Confidence);
    }
}
