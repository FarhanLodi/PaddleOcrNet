# Changelog

All notable changes to PaddleOcrNet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-08-26

### Changed

- **BREAKING — imaging backend moved from SixLabors.ImageSharp to EasyImageSharp.** All image
  decode/encode, resizing, cropping, rotation and pixel access now run on
  [EasyImageSharp](https://www.nuget.org/packages/EasyImageSharp) (MIT, fully managed, zero package
  dependencies). This removes the Six Labors Split License, so commercial use above their revenue
  threshold no longer requires a paid licence.

  Every public member that took or returned `SixLabors.ImageSharp.Image<Rgb24>` now uses
  `EasyImageSharp.Image<Rgb24>` — 29 signatures across `IPaddleOcrService` (6 overloads),
  `IDocumentIntelligence`, `OcrVisualizationExtensions.DrawAnnotations`, `StructureResult.ToDocx` /
  `WriteDocx` / `SaveAsDocx` / `ToHtml` / `SaveAsHtml`, and the PDF extensions.

  **Migration:** replace the package reference and change `using SixLabors.ImageSharp;` →
  `using EasyImageSharp;` (likewise `.PixelFormats`, `.Processing`, `.Formats.Png`, `.Formats.Jpeg`).
  The namespaces and type names map one-to-one, so this is normally a find-and-replace. The one API
  difference: `Rectangle` is a `readonly struct`, so the mutating `rect.Intersect(other)` becomes
  `rect = Rectangle.Intersect(rect, other)`.

  **Accuracy is unaffected.** Detection, recognition, classification, layout, formula and table output
  were diffed against the 1.0.0 build and are byte-identical, with one exception: a text-line
  orientation confidence of 0.9850 vs 0.9849 (1e-4 of resampler rounding; same decision). Throughput
  is equivalent.

## [1.0.0] - 2026-06-21

### Added

- **Table recognition v2 (SLANeXt)** — `StructureOptions.TableModel = TableRecognitionModel.SlaNeXt` opts
  into the PP-StructureV3 v2 table path: a `PP-LCNet_x1_0_table_cls` classifier picks wired (bordered) vs
  wireless (borderless) and runs the matching `SLANeXt` structure model (512×512). SLANeXt shares SLANet's
  output heads, so it reuses the validated recognizer via `SlaNeXtTableRouter` + `TableClassifier`. The
  default stays `SlanetPlus` (single end-to-end model). Validated end-to-end against the hosted models.
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
- **`OcrLanguage` enum — the language API is now enum-only.** Languages are expressed with the
  strongly-typed `OcrLanguage` enum (incl. `OcrLanguage.Auto`); the raw string-code language parameters
  have been removed. `ExtractTextFromImage`, `RecognizeRegionsAsync`, `WarmUp`, the PDF helpers
  (`ExtractTextFromPdfAsync` / `CreateSearchablePdfAsync`), `StructureOptions.Languages`, and
  `AddPaddleOcrHealthCheck(languages:)` all take `OcrLanguage` / `IReadOnlyList<OcrLanguage>`. The
  single-language convenience overloads default to `OcrLanguage.Auto`, so `ExtractTextFromImage("x.png")`
  auto-detects with zero configuration. `ToCode()`/`ToCodes()` convert to the underlying codes, and
  `OcrLanguageExtensions.FromCode`/`TryFromCode`/`FromCodes` parse raw string codes (from CLI args or
  config) back into the enum.
- **Document intelligence (LLM-backed KIE + Q&A)** — a new `PaddleOcrNet.Intelligence` layer that replaces
  PaddleOCR's PP-ChatOCR / KIE with a **provider-agnostic** design: a bring-your-own `IChatModel` interface
  plus a built-in `OpenAiCompatibleChatModel` that targets OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio,
  Groq, Together, DeepSeek, Mistral, and any OpenAI-style `/chat/completions` endpoint (factory helpers
  `OpenAiCompatibleOptions.OpenAi/AzureOpenAi/Ollama/Generic`; multimodal/vision and JSON-mode supported,
  source-gen JSON, AOT-safe). `IDocumentIntelligence` runs OCR/structure analysis, grounds the model on the
  parsed document Markdown, and offers `ExtractKeyInformationAsync` (key→value extraction, JSON-mode) and
  `AskAsync` (document Q&A). DI: `AddOpenAiCompatibleChatModel(...)` / `AddChatModel(...)` +
  `AddPaddleOcrDocumentIntelligence(...)`.
- **Chart-to-data parsing** — `IDocumentIntelligence.ParseChartsAsync` (image-path / `Image<Rgb24>` /
  pre-computed `StructureResult` overloads) detects chart/plot regions and reconstructs each chart's
  underlying data as a GitHub-flavored Markdown table via a **vision-capable** `IChatModel` — the
  provider-agnostic equivalent of PaddleOCR's PP-Chart2Table (OpenAI `gpt-4o`, Azure, or local Ollama
  `qwen2.5-vl` / `llama3.2-vision`; no local GPU, the provider does the vision inference). It crops each
  detected region and sends only those pixels to the model; new `ChartParseResult` / `ParsedChart` result
  types, plus `DocumentIntelligenceOptions.ChartExtractionSystemPromptOverride` to customize the prompt.
  Throws `NotSupportedException` when the configured model isn't vision-capable and the document has charts.
- **Offline (non-LLM) Key-Information Extraction** — `IOfflineKeyInformationExtractor` /
  `OfflineKeyInformationExtractor`, a heuristic, geometry-based extractor (no LLM, no network, CPU-only) that
  resolves each requested key from OCR layout — inline (`Key: value`), value-to-the-right, or value-below.
  `Extract(OcrResult, keys)` plus `ExtractAsync(imagePath/Image, keys)` overloads return the same
  `KeyInformationResult` as the LLM path (`Usage` / `Model` / `RawJson` left `null`). The offline alternative
  to `ExtractKeyInformationAsync`; best-effort on clearly labeled forms/invoices. DI: `AddPaddleOcrOfflineKie()`
  (requires `AddPaddleOcrNet()`).
- **Image-embedding export overloads** — `StructureResult.ToDocx(Image<Rgb24>)` and
  `ToHtml(Image<Rgb24>, title?)` crop figure/chart/seal regions from the supplied source image and embed them
  as **real pixels** (DOCX inline `word/media/` image part; HTML `data:image/png;base64,…` `<img>`). The
  existing no-image overloads keep their bbox-placeholder behavior.
- **Native Word equations (OMML)** — the image-aware DOCX exporter renders recovered formula LaTeX as native
  Word equations via a best-effort LaTeX→OMML converter (`PaddleOcrNet.Structure.Export.LatexToOmml.Convert`)
  — fractions, sub/superscripts, roots, Greek letters, n-ary sum/integral/product, and common operators;
  unsupported constructs degrade gracefully to text (previously `$$…$$` text).

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
- **The full structure model set is hosted and SHA-256 verified.** Layout (`PP-DocLayoutV3`, PicoDet S/M),
  tables (`SLANet_plus` plus the SLANeXt v2 wired/wireless models + table classifier), formula (LaTeX-OCR),
  and the PP-OCRv4 seal detector all download on first use and run end-to-end (see `VALIDATION.md`). The
  RT-DETR table-cell detectors are hosted but not yet wired into the pipeline — SLANeXt already recovers cells
  from its own location head, so this is an optional accuracy enhancement rather than a gap.

[1.0.0]: https://github.com/FarhanLodi/PaddleOcrNet/releases/tag/v1.0.0
