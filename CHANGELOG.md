# Changelog

All notable changes to PaddleOcrNet are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.0] - 2026-09-03

Two large pushes in one release: an **accuracy pass** that aligns the OCR pipeline with Python
PaddleOCR 3.x stage by stage, and a **rebuild of the PP-StructureV3 document-analysis orchestration**
around the same whole-page design the Python pipeline uses. Several defaults change as a result — every
change is listed under *Changed defaults* below with the one-liner that restores the old behavior.

### Accuracy — parity with Python PaddleOCR 3.x

Each stage of the OCR pipeline was diffed against PaddleX / PaddleOCR 3.x and brought into line. The
sum of these is a substantial accuracy improvement, most visibly on high-resolution scans, small text,
vertical text, seals and non-Latin scripts:

- **BGR tensor inputs.** The exported detection, recognition and UVDoc models consume **BGR** planes
  (their `inference.yml` says `DecodeImage img_mode: BGR`); PaddleOcrNet was feeding RGB, so every model
  saw its red and blue channels swapped. Detection, recognition, and UVDoc unwarp now build BGR tensors
  (and flip UVDoc's BGR output back).
- **Vertical text is rotated before recognition.** Tall crops (height/width ≥ 1.5) are rotated 90°
  counter-clockwise so vertical lines reach the recognizer horizontally — PaddleOCR's
  `get_rotate_crop_image` `np.rot90` rule, previously missing entirely.
- **The text-line orientation classifier was preprocessing wrongly — and is now on by default, but
  guarded.** Its input was resized with aspect preserved + zero padding and normalized to [−1, 1];
  PaddleX stretch-resizes to exactly 160×80 (bilinear) and ImageNet-normalizes. Fixed, and
  `RecognitionOptions.UseTextLineOrientation` now defaults to **true** (the Python pipeline default), so
  upside-down lines are corrected out of the box.

  Reaching preprocessing parity then exposed a problem in the upstream design, so PaddleOcrNet
  deliberately goes further than PaddleX here. PaddleX 3.x acts on the classifier's plain `argmax` with
  no confidence gate, and the classifier **misfires on upright text** — including confidently. Acting on
  a wrong verdict feeds the recognizer an upside-down crop, replacing a clean line with transliterated
  gibberish (`processing` comes back as `ussaooud`). Measured with the bundled corpus and the identical
  ONNX weights driven by Python PaddleOCR, mean recognition confidence on an upright multi-column page
  was **0.977 with the classifier off, 0.783 with it on** — 5 of 17 lines destroyed; a Devanagari page
  fell from **0.970 to 0.827**, its body text reduced to noise. Two guards close this:
  - `RecognitionOptions.TextLineOrientationThreshold` (default **0.9**, PaddleOCR 2.x's `cls_thresh`,
    which 3.x dropped) ignores low-confidence verdicts.
  - `RecognitionOptions.VerifyOrientationByRecognition` (default **true**) confirms each surviving
    verdict by recognizing that crop in both orientations and keeping the more confident reading —
    a genuinely inverted line reads far better rotated, a misfire reads far worse, so a wrong verdict
    costs only the extra recognition of the flagged lines.

  The same guard covers the page-level classifier: when `UseDocOrientation` uprights a page, every line
  is confirmed the same way, because a wrong page verdict corrupts the whole document at once (observed:
  an upright corpus page classified as 180°, mean confidence **0.461**; **0.945** with confirmation, against
  Python's 0.941). Together these turn orientation handling from a coin-flip into a net win: on the two
  pages above PaddleOcrNet now scores **0.978** and **0.968** — above the classifier-off baselines and
  well above Python's classifier-on results. Set the threshold to 0 and the verification to false for
  exact PaddleX 3.x behaviour.
- **Detection runs at near-native resolution.** `DetectionOptions` defaults moved from
  downscale-longest-side-to-960 to Python PaddleOCR 3.x's `OCR.yaml`: `limit_type=min` with
  `LimitSideLen` 64 and a new `MaxSideLimit` cap of 4000 px. Large scans are no longer crushed to 960 px
  before detection — the single biggest accuracy lever in this release for dense/high-res pages (and the
  main throughput cost; see *Changed defaults*). Tiny inputs (w + h < 64) are zero-padded to ≥ 32×32 and
  dimension rounding matches Python's `int()` truncation.
- **Recognition batch width matches Python.** The batch tensor width is now
  `imgW = int(H · max(320/H, widest crop's w/h))`, capped at 3200 px — i.e. a **320 px minimum width**
  (PaddleOCR's `rec_image_shape` [3, 48, 320]) instead of padding to the batch's own widest crop. Narrow
  crops no longer run through a much narrower tensor than the model was trained on.
- **Resampler parity.** Rectification of rotated quads now uses OpenCV's `INTER_CUBIC` bicubic kernel
  with `BORDER_REPLICATE` edge handling (was bilinear); the resizes that cv2 does with its default
  `INTER_LINEAR` (detector input, classifier input, recognition crops) now use bilinear (some were
  bicubic). Small tilts no longer take the axis-aligned fast path.
- **8-connectivity in DB post-processing.** Connected-component labeling is now 8-connected, matching
  `cv2.findContours` semantics — diagonally-touching strokes form one region instead of splitting.
- **Post-detection NMS is off by default.** Python PaddleOCR has no NMS after DB post-processing
  (nested/adjacent boxes may legitimately overlap); `DetectionOptions.NmsIouThreshold` now defaults to
  disabled (was 0.6).
- **`DropScore` defaults to 0.** Python's `drop_score` returns every reading; the previous 0.5 default
  silently discarded low-confidence lines. Deliberate deviation kept: empty/whitespace-only readings are
  still dropped.
- **Seal detection and rectification rebuilt.** The seal detector now upscales the crop's short side to
  736 px (`limit_type=min`, long side capped at 4000) as the PaddleX seal config does, post-processes in
  **polygon mode** (a port of `polygons_from_bitmap` — curved lines keep their true N-point outline
  instead of being flattened to a min-area quad), and rectifies each polygon with a port of
  `get_poly_rect_crop`: near-rectangular outlines take the plain quad warp, genuinely curved arcs are
  straightened piecewise into a strip. Together with the 2.0.4 plain-OCR fallback this makes seal text
  recovery actually work on curved stamps.
- **Document-orientation classifier preprocessing.** Now aspect-preserving resize (short side 256) +
  center-crop 224, as PaddleX does — was a stretch straight to 224×224, which distorted non-square pages.
- **UVDoc unwarp runs at page resolution.** The page is fed at its own resolution (stride-padded, then
  cropped), as PaddleX does, instead of a fixed 488×712 round trip that blurred the dewarped page. The
  working size is capped (memory) and only upscaled back when the cap applied.
- **Arabic-script output is reordered for display.** Python applies `bidi.get_display` to the arabic
  pack's output; PaddleOcrNet now applies an equivalent dependency-free reorder (RTL runs reversed,
  Latin words and digit sequences kept intact, brackets mirrored), so Arabic/Persian/Urdu text is no
  longer returned visually backwards.

### PP-StructureV3 — document analysis rebuilt around whole-page OCR

`AnalyzeDocumentAsync` now follows Python PP-StructureV3's `pipeline_v2.py` orchestration instead of
cropping and OCR-ing each layout region independently ([#5](https://github.com/FarhanLodi/PaddleOcrNet/issues/5)
reported structure text lagging plain OCR — this is the fix):

- **One whole-page OCR pass + line-to-block matching.** The page is OCR'd once (det + cls + rec) and
  each line is assigned to the layout block with the largest overlap ratio. Lines that straddle two or
  more blocks with no dominant owner ("hurdle" lines) are cropped at the block borders and each fragment
  **re-recognized**, so both blocks get their own share of the text. Text blocks that matched no lines
  fall back to the old per-block crop OCR so faint regions still get a reading.
- **Formulas are recognized first, masked out, and spliced back.** Formula regions are recognized before
  the page OCR pass and their pixels whited out on a working copy (so the text recognizer never reads
  half a formula); each inline formula is then spliced into its owning block — or table — as a
  `$…$` span, matching Python's output shape.
- **XY-Cut++ reading order by default.** A port of PP-StructureV3's `xycut_enhanced` orderer — recursive
  XY-cuts plus label-aware rules (headers first, footers last, title/vision handling). Python never uses
  the layout model's raw order column for final ordering, and `LayoutReadingOrder.Auto` now resolves to
  `XyCutEnhanced` accordingly (`Model` and plain `XyCut` remain selectable).
- **Per-class layout thresholds and merges, and NMS on by default.** New
  `StructureOptions.LayoutClassThresholds` / `LayoutClassMergeModes` mirror PaddleX's dict-typed
  `threshold` / `layout_merge_bboxes_mode`; left null, the PP-StructureV3.yaml per-class defaults apply
  (`paragraph_title` 0.3, `text` 0.4, `formula` 0.3, `seal` 0.45; containment-merge `large` for
  titles/images/formulas/charts). `LayoutNms` now defaults to **true** — the pipeline config ships
  `layout_nms: True`. Label post-fixes (footnote relabel, lone-title promotion) run after filtering, as
  in Python.
- **Python-parity text assembly and Markdown.** Block text is assembled with PP-StructureV3's
  line-joining rules (soft-hyphen collapse, CJK-aware joins), and `ToMarkdown` now takes a
  `MarkdownRenderOptions`: page furniture (headers, footers, page numbers, footnotes, margin notes) is
  **omitted by default** like Python's `markdown_ignore_labels`, consecutive text runs merge into
  paragraphs via seg-start/seg-end geometry, numbered titles map to heading levels (`format_title`), and
  `ConcatenateMarkdownPages` joins pages by paragraph-continuation flags (space, or nothing at a CJK
  boundary) instead of a `---` horizontal rule.
- **Table orientation classification.** `StructureOptions.UseTableOrientationClassification` (default
  true, as in Python) detects sideways table crops with the doc-orientation classifier, uprights them,
  and re-OCRs them locally so rotated tables recognize correctly.
- **SLANeXt's wireless leg now runs SLANet_plus.** The v2 table router pairs `SLANeXt_wired` (512,
  content-normalized) for bordered tables with `SLANet_plus` (488, canvas-normalized) for borderless
  ones — the exact model pairing PP-StructureV3.yaml ships. This sidesteps the SLANeXt wireless
  geometry problems recorded in 2.0.3/2.0.4 for borderless tables.
- **`StructureBlock.CellBounds`** — recognized tables now expose their per-cell rectangles in page
  coordinates, alongside the existing `TableHtml`.
- **Empty-layout fallback**: when layout detection finds nothing, each OCR line becomes its own `text`
  block instead of the document coming back empty.

### Added

- **Server detection and recognition models.** `PaddleOcrServiceOptions.DetectionModel` and
  `RecognitionModel` take `OcrModelVariant.Mobile` (default, unchanged) or `Server`, selecting
  `PP-OCRv5_server_det` / `PP-OCRv5_server_rec` — roughly 3–5× larger than the mobile networks and more
  accurate. The two sides are independent, so the server detector can be used with mobile recognizers. The
  server *recognizer* covers Chinese / English / Japanese only (it is built on `ppocrv5_dict.txt`); every
  other language pack stays on its mobile recognizer regardless of the setting. Assets download, cache and
  SHA-256 verify exactly like the mobile ones ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)).
- **Local model files.** `PaddleOcrServiceOptions.DetectionModelPath`, `RecognitionModelPath` and
  `RecognitionDictionaryPath` load ONNX/dictionary files straight from disk **instead of** the registry —
  no download is attempted and no checksum is verified (the file is trusted as-is). Pair with
  `Download.Offline = true` to guarantee the process never touches the network
  ([#4](https://github.com/FarhanLodi/PaddleOcrNet/issues/4)).
- **`RecognitionOptions.CropPadding`.** A white border, in pixels, added around every rectified line crop
  before recognition (default `0`, i.e. no change to existing behaviour). It helps when the detected box
  hugs the glyphs and the recognizer clips the first or last character; 10–20 px is a sensible range. The
  padding is applied before the text-line orientation classifier, so classification and recognition see the
  same pixels, and reported coordinates still refer to the original image
  ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)).
- **`PaddleOcrServiceOptions.DeviceId`** — zero-based accelerator index for the CUDA / DirectML
  providers, the equivalent of Python's `device="gpu:1"`.
- **~30 new languages.** Latin: Finnish, Basque, Galician, Luxembourgish, Romansh, Catalan, Quechua.
  Cyrillic: Kazakh, Kyrgyz, Tajik, Macedonian, Tatar, Chuvash, Bashkir, Meadow Mari, Moldovan (Cyrillic),
  Udmurt, Komi, Ossetian, Buryat, Kalmyk, Tuvan, Yakut, Karakalpak. Arabic script: Pashto, Sindhi,
  Balochi. All are `OcrLanguage` enum members routed to their script's existing recognizer pack. Also:
  a **dedicated English pack** (`en_PP-OCRv5_mobile_rec` on `ppocrv5_en_dict.txt`) now serves
  `OcrLanguage.English`, matching Python's `lang="en"` routing, and plain `ru` / `uk` / `be` route to the
  **East-Slavic** pack (see *Fixed*).
- **`PaddleOcrService.GetRuntimeInfo()`** returns an `OcrRuntimeInfo` snapshot answering "why is my GPU
  not used?" in one call: the loaded ONNX Runtime version, its available providers, the
  requested → resolved → active provider journey, the host GPU probe, and an actionable hint.
  `ToString()` renders it as a ready-to-log report. New `PaddleOcrService.ActiveExecutionProvider` and
  `OcrResult.ExecutionProvider` report the provider inference is **actually** running on, and a provider
  append failure is no longer invisible: when no `ILogger` is configured, the hint is written once per
  process to stderr ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)).
- **`RecognitionOptions.BatchSize` is now honored per call** (previously the recognizer always used its
  construction-time batch size); changing it never reloads the cached session. The phantom
  per-box-parallel path that capped ONNX intra-op threads at 1 is gone, so recognition uses the runtime's
  full intra-op parallelism.

### Fixed

- **`PaddleOcrNet.Gpu` never actually used the GPU.** Not on a misconfigured machine — on any machine.
  `PaddleOcrNet` depends on `Microsoft.ML.OnnxRuntime` (CPU natives) and the GPU package adds
  `Microsoft.ML.OnnxRuntime.Gpu`, so a consumer's graph contained both. The two ship the same native asset
  path (`runtimes/win-x64/native/onnxruntime.dll`, `runtimes/linux-x64/native/libonnxruntime.so`) and NuGet
  awards that one slot to a single package — the CPU one. The result was the 246 MB
  `onnxruntime_providers_cuda.dll` deployed beside a core runtime with no CUDA support compiled in, which
  will never load it. Measured on a plain console app referencing `PaddleOcrNet.Gpu` 2.0.4:
  `OrtEnv.GetAvailableProviders()` returned `AzureExecutionProvider, CPUExecutionProvider` and the deployed
  `onnxruntime.dll` was the CPU build (15,381,816 bytes, against the GPU build's 15,837,496). Because CUDA
  was absent from the provider list, `Auto` resolved straight to CPU without ever *attempting* an append —
  so there was no exception, no warning and no log line, which is exactly how it was reported
  ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)). The GPU package now ships a
  `buildTransitive` targets file that copies the GPU build's core runtime over the CPU copy after build and
  after publish, leaving `deps.json` and the NuGet item graph untouched. Verified across portable build,
  incremental rebuild, portable publish and RID-specific publish: all four now report
  `TensorrtExecutionProvider, CUDAExecutionProvider, CPUExecutionProvider`. A CPU-only consumer of plain
  `PaddleOcrNet` is unaffected. Opt out with `<PaddleOcrNetGpuPreferGpuRuntime>false</PaddleOcrNetGpuPreferGpuRuntime>`
  ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)).
- **`UseGpu` / `UsedGpu` reported the *requested* provider, not the one in use.** A CUDA attach failure
  fell back to CPU while `PaddleOcrService.UseGpu` and `OcrResult.UsedGpu` kept saying `true`. Both now
  reflect the **live** active provider at call time, `OcrResult.ExecutionProvider` records which provider
  produced each result, and the OpenTelemetry activity tags carry the same truthful values
  ([#6](https://github.com/FarhanLodi/PaddleOcrNet/issues/6)).
- **Two GPU diagnostics pointed at the wrong culprit.** The CPU-fallback hint told anyone on an NVIDIA host
  to "install the `PaddleOcrNet.Gpu` NuGet package" — including the people who already had, who were
  precisely the ones hitting the conflict above. It now recognizes that state from the deployed files (the
  CUDA provider library present while the loaded runtime denies having CUDA, a combination no missing
  driver or CUDA-major mismatch can produce) and explains it instead. The check runs before, and
  independently of, the hardware probe, so it also fires on Linux, where the probe is a no-op. Separately, a
  provider that failed to *attach* also advised installing the package; when the loaded runtime does contain
  the provider, the message now says so and points at the native dependencies (driver, CUDA runtime, cuDNN)
  rather than the NuGet graph.
- **The "Japanese" pack was not a PP-OCRv5 model.** The registered `japan_PP-OCRv5_mobile_rec.onnx` is
  byte-identical to the **PP-OCRv3** japan model — upstream ships no Japanese PP-OCRv5 recognizer at all.
  The mislabeled pack is dropped (the file stays hosted but unreferenced); `ja` / `japan` / `ja_full` now
  route to the default PP-OCRv5 recognizer, whose `ppocrv5_dict.txt` covers Japanese — exactly where
  Python sends `lang="japan"` (its server variant is the opt-in `RecognitionModel = Server`).
- **`ru` / `uk` / `be` recognized with the wrong pack.** Python's `ESLAV_LANGS` take priority over the
  generic Cyrillic group, so Russian, Ukrainian and Belarusian belong to the East-Slavic recognizer
  (`ppocrv5_eslav_dict.txt`); PaddleOcrNet was routing them to the generic `cyrillic` pack. The plain
  codes now resolve to the East-Slavic pack (the `*_eslav` aliases still work, and
  `OcrLanguage.Cyrillic` still selects the generic pack explicitly). Part of why Russian text read
  noticeably worse than it should have ([#5](https://github.com/FarhanLodi/PaddleOcrNet/issues/5)).
- **The structure engine cached its pre-processor and seal recognizer against the wrong key.** The
  document pre-processor was built once with whichever orientation/unwarp sessions the *first* call
  needed, so a later call requesting a session the cached instance lacked silently ran without it; the
  seal recognizer was similarly cached ignoring the language set. Both caches are now keyed on what the
  cached instance actually holds and rebuild when a call needs more.
- **`AnalyzeDocumentAsync` no longer builds a second OCR engine.** The structure engine now shares the
  service's `PaddleOcrEngine` (and its det/cls/rec ONNX sessions) instead of constructing a duplicate —
  half the sessions, half the memory, no double model load.
- **`TextGrouping.Line` never merged anything.** The default grouping documented "adjacent boxes on the
  same line are merged into one result" but every detected box came back as its own line. Boxes whose
  vertical overlap is at least half the smaller box's height are now merged left-to-right with single
  spaces, with the union box as geometry.

### Changed defaults — how to restore 2.0.x behavior

The parity work above changes several defaults. Each row shows the old behavior and the exact setting
that restores it:

| Changed default | Was → now | Restore the old behavior |
| --- | --- | --- |
| Detection resolution | downscale longest side to 960 → near-native (`limit_type=min`, 64, capped 4000). More accurate, but large images cost more time/memory. | `Detection = new DetectionOptions { LimitSideLen = 960, LimitTypeMax = true }` |
| `RecognitionOptions.DropScore` | 0.5 → 0.0 (return everything, like Python) | `DropScore = 0.5` |
| Text-line orientation | off → **on** per recognition call, gated at 0.9 confidence and confirmed by recognition | `UseTextLineOrientation = false` |
| Orientation verdicts | acted on unconditionally → gated + confirmed (see above; PaddleX 3.x acts on raw argmax) | `TextLineOrientationThreshold = 0`, `VerifyOrientationByRecognition = false` |
| Post-detection NMS | IoU 0.6 → disabled (like Python) | `Detection = new DetectionOptions { NmsIouThreshold = 0.6 }` |
| `StructureOptions.LayoutNms` | false → **true** (`layout_nms: True` in PP-StructureV3.yaml) | `LayoutNms = false` |
| Layout per-class thresholds/merges | none → PP-StructureV3.yaml defaults active | `LayoutClassThresholds = new Dictionary<string, float>()` and/or `LayoutClassMergeModes = new Dictionary<string, LayoutMergeMode>()` (empty = global threshold / no merging) |
| `StructureOptions.ReadingOrder` `Auto` | model order, XY-cut fallback → XY-Cut++ | `ReadingOrder = LayoutReadingOrder.Model` |
| Markdown block set | every block rendered → page furniture (headers/footers/page numbers/footnotes/margin notes) omitted | `doc.ToMarkdown(new MarkdownRenderOptions { IgnoredBlockTypes = Array.Empty<StructureBlockType>() })` |
| Markdown page joins | `---` rule between pages → paragraph-continuation joins | `ConcatenateMarkdownPages(pages, new MarkdownRenderOptions { PageSeparator = StructureMarkdownExtensions.PageSeparator })` |
| `en` recognizer | default ch/en/ja pack → dedicated English pack | pass `OcrLanguage.ChineseSimplified` (the `ch` pack still covers English) |
| `ru`/`uk`/`be` recognizer | generic Cyrillic pack → East-Slavic pack | pass `OcrLanguage.Cyrillic` |
| `UseGpu` semantics | "a GPU was selected" → "a GPU is actually in use right now" | none needed — the old value was simply wrong when attach failed |

## [2.0.4] - 2026-09-01

### Fixed

- **Seal regions no longer lose their text.** `RecognizeSeals` is on by default, and a
  `StructureBlockType.Seal` region is routed to the curved-text seal recognizer instead of the
  ordinary OCR path. When that recognizer rectifies nothing it returned zero lines, and the block reached the
  caller with `Text: null` — so every word printed on the stamp was silently dropped from
  `AnalyzeDocumentAsync`, even though the region itself was detected confidently. On the bundled `seal.png`
  the region scores 0.96 and came back empty, while plain OCR over the same pixels reads 发票专用章 and
  吗繁物. The seal branch now falls back to ordinary OCR over the same crop when the seal recognizer yields
  no lines, which costs one extra pass only in that case and can only add text where there was none. That
  fixture now returns "首页 / 发票专用章 / 吗繁物". This supersedes the second entry under "Known issues" in
  2.0.3; the curved-text recognizer itself is still the weaker path and is unchanged here.
- **`TableRecognitionModel.SlaNeXt` decoded its cell boxes with the wrong coordinate convention.** The
  location head is normalized against the resized *content*, while SLANet_plus normalizes against the padded
  *canvas* — and the recognizer applied the canvas formula to both. That inflated every SLANeXt y by
  `max(origW, origH) / origH`, so cells swallowed the rows beneath them and the rest clamped to the bottom
  edge. Measured against SLANet_plus's verified boxes on the 550×345 fixture the regression is
  `y_slanext = 1.576 · y_slanet` against an expected 550/345 = 1.594; on the 551×132 fixture 24 of 26 boxes
  had overflowed. The convention is now a per-model property, so the two SLANeXt legs rescale x by `origW`
  and y by `origH`. Empty cells fell from 54 to 33 on the 16×6 fixture and from 8 to 4 on the 4-row one.
  **This is an improvement, not a cure:** the fit explains only ~89% of the variance, so a residual error
  remains and `SlaNeXt` still misplaces cell text. **`SlanetPlus` remains the default and the accurate
  option** — it is untouched by this change and still places every value correctly. Partially supersedes the
  first entry under "Known issues" in 2.0.3.

## [2.0.3] - 2026-09-01

### Fixed

- **Multi-line table cells collapsed onto one line.**
  A cell usually swallows several detection boxes: fragments of one printed line, and — for a wrapped
  paragraph — several stacked lines. `SlanetTableRecognizer` joined all of them with spaces (what PaddleOCR's
  `get_pred_html` does), so a cell holding a heading plus a body paragraph came out as one run and the rows of
  a notes column ran together. The recognizer now rebuilds the cell's visual rows from the matched boxes'
  vertical overlap — same printed line when they overlap by at least half the shorter box height — joins
  fragments within a row with a space, and separates rows with `<br>`. Rows are ordered top-to-bottom and
  left-to-right regardless of the order the OCR lines arrived in.
- **`<br>` in a cell survives into DOCX and XLSX.** `OoxmlHtmlTable` stripped every inner tag from cell
  content, so the new break would have vanished along with it. It now maps `<br>` / `<br/>` / `<br />` to a
  newline; `StructureDocxExporter` emits a real `<w:br/>` run for it (a literal newline inside `<w:t>` renders
  as a space in Word), and `StructureXlsxExporter` writes a minimal `xl/styles.xml` and tags multi-line cells
  with a wrap-text format so Excel shows every line. Multi-line **text** blocks pick up the same Word fix.
- **`StructureResult.ToHtml()` degraded every real table to fallback text.** The recognizer returns PaddleOCR's
  whole `<html><body><table>…</table></body></html>` document, but `RenderTable`'s table-rooted validity
  check requires a bare `<table>` fragment, so it always failed and emitted the block's plain text instead of
  the recovered grid. Both text exporters now take the `<table>` span out of the wrapper first — which also
  stops `ToMarkdown()` from pasting a nested `<html>`/`<body>` into the page. `StructureBlock.TableHtml` and
  `TableResult.Html` are unchanged and still carry the wrapper verbatim, for parity with Python.
- **`TextGrouping.Paragraph` produced no paragraph breaks in `OcrResult.FullText`.** Every block was joined
  with a single newline — the same separator the paragraph grouper puts *between the lines inside* a
  paragraph — so the grouping carried no information into the text at all. Paragraph mode now separates
  blocks with a blank line (a block's own trailing newline is trimmed first, so the gap never widens).
  `Word` and `Line` grouping are unchanged.
- **A CUDA toolkit mismatch now explains itself.** ONNX Runtime moved its GPU build to CUDA 13 in 1.27, so
  `PaddleOcrNet.Gpu` looks for `cublasLt64_13.dll` and, on a machine with the CUDA 12 toolkit, fails to attach
  the provider and falls back to CPU. All the user saw was ONNX Runtime's raw `Error 126` text, which reads
  like a broken CUDA install rather than a version mismatch. When the failure names a missing CUDA library the
  warning now says which CUDA major version the loaded runtime was built against (from the `_13` / `_12`
  suffix), which ONNX Runtime releases match it, and that a direct `PackageReference` overrides the version
  the package brings in. The ONNX Runtime reference itself is **unchanged at 1.27.0** — see below.

### Documentation

- **How to run the GPU package on a CUDA 12 machine.** `PaddleOcrNet.Gpu` targets CUDA 13, as every ONNX
  Runtime from 1.27 onward does, and no earlier PaddleOcrNet release helps (all of them reference 1.27.0).
  The GPU package README now spells out the three ways out: install the CUDA 13 runtime alongside CUDA 12
  (the majors coexist — their libraries are suffixed), pin ONNX Runtime 1.26 in your own project (both
  packages, plus `NoWarn NU1605`, since a direct reference below a transitive one counts as a downgrade), or
  switch to DirectML on Windows, which needs no CUDA at all and which `ExecutionProvider.Auto` already
  prefers there.
- `OcrExecutionProvider.Auto` named a `PaddleOcrNet.DirectMl` package that was never shipped; the DirectML
  runtime comes from Microsoft's own `Microsoft.ML.OnnxRuntime.DirectML`, which is what the provider
  resolver has always told callers to install.
- **The package now builds warning-free.** Eight XML-doc warnings (`CS1574` / `CS1573`) had been shipping
  since 2.0.0, so the generated documentation file carried broken `<see>` links and missing `<param>` entries
  into consumers' IntelliSense: `RecognitionOptions` pointed at an `ExtractTextFromImage` overload taking
  `IEnumerable<string>` (every list overload takes `IReadOnlyList<OcrLanguage>`), `SealRecognizer` had an
  unresolvable cref to `PerspectiveWarp.Rectify`, and `EmitWithPossibleScripts`, `SplitByGaps` and
  `Preprocess` each documented only some of their parameters.

### Tests

- **The 14 language integration cases had never actually run.** `ModelIntegrationTests` resolved its assets
  as `AppContext.BaseDirectory/../../../test/Assets`, which from `bin/Debug/net10.0` lands inside the test
  project rather than the repo, and its fallback only worked when the runner happened to sit at the repo
  root — so every case failed on the `File.Exists` assert before touching the library. It now walks up to
  `PaddleOcrNet.sln` like every other integration suite. These are the tests that would have caught 2.0.2's
  per-script dictionary bug.
- **New coverage for the fixes above**: unit tests for the cell line-grouping rule, the `<br>` round-trip
  through the Word/Excel writers, the grouping-dependent `FullText` separator and the CUDA-mismatch
  diagnostic; integration tests that drive the real SLANet graph with stacked lines in one cell and run the
  reported document end to end.
- **Capability tests for the asset corpus** — table structure recovery and its Markdown/HTML/DOCX/XLSX
  exports, formula-to-LaTeX, seal region detection, and an orientation check that an upside-down page yields
  at least as much text with `DetectOrientation` on as off.
- **A 100-image accuracy benchmark** over `test/Assets/paddleocrnet_100_test_dataset/` (11 categories: plain
  text, multi-column, tables, forms, receipts, seals, dense mixed, low quality, rotated/perspective,
  numbers/codes, handwriting-like). Because the fixtures are synthetic renders with known content, the suite
  asserts recovered *strings* rather than just "it did not crash": the pangram, e-mail address and product
  name on every plain-text page, the `abcdefghijklmnopqrstuvwxyz 0123456789` character probe on every
  low-quality page, and the `OCR TEST` banner across the titled pages. Baseline at the time of writing —
  **100/100 pages recognized, corpus mean confidence 0.963**, banner recovered on 99% of the 92 titled pages,
  weakest category `rotated_perspective` at 0.942. Floors are set below those levels so the suite catches a
  regression without flaking on model variance.

### Known issues

Both of these are long-standing, were found while testing the fixes above, and are **not** changed by this
release — they are recorded here so they are not mistaken for new behaviour.

- **`TableRecognitionModel.SlaNeXt` misplaces cell text.** Its location head decodes cell boxes that are far
  too tall, and many of them clamp to the bottom of the crop, so OCR lines match into the wrong cells. On
  `medal_table.png` (550×345, a clean 16×6 grid) the mean cell is 2.48× the true row height and 47 of 96
  cells clamp; on `table.jpg` 12 of 13 clamp. The row/column structure decodes correctly — only the geometry
  is wrong — so the symptom is scrambled content rather than a malformed table. `SlanetPlus` (the default)
  places every value correctly on both fixtures, so **use the default**; the enum member's documentation now
  says so. Diagnosing the correct SLANeXt coordinate convention needs reference data we do not have, and a
  guessed rescale risks the working SLANet_plus path, so no speculative fix is shipped here.
- **Seal text is not recovered.** On `seal.png` the layout detector finds the seal region at 0.96 confidence,
  but `SealRecognizer` returns zero lines, so the block's `Text` is null — while plain OCR over the same image
  reads 发票专用章 and 吗繁物. The rectify-then-recognize path inside the seal recognizer produces nothing.

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
