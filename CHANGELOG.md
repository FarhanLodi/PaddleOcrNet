# Changelog

All notable changes to PaddleOcrNet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Detection tuning now functional** — `DetectionOptions.UseDilation` (2×2 bitmap dilation before
  contour extraction), `ScoreMode` (`Fast` box-crop vs `Slow` polygon-mask scoring), and `limit_type`
  min/max resize are now honored by `DbTextDetector` / `DBPostProcess` (previously declared but inert).
- **Recognition character filtering** — `RecognitionOptions.Allowlist` / `Blocklist` are now enforced
  via CTC-decode logit masking (`CharacterDictionary.BuildSelectableMask` → `CtcDecoder`), threaded
  through `ITextRecognizer.Recognize(crops, options)` and the engine (previously declared but inert).
  The CTC blank is always retained; blocklist wins on conflict.
- **Structure export to Office formats** — `StructureResult.ToDocx()` / `SaveAsDocx()` (WordprocessingML,
  native tables with row/col spans) and `ToXlsx()` / `SaveAsXlsx()` (SpreadsheetML, one sheet per table
  with merged cells) — parity with PP-StructureV3 `save_to_word` / `save_to_xlsx`. Built with BCL
  `ZipArchive` + hand-written OOXML (no new dependencies, AOT-safe).
- **Structure HTML + multi-page Markdown** — `StructureResult.ToHtml()` / `SaveAsHtml()` (semantic HTML5,
  tables verbatim, MathJax-wrapped formulas) and `StructureMarkdownExtensions.ConcatenateMarkdownPages(...)`
  — parity with `save_to_html` / `concatenate_markdown_pages`.
- **PDF page-range & password** — `PdfOcrOptions.PageRange` (1-based `"1-3,5,8-"` syntax) and `Password`
  (encrypted PDFs via PDFium) with original page numbers preserved in `PdfOcrResult`.
- **Document intelligence (LLM-backed KIE + Q&A)** — a new `PaddleOcrNet.Intelligence` layer that replaces
  PaddleOCR's PP-ChatOCR / KIE with a **provider-agnostic** design: a bring-your-own `IChatModel` interface
  plus a built-in `OpenAiCompatibleChatModel` that targets OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio,
  Groq, Together, DeepSeek, Mistral, and any OpenAI-style `/chat/completions` endpoint (factory helpers
  `OpenAiCompatibleOptions.OpenAi/AzureOpenAi/Ollama/Generic`; multimodal/vision and JSON-mode supported,
  source-gen JSON, AOT-safe). `IDocumentIntelligence` runs OCR/structure analysis, grounds the model on the
  parsed document Markdown, and offers `ExtractKeyInformationAsync` (key→value extraction, JSON-mode) and
  `AskAsync` (document Q&A). DI: `AddOpenAiCompatibleChatModel(...)` / `AddChatModel(...)` +
  `AddPaddleOcrDocumentIntelligence(...)`.

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
  (orientation classify/rotate and real UVDoc unwarp), **XY-cut** reading order,
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
- **Tests** — pure-function unit tests for min-area-rect ordering, DB unclip, CTC decode, character
  dictionary parsing, the registry, perspective warp, and reading order (`Category!=Integration`,
  CI-safe with no model download).
- **CI** — matrix build + unit tests (ubuntu/windows/macos), informational Native-AOT publish smoke,
  and NuGet pack validation.

### Known limitations

- **The package ships no weights; the core working set is published and downloads on first use.** The
  PP-OCRv5 detectors, every recognizer pack + dictionary, both orientation classifiers, UVDoc, the
  LaTeX-OCR formula files, and the exported structure models (`PP-DocLayoutV3`, `SLANet_plus`) are hosted
  on the public `PaddleOcrNet/PaddleOcrNet-models` Hugging Face repo and are **SHA-256 verified** on
  download. Point `PADDLEOCRNET_MODEL_BASE_URL` (or `ModelDownloadOptions.BaseUrlOverride`) at a private
  mirror if needed.
- **A few secondary structure models are not yet exported to ONNX, so they are not published.** The seal
  detector, the SLANeXt / PicoDet layout variants, the RT-DETR table-cell detectors, and the table
  classifier are referenced by the registry but have no published asset/checksum yet (they fail closed
  until `tools/export_onnx.py` produces and uploads them). The features that depend only on the published
  models — detection, recognition, orientation, dewarp, formula, `PP-DocLayoutV3` layout, and `SLANet_plus`
  tables — run end-to-end today (see `VALIDATION.md`).

[1.0.0-alpha]: https://github.com/paddleocrnet/PaddleOcrNet/releases/tag/v1.0.0-alpha
