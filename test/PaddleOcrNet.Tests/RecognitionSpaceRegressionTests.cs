using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Regression coverage for issue #8: on the synthetic crop <c>test/Assets/recognition/mixed_space_issue8.png</c>
/// (180×34, reading <c>结算 Pay 21190</c>, contributed with the report) PP-OCRv5 mobile decides the space after
/// <c>结算</c> by about 2% against the CTC blank. The winner flips with the batch tensor width — 2.0.4's tight
/// width kept it, the Python-parity 320 px minimum drops it, exactly as Python PaddleOCR 3.x does. Space
/// recovery (<see cref="RecognitionOptions.SpaceRecoveryThreshold"/> at
/// <see cref="RecognitionOptions.RecommendedSpaceRecoveryThreshold"/>) must read the space at every tensor width
/// the crop can land in, whatever it shares a batch with; the default (off) must stay Python-exact.
/// <para>
/// Needs the local <c>onnx_models/rec/PP-OCRv5_mobile_rec_infer.onnx</c> and <c>ppocrv5_dict.txt</c>; gated
/// behind <c>PADDLEOCRNET_RUN_INTEGRATION=1</c>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RecognitionSpaceRegressionTests
{
    private const string Gate = "PADDLEOCRNET_RUN_INTEGRATION";
    private const string Expected = "结算 Pay 21190";

    private static readonly RecognitionOptions Recovering =
        RecognitionOptions.Default with { SpaceRecoveryThreshold = RecognitionOptions.RecommendedSpaceRecoveryThreshold };

    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable(Gate) is "1" or "true" or "TRUE";

    private static string Repo(params string[] parts)
        => Path.Combine(new[] { OcrDatasetBenchmarkTests.RepoRoot }.Concat(parts).ToArray());

    private static (SvtrRecognizer Recognizer, Image<Rgb24> Crop) Load()
    {
        string model = Repo("onnx_models", "rec", "PP-OCRv5_mobile_rec_infer.onnx");
        string dict = Repo("onnx_models", "rec", "ppocrv5_dict.txt");
        Skip.IfNot(File.Exists(model) && File.Exists(dict), "PP-OCRv5 mobile rec model/dict not present under onnx_models/rec.");

        var crop = Image.Load<Rgb24>(Repo("test", "Assets", "recognition", "mixed_space_issue8.png"));
        // The raw RGB24 bytes the reporter published (SHA-256 of the 180×34 pixel buffer).
        var pixels = new byte[crop.Width * crop.Height * 3];
        crop.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
                MemoryMarshal.AsBytes(rows.GetRowSpan(y)).CopyTo(pixels.AsSpan(y * crop.Width * 3));
        });
        Assert.Equal("86ff7735eb51daf578d40c2520f4b96205cd7ef1a78b23d682bcb3207aa85867",
            Convert.ToHexStringLower(SHA256.HashData(pixels)));

        var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        var recognizer = new SvtrRecognizer(new InferenceSession(model, options), CharacterDictionary.LoadLines(dict));
        return (recognizer, crop);
    }

    [SkippableTheory]
    [InlineData(0)]     // alone: the 320 px minimum tensor
    [InlineData(400)]   // batched with a wider line: 564 px tensor
    [InlineData(1200)]  // 1694 px tensor
    public void Space_after_the_chinese_label_is_read_at_every_batch_width(int companionWidth)
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        var (recognizer, crop) = Load();
        using var _ = recognizer;
        using var __ = crop;
        using var companion = new Image<Rgb24>(Math.Max(1, companionWidth), crop.Height, new Rgb24(255, 255, 255));

        var crops = companionWidth > 0 ? new[] { crop, companion } : new[] { crop };
        var reading = recognizer.Recognize(crops, Recovering)[0];

        Assert.Equal(Expected, reading.Text);
    }

    [SkippableFact]
    public void Default_reproduces_python_paddleocr()
    {
        Skip.IfNot(IntegrationEnabled, $"Integration test skipped; set {Gate}=1 to run.");
        var (recognizer, crop) = Load();
        using var _ = recognizer;
        using var __ = crop;

        // Python PaddleOCR 3.x (cv2 INTER_LINEAR, 320 px tensor) reads this crop without the first space.
        var plain = recognizer.Recognize(new[] { crop }, RecognitionOptions.Default)[0];
        var recovered = recognizer.Recognize(new[] { crop }, Recovering)[0];

        Assert.Equal("结算Pay 21190", plain.Text);
        Assert.Equal(plain.Confidence, recovered.Confidence);
    }
}
