using System.Diagnostics;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Models;
using PaddleOcrNet.Extraction;

namespace PaddleOcrNet.Services;

/// <summary>Opt-in bounded, evidence-preserving document processing over the existing service API.</summary>
public static class DocumentProcessingExtensions
{
    /// <summary>
    /// Recognizes a page normally, then selectively re-reads weak lines under explicit admission limits.
    /// Original text is never overwritten. Explicit language hints are passed unchanged to every call.
    /// Missing text cannot be inferred from confidence; a blank page is marked for review.
    /// The caller retains ownership of the image. This method does not bound resident model memory.
    /// Original page preprocessing applies only to the initial OCR call. Selective calls use source pixels;
    /// supplied recovery settings must explicitly disable document orientation and every preprocessing flag.
    /// </summary>
    public static async Task<DocumentProcessingResult> ProcessDocumentAsync(
        this IPaddleOcrService service,
        Image<Rgb24> image,
        IReadOnlyList<OcrLanguage> languages,
        DocumentProcessingPolicy? policy = null,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);
        policy ??= new();
        int maxPasses = policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var imageQuality = image.AssessImageQuality(cancellationToken);
        var raw = await service.ExtractTextFromImage(image, languages, options, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = new List<RegionEvidence>(raw.Lines.Count);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        int passes = 0;
        long pixels = 0, pagePixels = checked((long)image.Width * image.Height);
        bool exhausted = false;
        if (raw.Lines.Count == 0) reasons.Add("no-recognized-text-coverage-unverified");
        if (options?.AutoRouting?.MaxCandidatePacks > 0 || policy.RecoveryOptions?.AutoRouting?.MaxCandidatePacks > 0)
            reasons.Add("auto-routing-candidates-limited");
        if (imageQuality.LuminanceContrast < .03) reasons.Add("low-sampled-contrast-or-blank");
        if (imageQuality.IlluminationVariation > .25) reasons.Add("uneven-sampled-luminance-or-content");
        if (raw.Coordinates?.SourceMappingAvailable == false) reasons.Add("unwarped-coordinates-require-inverse-mapping");
        for (int i = 0; i < raw.Lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.Lines[i];
            var regionReasons = new List<string>();
            IReadOnlyList<OcrLine> alternatives = Array.Empty<OcrLine>();
            bool weak = string.IsNullOrWhiteSpace(line.Text) || !double.IsFinite(line.Confidence) || line.Confidence < policy.ReviewBelowConfidence;
            if (weak)
            {
                regionReasons.Add(string.IsNullOrWhiteSpace(line.Text) ? "empty-reading" : "low-raw-confidence");
                if (maxPasses == 0) regionReasons.Add("recovery-disabled");
                else if (options is not null && policy.RecoveryOptions is null) regionReasons.Add("explicit-options-preserved");
                else if (raw.Coordinates?.SourceMappingAvailable == false || options?.UseDocUnwarp == true || policy.RecoveryOptions?.UseDocUnwarp == true)
                    regionReasons.Add("unwarped-coordinates-require-inverse-mapping");
                else if (!ValidPolygon(line.BoundingPolygon, image.Width, image.Height)) regionReasons.Add("invalid-source-geometry");
                else if (passes >= maxPasses || pagePixels > policy.MaximumExtraPixels - pixels || stopwatch.Elapsed >= policy.MaximumElapsed)
                {
                    exhausted = true;
                    regionReasons.Add("recovery-budget-exhausted");
                }
                else
                {
                    // The supplied region is already in source coordinates. No detection or crop expansion is requested.
                    var alternate = policy.RecoveryOptions ?? SourceFrameOptions(RecognitionOptions.Default) with { UseLegacyResize = true };
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (policy.MaximumElapsed - stopwatch.Elapsed).TotalMilliseconds)));
                    passes++;
                    pixels += pagePixels;
                    try
                    {
                        var candidate = await service.RecognizeRegionsAsync(image,
                            new[] { line.BoundingPolygon }, languages, alternate, deadline.Token).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        alternatives = candidate.Lines;
                        regionReasons.Add(candidate.Lines.Count == 1 && string.Equals(candidate.Lines[0].Text, line.Text, StringComparison.Ordinal)
                            ? "correlated-agreement-unvalidated" : "alternative-disagreement");
                        if (stopwatch.Elapsed >= policy.MaximumElapsed)
                        {
                            exhausted = true;
                            regionReasons.Add("recovery-budget-exhausted");
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                    {
                        exhausted = true;
                        regionReasons.Add("recovery-budget-exhausted");
                    }
                    catch (NotSupportedException)
                    {
                        regionReasons.Add("selective-recognition-unavailable");
                    }
                }
            }
            foreach (string reason in regionReasons) reasons.Add(reason);
            evidence.Add(new RegionEvidence { LineIndex = i, Original = line, Alternatives = alternatives, ReviewReasons = regionReasons });
        }
        IReadOnlyList<OcrLine> additional = Array.Empty<OcrLine>();
        IReadOnlyList<DetectedRegion> unread = Array.Empty<DetectedRegion>();
        if (policy.EnableDetectionRescan && (raw.Lines.Count == 0 || raw.Lines.Any(l => l.BoundingBox.Height < policy.SmallTextHeight)))
        {
            if (maxPasses == 0) reasons.Add("detection-rescan-disabled-by-pass-budget");
            else if (raw.Coordinates?.SourceMappingAvailable == false || options?.UseDocUnwarp == true)
                reasons.Add("unwarped-coordinates-require-inverse-mapping");
            else if (options is not null && policy.DetectionRescanOptions is null) reasons.Add("explicit-detection-options-preserved");
            else if (Admit())
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (policy.MaximumElapsed - stopwatch.Elapsed).TotalMilliseconds)));
                try
                {
                    var rescanOptions = SourceFrameOptions(options ?? RecognitionOptions.Default) with
                    {
                        Detection = policy.DetectionRescanOptions ?? DetectionOptions.Default with { TileLargeImages = true, MinTextHeight = 16 }
                    };
                    var detected = await service.DetectRegionsAsync(image, rescanOptions, deadline.Token).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var missing = new List<DetectedRegion>();
                    foreach (var region in detected)
                    {
                        if (!ValidPolygon(region.BoundingPolygon, image.Width, image.Height)) continue;
                        var box = Box(region.BoundingPolygon);
                        if (raw.Lines.Any(l => Overlaps(box, l.BoundingBox)) || missing.Any(r => Overlaps(box, r.BoundingBox))) continue;
                        missing.Add(region with { BoundingBox = box });
                    }
                    unread = missing;
                    if (missing.Count > 0)
                    {
                        reasons.Add("new-detector-regions-require-review");
                        if (Admit())
                        {
                            var admitted = missing.Take(policy.MaximumRescanRegions).ToArray();
                            var candidate = await service.RecognizeRegionsAsync(image, admitted.Select(r => r.BoundingPolygon), languages,
                                SourceFrameOptions(options ?? RecognitionOptions.Default), deadline.Token).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            additional = candidate.Lines;
                            unread = missing.Where(r => !candidate.Lines.Any(l => Overlaps(r.BoundingBox, l.BoundingBox))).ToArray();
                            if (missing.Count > admitted.Length) reasons.Add("detection-region-limit-reached");
                        }
                    }
                    if (stopwatch.Elapsed >= policy.MaximumElapsed) { exhausted = true; reasons.Add("recovery-budget-exhausted"); }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    exhausted = true;
                    reasons.Add("recovery-budget-exhausted");
                }
                catch (NotSupportedException) { reasons.Add("detection-rescan-unavailable"); }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new DocumentProcessingResult
        {
            RawResult = raw,
            AdditionalCandidates = additional,
            UnreadRegions = unread,
            Regions = evidence,
            Quality = new PageQualityReport
            {
                RequiresReview = reasons.Count > 0,
                ReviewReasons = reasons.Order(StringComparer.Ordinal).ToArray(),
                BudgetExhausted = exhausted,
                ExtraPasses = passes,
                ExtraPixels = pixels,
                Elapsed = stopwatch.Elapsed,
                ImageQuality = imageQuality
            }
        };

        bool Admit()
        {
            if (passes >= maxPasses || pagePixels > policy.MaximumExtraPixels - pixels || stopwatch.Elapsed >= policy.MaximumElapsed)
            {
                exhausted = true;
                reasons.Add("recovery-budget-exhausted");
                return false;
            }
            passes++;
            pixels += pagePixels;
            return true;
        }
    }

    private static bool ValidPolygon(IReadOnlyList<OcrPoint> polygon, int width, int height)
        => polygon.Count >= 3 && polygon.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)
            && p.X >= 0 && p.Y >= 0 && p.X <= width && p.Y <= height)
            && polygon.Max(p => p.X) > polygon.Min(p => p.X) && polygon.Max(p => p.Y) > polygon.Min(p => p.Y);

    // Page stages belong to the initial full-page call. Source polygons cannot be reused on
    // a newly transformed image without a new mapping, so selective calls never apply them.
    private static RecognitionOptions SourceFrameOptions(RecognitionOptions options) => options with
    {
        UseDocOrientation = false,
        UseDocUnwarp = false,
        Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false }
    };

    private static OcrBoundingBox Box(IReadOnlyList<OcrPoint> polygon)
        => new(polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X), polygon.Max(p => p.Y));

    private static bool Overlaps(OcrBoundingBox a, OcrBoundingBox b)
    {
        double intersection = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX))
            * Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        double larger = Math.Max(a.Width * a.Height, b.Width * b.Height);
        // Containment alone is not equivalent coverage: a small header must not hide a
        // newly detected region containing additional body text.
        return smaller > 0 && intersection / smaller > .5 && intersection / larger >= .5;
    }
}
