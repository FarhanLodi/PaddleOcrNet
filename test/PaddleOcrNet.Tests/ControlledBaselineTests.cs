using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaddleOcrNet.Diagnostics;
using PaddleOcrNet.Extraction;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Tests.Evaluation;
using Xunit;
using Xunit.Abstractions;

namespace PaddleOcrNet.Tests;

[CollectionDefinition("Controlled baseline", DisableParallelization = true)]
public sealed class ControlledBaselineCollection { }

[Collection("Controlled baseline")]
[Trait("Category", "Integration")]
public sealed class ControlledBaselineTests(ITestOutputHelper output)
{
    internal sealed record Input(BenchmarkManifest Manifest, Dictionary<string, string> Images,
        string RepositoryRevision, OcrLanguage[]? Languages = null, int WarmRuns = 3,
        int Threads = 1, string? ModelCachePath = null);
    private sealed record Measurement(string PageId, int Run, double Milliseconds, long ManagedAllocatedBytes,
        long WorkingSetBytesAfter, long ProcessLifetimePeakWorkingSetBytes, Dictionary<string, double[]> StageMilliseconds);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [SkippableFact]
    public async Task Run_controlled_cpu_baseline()
    {
        string? inputPath = Environment.GetEnvironmentVariable("PADDLEOCRNET_BASELINE_INPUT");
        Skip.If(string.IsNullOrWhiteSpace(inputPath), "Set PADDLEOCRNET_BASELINE_INPUT to an annotated input JSON file.");
        inputPath = Path.GetFullPath(inputPath!);
        var input = JsonSerializer.Deserialize<Input>(await File.ReadAllTextAsync(inputPath), Json)
            ?? throw new InvalidDataException("Baseline input is empty.");
        if (input.WarmRuns < 3 || input.Threads < 1 || string.IsNullOrWhiteSpace(input.RepositoryRevision))
            throw new InvalidDataException("WarmRuns >= 3, Threads >= 1 and RepositoryRevision are required.");
        var languages = input.Languages ?? [OcrLanguage.English];
        if (languages.Length == 0 || languages.Any(l => !Enum.IsDefined(l)))
            throw new InvalidDataException("Languages must contain valid OCR languages.");
        string root = Path.GetDirectoryName(inputPath)!;
        var images = input.Manifest.Pages.ToDictionary(p => p.Id, p =>
        {
            if (!input.Images.TryGetValue(p.Id, out string? relative) || string.IsNullOrWhiteSpace(relative)
                || Path.IsPathRooted(relative)) throw new InvalidDataException($"Missing relative image path for {p.Id}.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!File.Exists(path)) throw new FileNotFoundException($"Missing image for {p.Id}.", path);
            return path;
        }, StringComparer.Ordinal);
        var options = new PaddleOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cpu, IntraOpNumThreads = input.Threads,
            InterOpNumThreads = 1, Download = new ModelDownloadOptions { Offline = true },
            ModelCachePath = input.ModelCachePath is null ? null : Path.GetFullPath(Path.Combine(root, input.ModelCachePath))
        };
        var recognition = RecognitionOptions.Default with { MaxDegreeOfParallelism = input.Threads };
        string cache = PaddleOcrModels.GetCacheDirectory(options);
        // Registry-resolved assets are a configured inventory, not proof each session was loaded.
        var hashes = new Dictionary<string, string>();
        foreach (string name in PaddleOcrModels.GetModelFileNames(languages, serviceOptions: options))
        {
            string path = Path.Combine(cache, name);
            using var stream = File.OpenRead(path); // Explicit opt-in must fail for missing cached assets.
            hashes.Add("configured-cache-inventory/" + name, Convert.ToHexString(SHA256.HashData(stream)));
        }
        var imageHashes = images.ToDictionary(p => p.Key, p =>
        {
            using var stream = File.OpenRead(p.Value);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        string optionsJson = JsonSerializer.Serialize(new
        {
            Service = options, Recognition = recognition, Languages = languages,
            HashScope = "Configured registry cache inventory; actual loaded-session paths are not tracked"
        }, Json);
        BenchmarkRun MakeRun(BenchmarkPrediction[] predictions) => new(input.RepositoryRevision,
            RuntimeInformation.FrameworkDescription, "CPU", "Registry model precision (not independently inspected)",
            $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; CPUs={Environment.ProcessorCount}; {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}",
            input.Threads, optionsJson, hashes, predictions);
        // Validate annotations before doing expensive inference, without fabricating recovered fields.
        TranscriptionBenchmark.Evaluate(input.Manifest, MakeRun(input.Manifest.Pages.Select(p =>
            new BenchmarkPrediction(p.Id, "", [])).ToArray()));

        var stageValues = new ConcurrentDictionary<string, ConcurrentQueue<double>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == PaddleOcrDiagnostics.MeterName && instrument.Name == "paddleocr.stage.duration")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string stage = "unknown";
            foreach (var tag in tags) if (tag.Key == "stage") stage = tag.Value?.ToString() ?? stage;
            stageValues.GetOrAdd(stage, _ => new()).Enqueue(value);
        });
        listener.Start();
        var measurements = new List<Measurement>();
        var predictionsByRun = new List<BenchmarkRun>();
        var reports = new List<BenchmarkReport>();
        int oldImageThreads = EasyImageSharp.Configuration.Default.MaxDegreeOfParallelism;
        try
        {
            EasyImageSharp.Configuration.Default.MaxDegreeOfParallelism = input.Threads;
            using var service = new PaddleOcrService(options);
            using var process = Process.GetCurrentProcess();
            for (int run = 0; run <= input.WarmRuns; run++)
            {
                var predictions = new List<BenchmarkPrediction>();
                // Reproducible randomized order reduces systematic ordering bias; run 0 warms all inputs.
                var order = input.Manifest.Pages.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
                new Random(104729 + run).Shuffle(order);
                foreach (var page in order)
                {
                    stageValues.Clear();
                    long allocated = GC.GetTotalAllocatedBytes(precise: true);
                    long started = Stopwatch.GetTimestamp();
                    var result = await service.ExtractTextFromImage(images[page.Id], languages, recognition);
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long allocationDelta = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    process.Refresh();
                    measurements.Add(new(page.Id, run, elapsed, allocationDelta, process.WorkingSet64,
                        process.PeakWorkingSet64, stageValues.ToDictionary(p => p.Key, p => p.Value.ToArray())));
                    predictions.Add(new(page.Id, result.FullText, []));
                }
                var baselineRun = MakeRun(predictions.ToArray());
                predictionsByRun.Add(baselineRun);
                reports.Add(TranscriptionBenchmark.Evaluate(input.Manifest, baselineRun));
            }
        }
        finally { EasyImageSharp.Configuration.Default.MaxDegreeOfParallelism = oldImageThreads; }

        string destination = Environment.GetEnvironmentVariable("PADDLEOCRNET_BASELINE_OUTPUT")
            ?? Path.Combine(root, "baseline-output");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "predictions.json"), JsonSerializer.Serialize(predictionsByRun, Json));
        await File.WriteAllTextAsync(Path.Combine(destination, "reports.json"), JsonSerializer.Serialize(reports, Json));
        await File.WriteAllTextAsync(Path.Combine(destination, "measurements.json"), JsonSerializer.Serialize(new
        {
            input.RepositoryRevision, input.Manifest.DatasetRevision, input.WarmRuns, input.Threads,
            InputSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(inputPath))),
            ImageSha256 = imageHashes, ConfiguredModelCacheInventorySha256 = hashes,
            ActualLoadedModelPathsTracked = false,
            FirstPass = "Run 0 includes lazy model/session initialization; later pages may reuse sessions. Not process-cold per page.",
            MemoryScope = "Managed allocation deltas are process-wide; working set sampled after calls; peak working set is process-lifetime, not per-call native peak.",
            StageScope = "Stages may overlap/nest; do not sum them as end-to-end latency.",
            ThreadScope = "Pins ONNX, recognition and EasyImageSharp settings; internal detector Parallel.For uses the runtime thread pool and is not a process-wide thread cap.",
            OnnxRuntime = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version?.ToString(),
            Measurements = measurements,
            WarmLatency = measurements.Where(m => m.Run > 0).GroupBy(m => m.PageId).Select(g => new
            {
                PageId = g.Key, Samples = g.Count(), P50Ms = Percentile(g.Select(m => m.Milliseconds), 0.50),
                P95Ms = Percentile(g.Select(m => m.Milliseconds), 0.95)
            })
        }, Json));
        output.WriteLine($"Baseline artifacts: {Path.GetFullPath(destination)} ({measurements.Count} OCR calls)");
    }

    private static double Percentile(IEnumerable<double> values, double quantile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(quantile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
