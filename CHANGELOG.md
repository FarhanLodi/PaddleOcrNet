# Changelog

All notable changes to PaddleOcrNet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.2] - 2026-08-28

### Fixed

- **Every language pack decoded one character class off** ([#1](https://github.com/FarhanLodi/PaddleOcrNet/issues/1)).
  The per-script PP-OCRv5 dictionaries (`cyrillic_dict.txt`, `latin_dict.txt`, `arabic_dict.txt`,
  `devanagari_dict.txt`, `korean_dict.txt`, `japan_dict.txt`, `th_dict.txt`, `ppocrv5_el_dict.txt`,
  `te_dict.txt`, `ta_dict.txt`, `ppocrv5_eslav_dict.txt`) begin with an **empty first line** — that line is
  the CTC blank slot itself, and the class the file leaves off is the trailing space. `CharacterDictionary`
  saw only that the file was one class short of the network and prepended a *second* blank, shifting every
  class by one: `ИСТОРИЯ РОССИЙСКОГО ГОСУДАРСТВА` came out as `ЗРСНПЗЮꚟПНРРЗИРЙНВНꚟВНРТГЏПРСБЏ`.
  `BuildVocab` now checks whether the first line is empty and builds `["blank"] + lines[1..] + [" "]` in that
  case, which restores the exact class alignment the models were trained with. The dictionary *files* were
  correct all along and are unchanged — the bug was purely in how they were interpreted, so no re-download is
  needed. Affected: Russian and every other Cyrillic language, Latin-script languages routed to the `latin`
  pack (fr, de, es, it, pt, …), Arabic, Devanagari, Korean, Japanese (`ja_full`), Thai, Greek, Telugu, Tamil
  and East Slavic. The default Chinese/English/Japanese recognizers on `ppocrv5_dict.txt` were never affected —
  that file ships as the complete class list and took a different branch.
- **Non-ASCII text is no longer escaped in JSON output** ([#2](https://github.com/FarhanLodi/PaddleOcrNet/issues/2)).
  `StructureResult.ToJson()` and `OcrResult.ToJson()` inherited the System.Text.Json default encoder, which
  escapes everything outside Basic Latin — so Cyrillic, Greek, Arabic, Hebrew, CJK, Devanagari … reached the
  caller as `\uXXXX` sequences: valid JSON, but unreadable in Notepad and friends. Both exporters now
  serialize through `PaddleOcrJson.Encoder` (`JavaScriptEncoder.Create(UnicodeRanges.All)`) and emit those
  scripts verbatim. HTML-sensitive characters (`< > & ' +`) are still escaped — deliberately not
  `UnsafeRelaxedJsonEscaping`, since blocks carry `TableHtml` that may be embedded in a page. The decoded
  text is unchanged either way; only readability differs.

### Added

- **`ToJson(JsonSerializerOptions)` overloads** on `StructureResult` and `OcrResult`, for callers who want a
  different encoder, indentation, or naming policy. The options are copied onto the source-generated context,
  so the overload stays trim / Native-AOT safe and leaves the caller's options instance mutable and reusable.
  Passing `Encoder = JavaScriptEncoder.Default` restores the pre-2.0.2 escaping.
- **`PaddleOcrNet.Models.PaddleOcrJson.Encoder`**, the exporters' default `JavaScriptEncoder`, exposed so
  callers can reuse it in their own `JsonSerializerOptions`.
- **`StructureOptions.LayoutScoreThreshold`** — the layout-detection confidence floor is now configurable
  ([#3](https://github.com/FarhanLodi/PaddleOcrNet/issues/3)). It was a `private const float
  ScoreThreshold = 0.5f` inside `RtDetrLayoutDetector` and `PicoDetLayoutDetector`, so callers could not
  keep the regions the detector was unsure about (or discard the marginal ones). Default is unchanged at
  `0.5` — the confidence floor both PP-DocLayoutV3 and PP-DocLayout-S/M ship in their own model
  configs — and the comparison stays exclusive (`score > threshold`). The
  threshold is passed per `Detect` call rather than held on the detector, because the engine caches one
  detector instance per `LayoutModel` and reuses it across calls with different `StructureOptions`.
  `StructureOptions.DefaultLayoutScoreThreshold` exposes the default as a constant.
- **Layout regions are now cleaned up before recognition.** The detectors emit a fixed top-k of candidates
  with no NMS, so the same area of the page is routinely proposed several times under different labels, and
  every one of those candidates used to reach the caller. A new post-processing stage collapses them:
  regions overlapping by more than 70% of the smaller box merge into the larger, sub-6px slivers and
  `reference` markers (whose text is carried by the neighbouring `reference_content` block) are dropped, an
  `inline_formula` overlapping its paragraph by more than 50% is absorbed, and `image` regions covering
  essentially the whole page — the "this entire scan is one photograph" false positive — are discarded.
  Controlled by `StructureOptions.FilterOverlappingRegions`, on by default. At the default score threshold
  this changes nothing measurable (duplicates rarely score that high — on the test corpus the region
  counts are identical); at a lowered threshold it is the difference between 31 regions and 25, or 21 and 14.
- **`StructureOptions.LayoutNms`, `LayoutUnclipRatio` and `LayoutMergeMode`** — three optional layout
  passes, all off by default. `LayoutNms` suppresses a lower-scoring region overlapping a kept one by more
  than 0.6 IoU within a class (0.98 across classes). `LayoutUnclipRatio` grows every region about its centre
  by a ratio, for detectors whose boxes clip ascenders out of the crops handed to the recognizers.
  `LayoutMergeMode` resolves nested regions — `Large` keeps the enclosing block, `Small` the inner ones,
  `None`/`Union` keep both; formulas are never absorbed by the text around them.
- **Reading order now comes from the layout model when it predicts one.** PP-DocLayoutV3 is trained to emit a
  reading-order index alongside each box (the trailing column of its detection rows); that index was being
  discarded and order re-derived geometrically. It is now surfaced as `LayoutRegion.OrderIndex` and used to
  sequence the blocks, falling back to the XY-cut orderer for the PicoDet and PP-DocLayout_plus-L exports,
  which emit no such column. `StructureOptions.ReadingOrder` selects: `Auto` (default, model-then-XY-cut),
  `Model`, or `XyCut` to keep the previous behaviour on every model.
- **`LayoutRegion.RawLabel`** — the model's own label name for the region (`reference_content`,
  `inline_formula`, `vision_footnote`, …), alongside the existing `RawClassId`. `StructureBlockType`
  deliberately collapses several of these onto one value, so the raw name is what the clean-up filters key
  off, and it is useful diagnostically when a region maps to an unexpected type.

## [2.0.1] - 2026-08-27

### Fixed

- **Corrected `LayoutModel` documentation.** `LayoutModel.RtDetrL` was documented as
  "PP-DocLayout_plus-L — the RT-DETR-based layout detector (highest accuracy, heaviest)", but the engine
  has always resolved it to **PP-DocLayoutV3**. The XML doc now says so, and records why plus-L is not
  exposed: at 20 classes it is strictly dominated by PP-DocLayoutV3's 25 — V3 splits `formula` into
  `display_formula` / `inline_formula` and adds `header_image`, `footer_image`, `vertical_text` and
  `vision_footnote` — so selecting it could only ever be a downgrade.
- **Corrected `PicoDetLayoutDetector` documentation**, which claimed no PicoDet layout ONNX was published
  and that the detector was therefore unused. Both PP-DocLayout-S and -M are now hosted, so
  `LayoutModel.PicoDetS` / `PicoDetM` work.

### Model hosting (no package upgrade required)

These are asset-side changes to the public model repo. The SHA-256 pins for all three were already
correct in 2.0.0, so **2.0.0 consumers pick them up automatically** — no upgrade needed:

- **`PP-OCRv4_server_seal_det.onnx` is now published.** `StructureOptions.RecognizeSeals` defaults to
  `true` and the seal branch is unguarded, so a document whose layout contained a seal region previously
  threw on the missing model. Verified byte-identical to the pinned checksum.
- **`PP-DocLayout-S` / `PP-DocLayout-M` (+ label sidecars) are now published**, so
  `LayoutModel.PicoDetS` / `PicoDetM` resolve instead of failing with a 404.

### Notes

- `PP-DocLayout_plus-L` and the RT-DETR table-cell detectors remain unpublished and unreferenced by any
  code path; their registry checksums were refreshed against the current export toolchain but nothing
  resolves them.

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
