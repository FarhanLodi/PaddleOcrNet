using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Xunit;

namespace PaddleOcrNet.Tests;

[Trait("Category", "Integration")]
public sealed class RecognizerResidencyIntegrationTests
{
    [SkippableFact]
    public async Task CapOneEvictsAndReloadsWithoutInvalidatingBorrowedSpecialistFacade()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("PADDLEOCRNET_RUN_INTEGRATION") is "1" or "true" or "TRUE",
            "Set PADDLEOCRNET_RUN_INTEGRATION=1 to verify real recognizer eviction/reload.");
        string root = OcrDatasetBenchmarkTests.RepoRoot;
        string model = Path.Combine(root, "onnx_models", "rec", "PP-OCRv5_mobile_rec_infer.onnx");
        string dict = Path.Combine(root, "onnx_models", "rec", "ppocrv5_dict.txt");
        Skip.IfNot(File.Exists(model) && File.Exists(dict), "Local default recognizer/dictionary unavailable.");
        var options = new PaddleEngineOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cpu, IntraOpNumThreads = 2,
            RecognitionModelPath = model, RecognitionDictionaryPath = dict,
        };
        await using var unlimited = new PaddleOcrEngine(options, null);
        await using var bounded = new PaddleOcrEngine(options with { MaxResidentRecognizers = 1 }, null);
        using var crop = Image.Load<Rgb24>(Path.Combine(root, "test", "Assets", "recognition", "mixed_space_issue8.png"));
        var recognition = new RecognitionOptions { SpaceRecoveryThreshold = .15 };
        var expectedDefault = (await unlimited.GetSharedRecognizerAsync(["ch"], default)).Recognize([crop], recognition)[0];
        var expectedEnglish = (await unlimited.GetSharedRecognizerAsync(["en"], default)).Recognize([crop], recognition)[0];
        var borrowedDefault = await bounded.GetSharedRecognizerAsync(["ch"], default);
        var borrowedEnglish = await bounded.GetSharedRecognizerAsync(["en"], default);
        for (int i = 0; i < 2; i++)
        {
            var actualDefault = borrowedDefault.Recognize([crop], recognition)[0];
            var actualEnglish = borrowedEnglish.Recognize([crop], recognition)[0];
            Assert.Equal("结算 Pay 21190", actualDefault.Text);
            Assert.Equal(expectedDefault.Text, actualDefault.Text);
            Assert.Equal(expectedEnglish.Text, actualEnglish.Text);
            Assert.InRange(Math.Abs(expectedDefault.Confidence - actualDefault.Confidence), 0, .00001);
            Assert.InRange(Math.Abs(expectedEnglish.Confidence - actualEnglish.Confidence), 0, .00001);
        }
        // A specialist disposing its borrowed facade must not close the engine's native cache.
        borrowedDefault.Dispose();
        Assert.Equal(expectedDefault.Text, borrowedDefault.Recognize([crop], recognition)[0].Text);
    }
}
