# Adaptive processing and faithful text views

`IPaddleOcrService.ProcessDocumentAsync` is an opt-in extension in `PaddleOcrNet.Services`. It runs the normal OCR entry point and returns its original `OcrResult` unchanged alongside review metadata and alternate readings. Existing OCR entry points and defaults are unchanged.

```csharp
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;
using PaddleOcrNet.Export;

var document = await service.ProcessDocumentAsync(
    image,
    new[] { OcrLanguage.English },
    new DocumentProcessingPolicy
    {
        Profile = DocumentProcessingProfile.Accurate,
        MaximumExtraPasses = 2,
        MaximumExtraPixels = 20_000_000,
        MaximumElapsed = TimeSpan.FromSeconds(15)
    },
    cancellationToken: cancellationToken);

var raw = document.RawResult;
var review = document.Quality;
var presentation = raw.ToNormalizedText(new TextNormalizationOptions
{
    CanonicalUnicode = true,
    CleanWhitespace = true
});
```

Fast admits no recovery calls by default, Balanced one, and Accurate two. `MaximumExtraPasses` overrides that profile limit. Each low-score or empty returned line is eligible for one `RecognizeRegionsAsync` call in source coordinates. The default alternate uses the existing legacy recognition resize option. These profiles describe effort; they are not calibrated accuracy levels. Agreement between the same recognizer on two resizes is correlated evidence, and is still reported for review. A higher confidence candidate never overwrites the original, including punctuation-sensitive cases such as `8:47` versus `8147`.

Caller languages pass unchanged to each service call. Supplying recognition options disables automatic recovery unless `RecoveryOptions` explicitly authorizes alternate settings. Supplied `RecoveryOptions` must explicitly set `UseDocOrientation = false`, `UseDocUnwarp = false`, and every preprocessing flag false, including `Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false }`; incompatible options fail before the initial OCR call. These two default-true flags must be explicitly disabled for a supplied recovery configuration. A caller can copy original character restrictions with `RecoveryOptions = originalOptions with { UseLegacyResize = true, UseDocOrientation = false, UseDocUnwarp = false, Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false } }`. Automatic recovery constructs these source-frame settings internally, so omitting options remains valid. Original options are never mutated. Invalid/out-of-image polygons and unwarped source coordinates are not re-read. Returned candidates preserve their own text, geometry and raw scores under `RegionEvidence.Alternatives`.

Every recovery call conservatively charges the full source image area against `MaximumExtraPixels`, even though recognition is restricted to a selected polygon. This measures admitted source pixels, not normalized tensor pixels, allocated bytes or resident-model memory. A pass is charged when admitted, including cancellation or an unsupported custom service. Extra work runs sequentially; no unbounded recovery queue is created. Elapsed admission time starts before the initial OCR call. The original call is permitted to complete even when this exceeds the budget, then recovery is skipped. A linked token requests cancellation when the remaining recovery time expires; native inference can exceed this deadline until it observes cancellation. Caller cancellation always propagates. `BudgetExhausted`, reason codes and region evidence identify unfinished recovery.

Quality assessment deliberately does not infer correctness from mean confidence. Empty pages are marked `no-recognized-text-coverage-unverified`: a blank page and missed detection cannot be distinguished using returned text alone. Detected crops suppressed by the underlying service are not available to this wrapper. High-confidence wrong text, undetected omissions, unsupported handwriting and learned preprocessing selection require independent evidence and evaluation. No trained selector, calibrated accuracy gate, model residency cap or hard native-inference timeout is claimed here.

`ToNormalizedText` preserves exact `FullText` by default. Explicit switches enable NFC canonical Unicode, horizontal whitespace cleanup, selected physical-line suppression, or hyphenated-line joining. Physical-line indices refer to the original `FullText` split on CRLF, CR, LF, VT, FF, NEL, Unicode line separator or paragraph separator, not OCR polygon indices. Suppression and whitespace cleanup use LF separators. Each actual change records full before/after text and a transformation name, in application order; transformations can be audited or reversed. NFC preserves accents; compatibility normalization and spelling correction are never applied. Hyphen joining can alter identifiers or meaning and must be chosen deliberately. It does not join a digit-hyphen-newline-digit sequence.

The normalized view is separate from the geometric result so word text and source polygons are never silently made inconsistent. Existing JSON/hOCR/ALTO/TSV exports retain original recognition evidence. `ToNormalizedHtml` emits an escaped preformatted presentation view; strings resembling markup remain data. Raw text and the transformation log stay available through `NormalizedTextResult`.

The quality report also includes `PageImageQuality`, available independently through `image.AssessImageQuality()` in `PaddleOcrNet.Extraction`. At most 4096 center samples measure luminance contrast, adjacent-pixel edge energy and quadrant luminance variation. Edge energy is blur-sensitive but also depends on page content and sampling; quadrant variation can reflect pictures or columns rather than uneven lighting. These measurements are uncalibrated diagnostics, not measured probabilities of a degradation. Sampling can miss local defects. It does not apply automatic restoration.

`EnableDetectionRescan` explicitly enables one detector rescan when the raw result is blank or includes a line shorter than `SmallTextHeight`. The default alternate enables the existing small-text rescue and large-image tiling controls. If original recognition options were supplied, `DetectionRescanOptions` must also be explicitly supplied; language, character, grouping and recognition settings remain intact. Original page orientation and preprocessing apply only to the initial full-page OCR call; selective detection and recognition run directly on source pixels using a copy with page-stage flags disabled. Source regions cannot safely be reused on a newly transformed image without a new mapping. The original options object is unchanged. The detector API may itself execute multiple tiled/upscaled model runs: `ExtraPasses` counts admitted service calls, not internal ONNX runs. The pixel limit counts source pixels admitted to service calls and is not an upper bound on upscaled/tiled tensor pixels. Recovery consumes the same budget first; increase the explicit limit when both weak-line retries and a detector rescan are wanted.

The rescan keeps geometrically uncovered regions, suppressing boxes only when overlap exceeds half the smaller box area and covers at least half the larger box area with existing or already admitted regions; a small header cannot suppress an arbitrarily larger region. This heuristic can miss legitimately overlapping content and is not a labeled detection-recall gate. At most `MaximumRescanRegions` new regions are sent to recognition in one further call. New readings remain in `AdditionalCandidates` for review; `UnreadRegions` retains detections without matching returned text, including those excluded by budgets. Neither is silently merged into raw transcription. Actual result provenance with `SourceMappingAvailable = false` blocks source-region processing even when no explicit options were supplied. Bounded automatic recognizer candidate selection is disclosed as `auto-routing-candidates-limited`.

`DocumentProcessingTests` covers resource admission, the three profiles, caller cancellation, elapsed recovery limits, explicit controls, blank-page review, confidence-increasing punctuation damage, bounded image diagnostics, source-frame checks, missing-region candidates, geometric deduplication, HTML escaping and reversible normalization. These deterministic tests verify behavior, not OCR accuracy improvements or research exit gates.
