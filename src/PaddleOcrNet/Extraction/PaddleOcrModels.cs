using System.Diagnostics;
using PaddleOcrNet.Internal;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Extraction;

/// <summary>
/// Pre-downloads model files into the model cache ahead of time — for Docker image builds, air-gapped
/// deployments (download once, then run with <see cref="ModelDownloadOptions.Offline"/>), or simply to
/// avoid first-request latency. Files are fetched and checksum-verified by the same download manager the
/// OCR service uses, into the same cache directory: the service's <see cref="PaddleOcrServiceOptions.ModelCachePath"/>
/// when given, otherwise the <c>PADDLEOCRNET_CACHE</c> environment variable, otherwise
/// <c>%LOCALAPPDATA%/PaddleOcrNet/models</c>. <see cref="ModelDownloadOptions.BaseUrlOverride"/> and the
/// <c>PADDLEOCRNET_MODEL_BASE_URL</c> environment variable select a mirror exactly as they do for the service.
/// </summary>
public static class PaddleOcrModels
{
    // Mirrors PaddleOcrEngine's default auto-detect shortlist: the recognizer packs OcrLanguage.Auto may load.
    private static readonly string[] AutoDetectCandidates =
    {
        "ch", "latin", "cyrillic", "arabic", "devanagari", "korean", "japan", "thai", "greek", "telugu", "tamil",
    };

    /// <summary>
    /// Returns the absolute model cache directory a service built with <paramref name="options"/> uses
    /// (the default cache when <paramref name="options"/> or its <see cref="PaddleOcrServiceOptions.ModelCachePath"/> is null).
    /// </summary>
    /// <param name="options">The service options whose cache path to resolve, or <c>null</c> for the default.</param>
    /// <returns>The absolute cache directory path (not created).</returns>
    public static string GetCacheDirectory(PaddleOcrServiceOptions? options = null)
        => ModelDownloadManager.ResolveCacheRoot(NormalizeCachePath(options?.ModelCachePath));

    /// <summary>
    /// Lists the model file names <see cref="DownloadAsync(IEnumerable{OcrLanguage}, PaddleModelSet, ModelDownloadOptions?, IProgress{ModelDownloadProgress}?, CancellationToken)"/>
    /// would fetch, without touching the network or disk — handy for building a mirror or checking a
    /// pre-seeded cache.
    /// </summary>
    /// <param name="languages">The recognition languages (<see cref="OcrLanguage.Auto"/> expands to the auto-detect candidate packs; none means the default pack).</param>
    /// <param name="sets">The model sets to include.</param>
    /// <param name="serviceOptions">Optional service options: server model variants and local model paths are honored as the service would.</param>
    /// <returns>The distinct file names, in download order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> is <c>null</c>.</exception>
    public static IReadOnlyList<string> GetModelFileNames(
        IEnumerable<OcrLanguage> languages,
        PaddleModelSet sets = PaddleModelSet.Ocr,
        PaddleOcrServiceOptions? serviceOptions = null)
        => ResolveAssets(languages, sets, serviceOptions).Select(a => a.FileName).ToArray();

    /// <summary>
    /// Ensures the model files for <paramref name="languages"/> and <paramref name="sets"/> are in the default
    /// model cache, downloading any that are missing. Every file is attempted; failures are reported per
    /// file rather than thrown — call <see cref="ModelDownloadReport.EnsureSuccess"/> to fail the step.
    /// </summary>
    /// <param name="languages">The recognition languages (<see cref="OcrLanguage.Auto"/> expands to the auto-detect candidate packs; none means the default pack).</param>
    /// <param name="sets">The model sets to fetch. Defaults to <see cref="PaddleModelSet.Ocr"/>.</param>
    /// <param name="options">Download behavior (retries, proxy, mirror, checksum policy). <c>null</c> uses the defaults.</param>
    /// <param name="progress">Optional progress sink: byte progress during downloads, plus one completed report per cached file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One entry per file with its status, size and path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<ModelDownloadReport> DownloadAsync(
        IEnumerable<OcrLanguage> languages,
        PaddleModelSet sets = PaddleModelSet.Ocr,
        ModelDownloadOptions? options = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var assets = ResolveAssets(languages, sets, serviceOptions: null);
        return DownloadCoreAsync(assets, cachePath: null, options ?? new ModelDownloadOptions(), progress, cancellationToken);
    }

    /// <summary>
    /// Ensures the model files a service configured with <paramref name="serviceOptions"/> needs are in that
    /// service's cache: its <see cref="PaddleOcrServiceOptions.ModelCachePath"/>, <see cref="PaddleOcrServiceOptions.Download"/>
    /// options, server model variants and local model paths are all honored. Every file is attempted;
    /// failures are reported per file rather than thrown.
    /// </summary>
    /// <param name="serviceOptions">The options the OCR service will be built with.</param>
    /// <param name="languages">The recognition languages (<see cref="OcrLanguage.Auto"/> expands to the auto-detect candidate packs; none means the default pack).</param>
    /// <param name="sets">The model sets to fetch. Defaults to <see cref="PaddleModelSet.Ocr"/>.</param>
    /// <param name="progress">Optional progress sink: byte progress during downloads, plus one completed report per cached file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One entry per file with its status, size and path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceOptions"/> or <paramref name="languages"/> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<ModelDownloadReport> DownloadAsync(
        PaddleOcrServiceOptions serviceOptions,
        IEnumerable<OcrLanguage> languages,
        PaddleModelSet sets = PaddleModelSet.Ocr,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceOptions);
        var assets = ResolveAssets(languages, sets, serviceOptions);
        return DownloadCoreAsync(assets, NormalizeCachePath(serviceOptions.ModelCachePath), serviceOptions.Download, progress, cancellationToken);
    }

    /// <summary>Resolves the distinct registry assets for the requested languages and sets, in download order.</summary>
    internal static IReadOnlyList<ModelAsset> ResolveAssets(
        IEnumerable<OcrLanguage> languages,
        PaddleModelSet sets,
        PaddleOcrServiceOptions? serviceOptions)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var requested = languages.ToArray();

        var assets = new List<ModelAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(ModelAsset asset)
        {
            if (seen.Add(asset.FileName)) assets.Add(asset);
        }

        if ((sets & PaddleModelSet.Ocr) != 0)
        {
            if (string.IsNullOrWhiteSpace(serviceOptions?.DetectionModelPath))
            {
                Add(serviceOptions?.DetectionModel == OcrModelVariant.Server
                    ? PaddleModelRegistry.ServerDetector
                    : PaddleModelRegistry.MobileDetector);
            }

            bool localRecognizer = serviceOptions is not null && !string.IsNullOrWhiteSpace(serviceOptions.RecognitionModelPath);
            bool localDictionary = serviceOptions is not null && !string.IsNullOrWhiteSpace(serviceOptions.RecognitionDictionaryPath);
            foreach (var pack in ResolvePacks(requested))
            {
                var effective = ApplyRecognitionVariant(pack, serviceOptions);
                bool isDefaultPack = effective.Name == PaddleModelRegistry.MobileRecognizer.Name
                    || effective.Name == PaddleModelRegistry.ServerRecognizer.Name;

                // A local recognizer replaces the default pack's network; its dictionary still comes from
                // the registry unless a local dictionary is supplied too (as the engine loads it).
                if (isDefaultPack && localRecognizer)
                {
                    if (!localDictionary) Add(effective.Dictionary);
                    continue;
                }

                Add(effective.Model);
                Add(effective.Dictionary);
            }

            Add(PaddleModelRegistry.TextLineOrientationClassifier);
            Add(PaddleModelRegistry.DocOrientationClassifier);
        }

        if ((sets & PaddleModelSet.Orientation) != 0)
        {
            Add(PaddleModelRegistry.TextLineOrientationClassifier);
            Add(PaddleModelRegistry.DocOrientationClassifier);
        }

        if ((sets & PaddleModelSet.Unwarp) != 0)
        {
            Add(PaddleModelRegistry.DocUnwarp);
        }

        if ((sets & PaddleModelSet.Layout) != 0)
        {
            Add(PaddleModelRegistry.DocLayoutV3);
            Add(PaddleModelRegistry.DocLayoutV3Labels);
        }

        if ((sets & PaddleModelSet.LayoutPicoDet) != 0)
        {
            Add(PaddleModelRegistry.DocLayoutS);
            Add(PaddleModelRegistry.DocLayoutSLabels);
            Add(PaddleModelRegistry.DocLayoutM);
            Add(PaddleModelRegistry.DocLayoutMLabels);
        }

        if ((sets & PaddleModelSet.Table) != 0)
        {
            Add(PaddleModelRegistry.SlanetPlus);
            Add(PaddleModelRegistry.DocImageOrientation);
        }

        if ((sets & PaddleModelSet.TableSlaNeXt) != 0)
        {
            Add(PaddleModelRegistry.TableClassifier);
            Add(PaddleModelRegistry.SlaNeXtWired);
            Add(PaddleModelRegistry.SlanetPlus);
            Add(PaddleModelRegistry.DocImageOrientation);
        }

        if ((sets & PaddleModelSet.Formula) != 0)
        {
            Add(PaddleModelRegistry.FormulaImageResizer);
            Add(PaddleModelRegistry.FormulaEncoder);
            Add(PaddleModelRegistry.FormulaDecoder);
            Add(PaddleModelRegistry.FormulaTokenizer);
        }

        if ((sets & PaddleModelSet.Seal) != 0)
        {
            Add(PaddleModelRegistry.SealDetector);
        }

        if ((sets & PaddleModelSet.ServerModels) != 0)
        {
            Add(PaddleModelRegistry.ServerDetector);
            Add(PaddleModelRegistry.ServerRecognizer.Model);
            Add(PaddleModelRegistry.ServerRecognizer.Dictionary);
        }

        return assets;
    }

    private static List<RecognizerPack> ResolvePacks(IReadOnlyList<OcrLanguage> languages)
    {
        var packs = new List<RecognizerPack>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void AddCode(string code)
        {
            if (PaddleModelRegistry.FindByLanguage(code) is { } pack && names.Add(pack.Name)) packs.Add(pack);
        }

        foreach (var language in languages)
        {
            if (language == OcrLanguage.Auto)
            {
                foreach (var code in AutoDetectCandidates) AddCode(code);
            }
            else
            {
                AddCode(language.ToCode());
            }
        }

        if (packs.Count == 0) packs.Add(PaddleModelRegistry.MobileRecognizer);
        return packs;
    }

    // Mirrors PaddleOcrEngine.ApplyRecognitionVariant: only the default (and Traditional-Chinese) pack has a server network.
    private static RecognizerPack ApplyRecognitionVariant(RecognizerPack pack, PaddleOcrServiceOptions? serviceOptions)
    {
        if (serviceOptions?.RecognitionModel != OcrModelVariant.Server) return pack;
        return pack.Name == PaddleModelRegistry.MobileRecognizer.Name || pack.Name == PaddleModelRegistry.ChineseTraditional.Name
            ? PaddleModelRegistry.ServerRecognizer
            : pack;
    }

    private static string? NormalizeCachePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static async Task<ModelDownloadReport> DownloadCoreAsync(
        IReadOnlyList<ModelAsset> assets,
        string? cachePath,
        ModelDownloadOptions options,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string cacheDirectory = ModelDownloadManager.ResolveCacheRoot(cachePath);
        var effectiveOptions = progress is null ? options : WithProgress(options, progress);
        var stopwatch = Stopwatch.StartNew();

        var files = new List<ModelFileReport>(assets.Count);
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string expectedPath = Path.Combine(cacheDirectory, asset.FileName);
            bool wasCached = File.Exists(expectedPath);
            try
            {
                string path = await ModelDownloadManager
                    .EnsureModelAsync(asset, cachePath, effectiveOptions, logger: null, cancellationToken)
                    .ConfigureAwait(false);
                long size = new FileInfo(path).Length;
                if (wasCached) progress?.Report(new ModelDownloadProgress(asset.FileName, size, size));
                files.Add(new ModelFileReport(asset.FileName, wasCached ? ModelFileStatus.Cached : ModelFileStatus.Downloaded, size, path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                files.Add(new ModelFileReport(asset.FileName, ModelFileStatus.Failed, 0, expectedPath, ex));
            }
        }

        return new ModelDownloadReport
        {
            CacheDirectory = cacheDirectory,
            Files = files,
            Duration = stopwatch.Elapsed,
        };
    }

    // Copies the caller's options (never mutated) with the progress sink added alongside any existing one.
    private static ModelDownloadOptions WithProgress(ModelDownloadOptions options, IProgress<ModelDownloadProgress> progress)
        => new()
        {
            MaxRetries = options.MaxRetries,
            RetryBaseDelay = options.RetryBaseDelay,
            Offline = options.Offline,
            HttpClientFactory = options.HttpClientFactory,
            BaseUrlOverride = options.BaseUrlOverride,
            AllowInsecureModelSource = options.AllowInsecureModelSource,
            AllowUnverifiedModels = options.AllowUnverifiedModels,
            Progress = options.Progress is { } existing ? new TeeProgress(existing, progress) : progress,
        };

    private sealed class TeeProgress(IProgress<ModelDownloadProgress> first, IProgress<ModelDownloadProgress> second)
        : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value)
        {
            first.Report(value);
            second.Report(value);
        }
    }
}
