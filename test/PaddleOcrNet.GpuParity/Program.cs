using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PaddleOcrNet.GpuParity <manifest.json> <output-directory>. Requires an actual CUDA provider; fallback is a failure.");
    return 2;
}
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, RespectRequiredConstructorParameters = true };
var results = new List<object>();
string? failure = null;
string[] providers = Array.Empty<string>();
string cache = Path.Combine(output, "models");
ParityManifest? manifest = null;
var options = new RecognitionOptions
{
    UseDocOrientation = false, UseDocUnwarp = false, UseTextLineOrientation = false,
    Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false },
    MaxDegreeOfParallelism = 1, BatchSize = 1, ReturnWordBoxes = true
};
try
{
    string manifestPath = Path.GetFullPath(args[0]);
    manifest = JsonSerializer.Deserialize<ParityManifest>(File.ReadAllText(manifestPath), jsonOptions)
        ?? throw new InvalidDataException("Manifest is null.");
    if (manifest.Images is null || manifest.Images.Length == 0) throw new InvalidDataException("At least one image fixture is required.");
    if (!Enum.TryParse<OcrLanguage>(manifest.Language, ignoreCase: true, out var language) || !Enum.IsDefined(language) || language == OcrLanguage.Auto)
        throw new InvalidDataException("Specify a supported explicit OcrLanguage enum name, not Auto.");
    if (!double.IsFinite(manifest.MaximumCer) || manifest.MaximumCer < 0 || manifest.MaximumCer > 1
        || !double.IsFinite(manifest.MaximumGeometryPixels) || manifest.MaximumGeometryPixels < 0
        || !double.IsFinite(manifest.MaximumConfidenceDelta) || manifest.MaximumConfidenceDelta < 0 || manifest.MaximumConfidenceDelta > 1)
        throw new InvalidDataException("CER/confidence tolerances must be finite in [0,1]; geometry tolerance must be finite and nonnegative.");
    var fixtures = manifest.Images.Select(path => Path.GetFullPath(path, Path.GetDirectoryName(manifestPath)!)).ToArray();
    foreach (var fixture in fixtures) if (!File.Exists(fixture)) throw new FileNotFoundException("Required parity fixture is missing.", fixture);
    providers = OrtEnv.Instance().GetAvailableProviders();
    if (!providers.Contains("CUDAExecutionProvider", StringComparer.Ordinal))
        throw new InvalidOperationException("Loaded ONNX Runtime has no CUDAExecutionProvider. GPU validation cannot pass on CPU.");
    await using var cpu = new PaddleOcrService(new PaddleOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cpu, ModelCachePath = cache, IntraOpNumThreads = 1 });
    await using var cuda = new PaddleOcrService(new PaddleOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cuda, ModelCachePath = cache, IntraOpNumThreads = 1 });
    foreach (var fixture in fixtures)
    {
        var reference = await cpu.ExtractTextFromImage(fixture, language, options);
        var candidate = await cuda.ExtractTextFromImage(fixture, language, options);
        bool actualProviders = reference.ExecutionProvider == OcrExecutionProvider.Cpu
            && !reference.UsedGpu && candidate.ExecutionProvider == OcrExecutionProvider.Cuda && candidate.UsedGpu;
        bool nonempty = !string.IsNullOrWhiteSpace(reference.FullText) && reference.Lines.Count > 0
            && !string.IsNullOrWhiteSpace(candidate.FullText) && candidate.Lines.Count > 0;
        double cer = CharacterError(reference.FullText, candidate.FullText);
        double geometry = GeometryDifference(reference, candidate);
        double confidence = reference.Lines.Count == candidate.Lines.Count
            ? reference.Lines.Zip(candidate.Lines).Select(pair => Math.Abs(pair.First.Confidence - pair.Second.Confidence)).DefaultIfEmpty(0).Max()
            : double.PositiveInfinity;
        bool passed = actualProviders && nonempty && cer <= manifest.MaximumCer
            && geometry <= manifest.MaximumGeometryPixels && confidence <= manifest.MaximumConfidenceDelta;
        results.Add(new
        {
            Fixture = fixture, Sha256 = HashFile(fixture),
            CpuProvider = reference.ExecutionProvider.ToString(), CudaProvider = candidate.ExecutionProvider.ToString(),
            ActualProviders = actualProviders, Nonempty = nonempty, Cer = cer,
            MaximumGeometryDisplacementPixels = double.IsFinite(geometry) ? (double?)geometry : null,
            MaximumConfidenceDelta = double.IsFinite(confidence) ? (double?)confidence : null,
            CpuText = reference.FullText, CudaText = candidate.FullText,
            CpuLineCount = reference.Lines.Count, CudaLineCount = candidate.Lines.Count, Passed = passed
        });
        if (!passed) failure = "At least one fixture failed provider, nonempty-output, full-transcription, geometry or confidence parity requirements.";
    }
}
catch (Exception ex) { failure = ex.ToString(); }

var assets = Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
    .Where(path => Path.GetExtension(path) is ".onnx" or ".txt")
    .Order(StringComparer.Ordinal).Select(path => new { Path = Path.GetRelativePath(cache, path), Sha256 = HashFile(path) }).ToArray() : [];
File.WriteAllText(Path.Combine(output, "parity.json"), JsonSerializer.Serialize(new
{
    Passed = failure is null && results.Count > 0, Failure = failure, Manifest = manifest, Options = options,
    Providers = providers, Runtime = Environment.Version.ToString(), OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    OnnxRuntimeVersion = typeof(OrtEnv).Assembly.GetName().Version?.ToString(),
    Assets = assets, Results = results, Reference = "CPU output is the parity reference, not annotated ground truth. No speed claim is made."
}, jsonOptions));
if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
Console.WriteLine($"CUDA parity passed for {results.Count} fixture(s). Report: {Path.Combine(output, "parity.json")}");
return 0;

static double CharacterError(string reference, string candidate)
{
    var a = reference.EnumerateRunes().ToArray(); var b = candidate.EnumerateRunes().ToArray();
    if (a.Length == 0) return b.Length == 0 ? 0 : 1;
    var previous = Enumerable.Range(0, b.Length + 1).ToArray();
    var current = new int[b.Length + 1];
    for (int i = 1; i <= a.Length; i++)
    {
        current[0] = i;
        for (int j = 1; j <= b.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        (previous, current) = (current, previous);
    }
    return (double)previous[b.Length] / a.Length;
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static double GeometryDifference(OcrResult reference, OcrResult candidate)
{
    if (reference.SourceWidth != candidate.SourceWidth || reference.SourceHeight != candidate.SourceHeight
        || reference.Lines.Count != candidate.Lines.Count) return double.PositiveInfinity;
    double maximum = 0;
    foreach (var (a, b) in reference.Lines.Zip(candidate.Lines))
    {
        if (a.BoundingPolygon.Count < 3 || a.BoundingPolygon.Count != b.BoundingPolygon.Count || a.Words.Count != b.Words.Count) return double.PositiveInfinity;
        foreach (var (x, y) in a.BoundingPolygon.Zip(b.BoundingPolygon))
            maximum = Math.Max(maximum, Math.Sqrt(Math.Pow(x.X - y.X, 2) + Math.Pow(x.Y - y.Y, 2)));
        foreach (var (x, y) in a.Words.Zip(b.Words))
        {
            var p = x.BoundingBox; var q = y.BoundingBox;
            maximum = Math.Max(maximum, new[] { Math.Abs(p.MinX - q.MinX), Math.Abs(p.MinY - q.MinY), Math.Abs(p.MaxX - q.MaxX), Math.Abs(p.MaxY - q.MaxY) }.Max());
        }
    }
    return maximum;
}

internal sealed record ParityManifest(string[] Images, string Language, double MaximumCer, double MaximumGeometryPixels, double MaximumConfidenceDelta);
