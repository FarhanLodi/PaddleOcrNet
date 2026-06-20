using System.Collections.Concurrent;
using PaddleOcrNet.Internal.Classification;
using PaddleOcrNet.Internal.Detection;
using PaddleOcrNet.Internal.Geometry;
using PaddleOcrNet.Internal.Recognition;
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PaddleOcrNet.Internal;

/// <summary>
/// Top-level coordinator: loads the DB detector once, lazy-loads the (optional) text-line orientation
/// classifier and the recognizer pack(s) per language, and runs the det → cls → rec → reading-order
/// pipeline producing <see cref="OcrLine"/>s. Thread-safe; all underlying ONNX sessions are reused
/// across calls. Mirrors the lazy-session-cache / WarmUp / DisposeAsync patterns of the reference engine.
/// <para>
/// The pipeline is PaddleOCR's <c>PaddleOCR.ocr()</c>: detect text quads → rectify each quad into an
/// upright crop (<see cref="PerspectiveWarp"/> / <c>get_rotate_crop_image</c>) → optionally flip 180°
/// boxes via the orientation classifier → recognize crops (batched) → drop low-confidence readings →
/// sort into reading order (<see cref="SortBoxes"/> / <c>sorted_boxes</c>) → assemble
/// <see cref="OcrLine"/>s. All ONNX sessions are loaded on demand via the wired loader plumbing
/// (<see cref="GetOrLoadDetectorAsync"/>, <see cref="GetOrLoadClassifierAsync"/>,
/// <see cref="GetOrLoadRecognizerAsync"/>).
/// </para>
/// </summary>
internal sealed class PaddleOcrEngine : IAsyncDisposable
{
    private readonly PaddleEngineOptions _options;
    private readonly ILogger? _logger;

    // Session options for the resolved provider, split by workload: the detector runs one big graph per
    // image (wants intra-op parallelism); the classifier/recognizer run from a data-parallel loop over
    // boxes (on CPU each gets intra-op=1 so the loop supplies the parallelism). If an accelerated session
    // fails to initialize at model-load time we build the matching CPU sets once and route through them.
    private readonly SessionOptions _detectorSessionOptions;
    private readonly SessionOptions _recognizerSessionOptions;
    private SessionOptions? _detectorCpuFallback;
    private SessionOptions? _recognizerCpuFallback;
    private volatile OcrExecutionProvider _activeProvider;
    private readonly object _fallbackLock = new();

    private IPaddleDetector? _detector;
    private readonly SemaphoreSlim _detectorLock = new(1, 1);

    private IAngleClassifier? _classifier;
    private readonly SemaphoreSlim _classifierLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Lazy<Task<ITextRecognizer>>> _recognizers =
        new(StringComparer.OrdinalIgnoreCase);

    public PaddleOcrEngine(PaddleEngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        ResolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        _activeProvider = ResolvedProvider;
        _detectorSessionOptions = ExecutionProviderResolver.BuildSessionOptions(ResolvedProvider, options, logger, perBoxParallel: false);
        _recognizerSessionOptions = ExecutionProviderResolver.BuildSessionOptions(ResolvedProvider, options, logger, perBoxParallel: true);
        GpuHint = BuildGpuHint(options.ExecutionProvider, ResolvedProvider, options.LogGpuHint, logger);
    }

    /// <summary>
    /// The accelerator PaddleOcrNet resolved to attempt at startup — <see cref="OcrExecutionProvider.Auto"/>
    /// is resolved to a concrete provider here. A non-CPU value may still degrade to CPU at the first
    /// model load if the device turns out to be unusable; this property reflects the initial attempt.
    /// </summary>
    public OcrExecutionProvider ResolvedProvider { get; }

    /// <summary>
    /// A one-time, actionable message naming the exact provider package to install, set only when
    /// <see cref="OcrExecutionProvider.Auto"/> fell back to CPU yet a usable GPU is physically present.
    /// Null whenever a GPU is already in use, CPU was chosen explicitly, or no GPU was detected.
    /// </summary>
    public string? GpuHint { get; }

    /// <summary>
    /// Runs the full pipeline (detect → optional classify → recognize) and returns the recognized lines
    /// in reading order. This is PaddleOCR's <c>ocr(det=True, rec=True)</c>: the DB detector locates text
    /// quadrilaterals, each quad is rectified into an upright crop, recognized, filtered by
    /// <see cref="RecognitionOptions.DropScore"/>, and sorted top-to-bottom / left-to-right.
    /// </summary>
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var quads = detector.Detect(image, options.Detection);
        _logger?.LogInformation("DB detector located {Count} text regions", quads.Count);
        if (quads.Count == 0) return Array.Empty<OcrLine>();

        // De-duplicate overlapping detections (the detector can emit near-identical boxes) so the same
        // text isn't cropped and recognized twice.
        var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), options.Detection.NmsIouThreshold);
        return await RecognizePolygonsAsync(image, languages, polygons, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes a caller-supplied set of region polygons (skipping detection) — PaddleOCR's
    /// <c>ocr(det=False)</c>. Applies the optional text-line orientation classifier and the requested
    /// grouping. Boxes are returned in reading order, in the image's pixel coordinates.
    /// </summary>
    public Task<IReadOnlyList<OcrLine>> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => RecognizePolygonsAsync(image, languages, polygons, options, cancellationToken);

    /// <summary>Runs only the detector (no recognition) and returns the located regions in reading order.</summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        DetectionOptions detection,
        TextGrouping grouping,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var quads = detector.Detect(image, detection);
        if (quads.Count == 0) return Array.Empty<DetectedRegion>();

        var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), detection.NmsIouThreshold);
        var ordered = SortBoxes(polygons);

        var regions = new List<DetectedRegion>(ordered.Count);
        foreach (var poly in ordered)
        {
            regions.Add(new DetectedRegion
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
            });
        }
        return regions;
    }

    /// <summary>
    /// Shared det-less core: rectify every polygon into an upright crop, optionally flip 180° crops,
    /// recognize the crops in batches, drop low-confidence readings, sort the survivors into reading order
    /// and (when requested) merge them into paragraphs.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizePolygonsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        if (polygons.Count == 0) return Array.Empty<OcrLine>();

        var pack = ResolvePacks(languages).FirstOrDefault();
        if (pack is null) return Array.Empty<OcrLine>();

        var recognizer = await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);

        // Honor the per-call orientation flag even when the engine default left the classifier off; the
        // classifier session is loaded on demand and reused thereafter.
        IAngleClassifier? classifier = null;
        if (options.UseTextLineOrientation || _options.UseTextLineOrientation)
        {
            classifier = await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }

        // Rectify each quad into an upright crop. A null crop means the polygon was degenerate
        // (sub-2px after rectification); skip it but keep the polygon→crop index mapping intact so
        // recognition results line back up with their source polygons.
        var crops = new List<Image<Rgb24>>(polygons.Count);
        var cropPolygons = new List<OcrPoint[]>(polygons.Count);
        try
        {
            foreach (var poly in polygons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var crop = PerspectiveWarp.Rectify(image, poly);
                if (crop is null) continue;

                // Optional text-line orientation: a crop the classifier marks as upside-down is rotated
                // 180° in place before it reaches the recognizer.
                if (classifier is not null)
                {
                    var (rotated, _) = classifier.Classify(crop);
                    if (rotated) crop.Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                }

                crops.Add(crop);
                cropPolygons.Add(poly);
            }

            if (crops.Count == 0) return Array.Empty<OcrLine>();

            cancellationToken.ThrowIfCancellationRequested();
            var readings = recognizer.Recognize(crops);

            // Drop low-confidence / empty readings (PaddleOCR's drop_score), then keep each surviving
            // line paired with its source polygon.
            var lines = new List<OcrLine>(crops.Count);
            for (int i = 0; i < crops.Count && i < readings.Count; i++)
            {
                var (text, confidence) = readings[i];
                if (string.IsNullOrWhiteSpace(text) || confidence < options.DropScore) continue;

                var poly = cropPolygons[i];
                lines.Add(new OcrLine
                {
                    Text = text,
                    Confidence = confidence,
                    BoundingPolygon = poly,
                    BoundingBox = OcrBoundingBox.FromPoints(poly),
                });
            }

            var ordered = SortLines(lines);
            return options.Grouping == TextGrouping.Paragraph
                ? ParagraphGrouper.Merge(ordered)
                : ordered;
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
    }

    /// <summary>
    /// Eagerly loads the DB detector, the recognizer pack(s) for the given languages, and (when enabled)
    /// the text-line classifier so the first real OCR call doesn't pay cold-start latency.
    /// </summary>
    public async Task WarmUp(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        if (_options.UseTextLineOrientation)
        {
            await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var pack in ResolvePacks(languages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the shared <see cref="ITextRecognizer"/> for the first resolvable language (falling back to
    /// the default pack), loading and caching it via the same on-demand path as the OCR pipeline. Used by
    /// the structure subsystem (e.g. the seal recognizer) so it reuses the one cached recognizer session
    /// rather than loading its own. The returned recognizer is owned by this engine and disposed by it.
    /// </summary>
    public Task<ITextRecognizer> GetSharedRecognizerAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var pack = ResolvePacks(languages).FirstOrDefault() ?? PaddleModelRegistry.MobileRecognizer;
        return GetOrLoadRecognizerAsync(pack, cancellationToken);
    }

    /// <summary>Resolves the distinct recognizer packs for the requested languages (unsupported codes are skipped).</summary>
    private IReadOnlyList<RecognizerPack> ResolvePacks(IReadOnlyList<string> languages)
    {
        var packs = new Dictionary<string, RecognizerPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            var pack = PaddleModelRegistry.FindByLanguage(lang);
            if (pack is null)
            {
                _logger?.LogWarning("Language '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            packs[pack.Name] = pack;
        }
        return packs.Values.ToArray();
    }

    // ---- reading order (PaddleOCR's sorted_boxes) ----

    /// <summary>
    /// Vertical reading tolerance (px) within which two boxes are treated as being on the same text line
    /// and therefore ordered left-to-right. PaddleOCR's <c>sorted_boxes</c> uses 10px.
    /// </summary>
    private const double SameLineTolerance = 10.0;

    /// <summary>
    /// Orders detected polygons into reading order — PaddleOCR's <c>sorted_boxes</c>. Boxes are stably
    /// sorted by top-y then left-x, then a single adjacent-swap (bubble) pass fixes pairs that belong to
    /// the same text line (their top-y differs by less than <see cref="SameLineTolerance"/>) but landed
    /// out of left-to-right order.
    /// </summary>
    private static IReadOnlyList<OcrPoint[]> SortBoxes(IReadOnlyList<OcrPoint[]> polygons)
    {
        if (polygons.Count <= 1) return polygons;
        var boxes = polygons
            .Select(p => (Poly: p, Box: OcrBoundingBox.FromPoints(p)))
            .ToList();
        SortByReadingOrder(boxes, b => b.Box);
        return boxes.Select(b => b.Poly).ToArray();
    }

    /// <summary>
    /// Orders recognized lines into reading order using the same <c>sorted_boxes</c> rule as
    /// <see cref="SortBoxes"/>, keying off each line's axis-aligned bounding box.
    /// </summary>
    private static List<OcrLine> SortLines(List<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines;
        SortByReadingOrder(lines, l => l.BoundingBox);
        return lines;
    }

    /// <summary>
    /// In-place <c>sorted_boxes</c>: a stable sort by top-y then left-x, followed by a one-pass bubble
    /// that swaps an out-of-order neighbour pair that actually sits on the same text line.
    /// </summary>
    private static void SortByReadingOrder<T>(List<T> items, Func<T, OcrBoundingBox> boxOf)
    {
        // Stable primary sort: top-y, then left-x. List.Sort is not stable, so use OrderBy/ThenBy (stable)
        // and copy back — matching numpy's lexicographic sort used by PaddleOCR.
        var sorted = items
            .OrderBy(i => boxOf(i).MinY)
            .ThenBy(i => boxOf(i).MinX)
            .ToList();
        for (int i = 0; i < items.Count; i++) items[i] = sorted[i];

        // Bubble pass: when two adjacent boxes are within the same-line tolerance vertically but the
        // earlier one is further right, swap them so the line reads left-to-right. The inner loop walks
        // back while the pair stays on the same line; the `else break` is load-bearing — the moment a
        // neighbour is on a *different* line we must stop, otherwise a later same-line pair could pull a
        // box across a line boundary and corrupt the vertical order.
        for (int i = 0; i < items.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                var a = boxOf(items[j + 1]);
                var b = boxOf(items[j]);
                if (Math.Abs(a.MinY - b.MinY) < SameLineTolerance && a.MinX < b.MinX)
                {
                    (items[j], items[j + 1]) = (items[j + 1], items[j]);
                }
                else
                {
                    break;
                }
            }
        }
    }

    // ---- session loading (wired; reused by the pipeline methods) ----

    private async Task<IPaddleDetector> GetOrLoadDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;

        await _detectorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            var path = await ModelDownloadManager.EnsureModelAsync(
                PaddleModelRegistry.Detector, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _detector = CreateSessionBacked(so => (IPaddleDetector)new DbTextDetector(new InferenceSession(path, so)), recognizer: false);
            Diagnostics.PaddleOcrDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "det"));
            _logger?.LogInformation("DB detector loaded from {Path}", path);
            return _detector;
        }
        finally
        {
            _detectorLock.Release();
        }
    }

    private async Task<IAngleClassifier> GetOrLoadClassifierAsync(CancellationToken cancellationToken)
    {
        if (_classifier is not null) return _classifier;

        await _classifierLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_classifier is not null) return _classifier;
            var path = await ModelDownloadManager.EnsureModelAsync(
                PaddleModelRegistry.Classifier, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _classifier = CreateSessionBacked(so => (IAngleClassifier)new TextLineClassifier(new InferenceSession(path, so)), recognizer: true);
            Diagnostics.PaddleOcrDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "cls"));
            _logger?.LogInformation("Text-line orientation classifier loaded from {Path}", path);
            return _classifier;
        }
        finally
        {
            _classifierLock.Release();
        }
    }

    private async Task<ITextRecognizer> GetOrLoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        // The load Task is shared across all callers, so it must NOT capture any single caller's
        // CancellationToken — otherwise the first caller cancelling would poison the cached Task and
        // every later caller (with a live token) would observe that cancellation. Load with None and
        // let each caller observe only its own token via WaitAsync.
        var lazy = _recognizers.GetOrAdd(pack.Name, _ => new Lazy<Task<ITextRecognizer>>(
            () => LoadRecognizerAsync(pack, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var loadTask = lazy.Value;
        try
        {
            return await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If the shared load itself failed (download/IO/corrupt model), evict the poisoned entry so a
            // later call retries instead of being served the cached faulted Task forever. A per-caller
            // cancellation leaves a still-running/successful task in place, so only the genuine fault path
            // evicts. TryRemove(pair) is a CAS: it removes only when the value is still this exact Lazy.
            if (loadTask.IsFaulted)
            {
                _recognizers.TryRemove(new KeyValuePair<string, Lazy<Task<ITextRecognizer>>>(pack.Name, lazy));
            }
            throw;
        }
    }

    private async Task<ITextRecognizer> LoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        var modelPath = await ModelDownloadManager.EnsureModelAsync(
            pack.Model, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
        var dictPath = await ModelDownloadManager.EnsureModelAsync(
            pack.Dictionary, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);

        var vocab = CharacterDictionary.Load(dictPath);
        Diagnostics.PaddleOcrDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", pack.Name));
        _logger?.LogInformation("Recognizer '{Name}' loaded from {Path} ({Count} classes)", pack.Name, modelPath, vocab.Count);
        return CreateSessionBacked(so => (ITextRecognizer)new SvtrRecognizer(new InferenceSession(modelPath, so), vocab), recognizer: true);
    }

    /// <summary>
    /// Creates an ONNX-session-backed object via <paramref name="factory"/> using the active session
    /// options. When an accelerated provider was resolved but its session fails to initialize, this
    /// permanently downgrades the engine to CPU and retries once, so the very first model load can't
    /// hard-fail on a bad accelerator.
    /// </summary>
    private T CreateSessionBacked<T>(Func<SessionOptions, T> factory, bool recognizer)
    {
        var primary = recognizer ? _recognizerSessionOptions : _detectorSessionOptions;
        if (_activeProvider == OcrExecutionProvider.Cpu)
        {
            var fallback = recognizer ? _recognizerCpuFallback : _detectorCpuFallback;
            return factory(fallback ?? primary);
        }

        try
        {
            return factory(primary);
        }
        catch (Exception ex)
        {
            return factory(DowngradeToCpu(ex, recognizer));
        }
    }

    private SessionOptions DowngradeToCpu(Exception cause, bool recognizer)
    {
        lock (_fallbackLock)
        {
            if (_detectorCpuFallback is null)
            {
                _logger?.LogWarning(cause,
                    "{Provider} session initialization failed at model load; falling back to CPU for all sessions.",
                    _activeProvider);
                _detectorCpuFallback = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger, perBoxParallel: false);
                _recognizerCpuFallback = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger, perBoxParallel: true);
                _activeProvider = OcrExecutionProvider.Cpu;
            }
            return (recognizer ? _recognizerCpuFallback : _detectorCpuFallback)!;
        }
    }

    /// <summary>
    /// When auto-detection landed on CPU but the host actually has a GPU, builds the package-specific
    /// upgrade hint. The string is always returned (and exposed via <see cref="GpuHint"/>); it is only
    /// logged as a startup warning when <paramref name="logHint"/> is true (off by default).
    /// </summary>
    private static string? BuildGpuHint(OcrExecutionProvider requested, OcrExecutionProvider resolved, bool logHint, ILogger? logger)
    {
        if (requested != OcrExecutionProvider.Auto || resolved != OcrExecutionProvider.Cpu) return null;

        var vendor = GpuProbe.Detect();
        if (vendor == GpuProbe.GpuVendor.None) return null;

        var message = vendor == GpuProbe.GpuVendor.Nvidia
            ? "PaddleOcrNet: an NVIDIA GPU was detected but OCR is running on CPU. Install the " +
              "'PaddleOcrNet.Gpu' NuGet package for CUDA acceleration. It is then used automatically — " +
              "no code change needed."
            : $"PaddleOcrNet: a GPU ({vendor}) was detected but OCR is running on CPU. GPU acceleration is " +
              "currently available for NVIDIA GPUs via the 'PaddleOcrNet.Gpu' package (CUDA 12+).";

        if (logHint) logger?.LogWarning("{GpuHint}", message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        _detector?.Dispose();
        _detector = null;
        _classifier?.Dispose();
        _classifier = null;

        foreach (var entry in _recognizers.Values)
        {
            if (entry.IsValueCreated)
            {
                try { (await entry.Value.ConfigureAwait(false)).Dispose(); }
                catch { /* dispose best-effort */ }
            }
        }
        _recognizers.Clear();
        _detectorSessionOptions.Dispose();
        _recognizerSessionOptions.Dispose();
        _detectorCpuFallback?.Dispose();
        _recognizerCpuFallback?.Dispose();
    }
}
