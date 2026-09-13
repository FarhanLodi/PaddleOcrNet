using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PaddleOcrNet.Services;

/// <summary>
/// Health check that reports whether PaddleOcrNet can serve requests: the model cache directory is
/// accessible, and the models the configured service will actually load are present (so the first real
/// request won't block on a download). That is the configured detector (mobile, server, or a local
/// <see cref="PaddleOcrServiceOptions.DetectionModelPath"/>), the text-line and document orientation
/// classifiers that default recognition calls use, and the recognizer pack for each expected language
/// (honoring <see cref="PaddleOcrServiceOptions.RecognitionModel"/> and the local recognition overrides).
/// Register via <see cref="ServiceCollectionExtensions.AddPaddleOcrHealthCheck"/>.
/// </summary>
public sealed class PaddleOcrHealthCheck : IHealthCheck
{
    private readonly PaddleOcrServiceOptions _options;
    private readonly string[] _languages;
    private readonly HealthStatus _failureStatus;

    /// <summary>
    /// Creates a health check for the given service options and expected languages.
    /// </summary>
    public PaddleOcrHealthCheck(PaddleOcrServiceOptions options, IEnumerable<string>? languages = null, HealthStatus failureStatus = HealthStatus.Degraded)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _languages = languages?.ToArray() ?? Array.Empty<string>();
        _failureStatus = failureStatus;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string cacheRoot;
        try
        {
            cacheRoot = ModelDownloadManager.ResolveCacheRoot(_options.ModelCachePath);
            Directory.CreateDirectory(cacheRoot);
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Model cache directory is not accessible.", ex));
        }

        var data = new Dictionary<string, object> { ["cachePath"] = cacheRoot };
        var missing = new List<string>();          // required: the pipeline fails without them
        var missingOptional = new List<string>();  // degrade gracefully when absent
        var missingLocal = new List<string>();     // configured local files: never downloadable

        // Detector — required for every language.
        if (!string.IsNullOrWhiteSpace(_options.DetectionModelPath))
        {
            AddIfLocalMissing(_options.DetectionModelPath, missingLocal);
        }
        else
        {
            var detector = _options.DetectionModel == OcrModelVariant.Server
                ? PaddleModelRegistry.ServerDetector
                : PaddleModelRegistry.MobileDetector;
            AddIfMissing(cacheRoot, detector.FileName, missing);
        }

        // Text-line orientation classifier: loaded whenever the service or the per-call default enables it
        // (RecognitionOptions.UseTextLineOrientation defaults to true).
        if (_options.UseTextLineOrientation || RecognitionOptions.Default.UseTextLineOrientation)
        {
            AddIfMissing(cacheRoot, PaddleModelRegistry.TextLineOrientationClassifier.FileName, missing);
        }

        // Document orientation classifier: on by default, but a failed load degrades gracefully.
        if (RecognitionOptions.Default.UseDocOrientation)
        {
            AddIfMissing(cacheRoot, PaddleModelRegistry.DocOrientationClassifier.FileName, missingOptional);
        }

        foreach (var lang in _languages)
        {
            var def = PaddleModelRegistry.FindByLanguage(lang);
            if (def is null)
            {
                missing.Add($"{lang} (unsupported language)");
                continue;
            }

            def = ApplyRecognitionVariant(def);
            bool isDefaultPack = def.Name == PaddleModelRegistry.MobileRecognizer.Name
                                 || def.Name == PaddleModelRegistry.ServerRecognizer.Name;
            if (isDefaultPack && !string.IsNullOrWhiteSpace(_options.RecognitionModelPath))
            {
                AddIfLocalMissing(_options.RecognitionModelPath, missingLocal);
                if (!string.IsNullOrWhiteSpace(_options.RecognitionDictionaryPath))
                    AddIfLocalMissing(_options.RecognitionDictionaryPath, missingLocal);
                else
                    AddIfMissing(cacheRoot, def.Dictionary.FileName, missing);
                continue;
            }

            AddIfMissing(cacheRoot, def.Model.FileName, missing);
            AddIfMissing(cacheRoot, def.Dictionary.FileName, missing);
        }

        if (missing.Count == 0 && missingOptional.Count == 0 && missingLocal.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                _languages.Length > 0 ? "Models present; ready to serve." : "Model cache accessible.",
                data));
        }

        data["missing"] = missing.Concat(missingLocal).Concat(missingOptional).Distinct().ToList();

        if (missingLocal.Count > 0)
        {
            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Unhealthy,
                "A configured local model or dictionary file does not exist.",
                data: data));
        }

        var description = _options.Download.Offline
            ? "Offline mode: required models are missing from the cache."
            : "Some models are not cached yet; they will download on first use.";

        // In offline mode, missing REQUIRED models mean the service cannot run at all.
        var status = _options.Download.Offline && missing.Count > 0 ? HealthStatus.Unhealthy : _failureStatus;
        return Task.FromResult(new HealthCheckResult(status, description, data: data));
    }

    /// <summary>
    /// Mirrors the engine's server-variant substitution: the default ch/en/ja pack (and Traditional
    /// Chinese, which shares its network) moves to the server recognizer; per-script packs stay mobile.
    /// </summary>
    private RecognizerPack ApplyRecognitionVariant(RecognizerPack pack)
    {
        if (_options.RecognitionModel != OcrModelVariant.Server) return pack;
        return pack.Name == PaddleModelRegistry.MobileRecognizer.Name
               || pack.Name == PaddleModelRegistry.ChineseTraditional.Name
            ? PaddleModelRegistry.ServerRecognizer
            : pack;
    }

    private static void AddIfMissing(string cacheRoot, string fileName, List<string> missing)
    {
        if (!File.Exists(Path.Combine(cacheRoot, fileName)))
        {
            missing.Add(fileName);
        }
    }

    private static void AddIfLocalMissing(string path, List<string> missing)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            missing.Add(fullPath);
        }
    }
}
