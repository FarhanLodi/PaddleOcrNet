# Changelog

All notable changes to PaddleOcrNet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-alpha] - 2026-06-20

First alpha of the core OCR engine. Functional end-to-end once the PP-OCRv5 ONNX models are published
to the configured model host.

### Added

- **Text detection** — DB (Differentiable Binarization) detector (`DbTextDetector`) with full
  post-processing: probability-map thresholding, connected components, min-area rotated boxes, the
  Vatti **unclip** expansion (Clipper2), and box NMS.
- **Text-line orientation** — `TextLineClassifier` (PP-LCNet, 0°/180°) to upright flipped lines before
  recognition.
- **Text recognition** — `SvtrRecognizer` running the PP-OCRv5 SVTR/CRNN network, **CTC** greedy
  decoding (`CtcDecoder`), and `ppocr` character dictionaries (`CharacterDictionary`).
- **Pipeline** — `PaddleOcrEngine` coordinates detect → (optional) classify → rectify crops
  (perspective warp) → recognize (batched) → drop low-confidence readings → reading-order sort
  (`sorted_boxes`) → optional paragraph grouping.
- **Document-structure subsystem (PP-StructureV3)** — `IPaddleOcrService.AnalyzeDocumentAsync`
  (path / stream / `byte[]` / `ReadOnlyMemory<byte>` / ImageSharp overloads) coordinated by
  `PaddleStructureEngine`: **layout** detection (PP-DocLayout PicoDet-S/M + RT-DETR `plus-L`), **table**
  recognition (**SLANet** structure-token decode → HTML with OCR cell-text matching), **formula**
  recognition (**LaTeX-OCR**: image-resize + split encoder/decoder transformer + autoregressive LaTeX
  decode — the MIT RapidLaTeXOCR ONNX, *not* PP-FormulaNet, which is not ONNX-exportable), **seal**
  recognition (PP-OCRv4 seal detector + shared text recognizer), document **pre-processing**
  (orientation classify/rotate; UVDoc unwarp is a safe pass-through stub), **XY-cut** reading order,
  and `StructureResult.ToMarkdown()` / `.ToJson()` exporters (the latter AOT-safe via a source-generated
  `StructureJsonContext`). Sub-models are lazily loaded once and their ONNX sessions reused.
- **Multilingual model registry** — `PaddleModelRegistry` cataloguing the PP-OCRv5 detector, the
  default Chinese/English/Japanese recognizer, the orientation classifiers, and per-script recognizer
  packs (latin, cyrillic, arabic, devanagari, korean, japanese, thai, greek, telugu, tamil,
  traditional-chinese, east-slavic), each with its character dictionary.
- **Public API** — `PaddleOcrService` / `IPaddleOcrService` with `ExtractTextFromImage` overloads
  (path, stream, `byte[]`, `ReadOnlyMemory<byte>`, ImageSharp `Image<Rgb24>`), `DetectRegionsAsync`,
  `RecognizeRegionsAsync`, and `WarmUp`.
- **DI & health** — `services.AddPaddleOcrNet(...)` (singleton) and `AddPaddleOcrHealthCheck(...)`
  (named `paddleocr`) reporting model-cache readiness.
- **Model download manager** — cached downloads with retry/offline/proxy/mirror support,
  `PADDLEOCRNET_MODEL_BASE_URL` host override, and SHA256 verification (enforced once checksums are
  published).
- **Export & output** — text / JSON (AOT-safe via `PaddleOcrJsonContext`) / hOCR / ALTO exporters and
  visualization helpers; PDF input rasterization and searchable-PDF output plumbing.
- **GPU** — optional `PaddleOcrNet.Gpu` package (CUDA 12+), auto-detected with a CPU fallback and an
  actionable upgrade hint (`GpuAccelerationHint`).
- **Maintainer export toolchain** (`tools/`) — converts every PaddleOCR / PaddleX model to ONNX,
  generates checksums + a C# registry snippet, and uploads to Hugging Face.
- **Tests** — pure-function unit tests for min-area-rect ordering, DB unclip, CTC decode, character
  dictionary parsing, the registry, perspective warp, and reading order (`Category!=Integration`,
  CI-safe with no model download).
- **CI** — matrix build + unit tests (ubuntu/windows/macos), informational Native-AOT publish smoke,
  and NuGet pack validation.

### Known limitations

- **No model weights are shipped, and the default host is a placeholder.** Set
  `PADDLEOCRNET_MODEL_BASE_URL` (or `ModelDownloadOptions.BaseUrlOverride`) to a host that has the
  PP-OCRv5 ONNX assets. Until checksums are published, downloads require
  `ModelDownloadOptions.AllowUnverifiedModels`.
- **Structure subsystem is implemented in-code but needs the model upload to validate end-to-end.**
  Layout analysis, table recognition (SLANet → HTML), formula recognition (LaTeX-OCR → LaTeX, via the
  MIT RapidLaTeXOCR ONNX — PP-FormulaNet is not ONNX-exportable) and seal recognition are wired and
  unit-tested; **document dewarp (UVDoc) is a deliberate safe pass-through stub** (no-op remap until its
  grid I/O contract is verified against the real export).
- Because no weights are published, both the core engine and the structure subsystem are validated by
  pure-function unit tests rather than full image-to-text runs.

[1.0.0-alpha]: https://github.com/paddleocrnet/PaddleOcrNet/releases/tag/v1.0.0-alpha
