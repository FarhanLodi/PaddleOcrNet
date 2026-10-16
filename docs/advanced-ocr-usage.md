# Advanced OCR APIs

The additions are opt-in. Existing recognition, PDF and structure defaults retain their previous policies. The EasyImageSharp dependency is 1.2.0.

## Language routing

```csharp
var options = RecognitionOptions.Default with
{
    AutoDetectCandidates = new[] { "en", "ar", "devanagari" },
    AutoRouting = new AutoRoutingOptions
    {
        EvaluateAllCandidates = true,
        MaxCandidatePacks = 3
    }
};
var result = await service.ExtractTextFromImage(path, new[] { OcrLanguage.Auto }, options);
```

Explicit candidate order defines admission priority. Zero maximum means all resolved packs. A bounded list can exclude the correct script; `paddleocr.auto_routing` traces expose available/admitted/evaluated counts and budget exhaustion. The adaptive wrapper also reports candidate-limited review metadata. This is an explicit candidate budget, not an image-trained script classifier.

`EvaluateAllCandidates` prevents the page-average fast path from skipping admitted minority-script candidates. Optional `Calibration` supplies a curve for every admitted pack. Curves must have matching model/dictionary hashes and finite monotonic knots; partial or incompatible calibration fails instead of mixing calibrated and raw scores. Selection uses calibrated scores, while each returned line preserves its original raw confidence. Calibration disables the raw-confidence shortcut. No production curves are bundled; use the [labeled fitting command](calibration.md) and independent held-out evaluation.

Opt-in routing resolves server-model substitutions before deduplicating candidates, applying admission limits and validating calibration. Registry-bound calibration rejects a local default-recognizer model override, whose bytes are not represented by the registry hash. Dedicated script packs without overrides retain their own calibration contracts.

## Geometry

`OcrResult.Coordinates` describes the frame and applied operations. Ordinary OCR maps affine rotations, deskew, pixel-aspect correction and crop offsets back to decoded-source pixels. A nonlinear dewarp marks `DewarpedPixels` and `SourceMappingAvailable = false`: the current UVDoc export supplies a rectified image, not an inverse field. Affine mapping does not undo that nonlinear displacement. Encoded images are decoded/EXIF-oriented before this source contract applies.

`StructureResult.Coordinates` separately records original/working dimensions and actual document preprocessing. Structure blocks remain in the preprocessed frame when rotated, unlike ordinary OCR results. Nonlinear mappings have no claimed inverse. Hybrid PDF fusion and searchable-PDF placement reject explicitly unmapped OCR frames. Custom services producing no metadata retain responsibility for supplying their documented source frame.

## Feature guides

- [Long-line segmentation and input-tensor budgets](recognition-advancements.md): overlapping windows, Unicode grapheme ownership, source geometry and cancellation boundaries.
- [Adaptive processing](adaptive-processing.md): bounded weak-line alternatives, sampled image diagnostics, optional missing-region rescans and explicit text normalization logs.
- [Hybrid PDF](hybrid-pdf.md): native/OCR evidence, conflict reporting and searchable output.
- [Multi-page document assembly](document-assembly.md): page ownership, orphan preservation, containment/sequence links and conservative header/table continuation evidence.
- [Controlled baseline](controlled-baseline.md), [full transcription scoring](transcription-benchmarks.md), and [calibration/statistical evaluation](calibration.md).

Recognition cancellation is checked before/after native inference, between batches/windows and during decoding. It does not forcibly terminate an in-flight native inference call. `MaxBatchPixels` caps normalized input tensor pixels per concurrent batch; recovery limits charge service calls/source image pixels; neither is a process-wide memory or resident-model cap.

## Deployment smoke

```powershell
dotnet publish test/PaddleOcrNet.AotSmoke -c Release -r win-x64 -o artifacts/aot-smoke
./artifacts/aot-smoke/PaddleOcrNet.AotSmoke.exe
```

The smoke uses source-generated document JSON, evidence-preserving normalization and structure orphan assembly/serialization without downloading models. Native AOT requires the platform native linker. A trimmed self-contained alternative checks trimming/runtime behavior but is not Native AOT:

```powershell
dotnet publish test/PaddleOcrNet.AotSmoke -c Release -r win-x64 `
  -p:PublishAot=false -p:PublishTrimmed=true --self-contained true -o artifacts/trimmed-smoke
./artifacts/trimmed-smoke/PaddleOcrNet.AotSmoke.exe
```

## Evidence still required for release claims

The implementation supplies usable mechanisms and evaluation commands; it does not manufacture an adjudicated real corpus, trained script classifier, specialist handwriting model, distortion inverse field, teacher/student training run or GPU measurements. Hybrid defaults to full-page visual OCR to retain conflicts with misleading native layers; the explicit uncovered-only option trusts native coverage and records that visual verification was skipped. Table continuations remain reviewable links until callers approve a separate joined-table view, which preserves source rows and spans. Learned action selection, automatic calibrated acceptance, model-residency eviction and specialist training remain research work. Neither synthetic tests nor a single smoke image establish the plan's accuracy/speed percentages.
