# PaddleOcrNet 2.1.0 — Release Summary

**Date:** 2026-09-03 · **Packages:** `PaddleOcrNet` 2.1.0, `PaddleOcrNet.Gpu` 2.1.0 · **Target:** .NET 10, ONNX Runtime 1.27.0

## The release in one paragraph

2.1.0 is a parity release: every stage of the OCR pipeline was diffed against Python PaddleOCR 3.x /
PaddleX and brought into line (BGR tensor inputs, near-native detection resolution, vertical-text
rotation, corrected text-line orientation classification, Python-exact recognition batch widths,
resampler and connected-component parity, polygon-mode seal rectification, Arabic bidi reordering), and
the PP-StructureV3 document-analysis orchestration was rebuilt around the same whole-page-OCR +
line-to-block matching design Python uses. On top of that: opt-in **server** detection/recognition
models, **local model file** loading for fully offline setups, ~30 new languages, a truthful GPU
diagnostics story (`GetRuntimeInfo()`), and a fix for `PaddleOcrNet.Gpu` never actually engaging the
GPU due to a NuGet native-asset conflict.

## What changed

### Accuracy (plain OCR)

| Area | Change |
| --- | --- |
| Channel order | Det/rec/UVDoc tensors are now **BGR**, as the exported models expect — previously every model saw swapped red/blue channels. |
| Detection resolution | `limit_type=min` (short side ≥ 64, long side ≤ 4000) instead of downscaling the longest side to 960. The single biggest accuracy lever on dense/high-res pages. |
| Vertical text | Tall crops (h/w ≥ 1.5) rotate 90° CCW before recognition (`np.rot90` rule). |
| Text-line orientation | Classifier preprocessing fixed (stretch 160×80 + ImageNet norm) and **on by default** — but guarded, because the classifier misfires on upright text and PaddleX 3.x acts on its raw `argmax`. See *Orientation, and why we deviate*. |
| Recognition batching | Batch width = `int(48 · max(320/48, widest w/h))`, capped 3200 — a 320 px minimum, as trained. |
| Resamplers | Bicubic + replicate-border rectification; bilinear where cv2 uses `INTER_LINEAR`. |
| DB post-process | 8-connected components; post-detection NMS **off** by default; `DropScore` 0. |
| Seals | Polygon-mode DB output + `get_poly_rect_crop` port: curved stamps are straightened piecewise. |
| RTL scripts | Arabic/Persian/Urdu output reordered for display (bidi-equivalent, dependency-free). |

### PP-StructureV3 (document analysis)

One whole-page OCR pass with line-to-block matching (hurdle lines split and re-recognized), formulas
recognized first and masked out of the page, XY-Cut++ reading order by default, PP-StructureV3.yaml
per-class layout thresholds/merges with `LayoutNms` on, Python-parity text assembly and Markdown
(page furniture omitted, paragraph merging, title levels, continuation-aware page concatenation),
table orientation classification, and the wired/wireless table router pairing `SLANeXt_wired` with
`SLANet_plus`. Fixes issue #5 (structure text lagging plain OCR).

### New capabilities

- **Server models** — `DetectionModel` / `RecognitionModel = OcrModelVariant.Server` (server recognizer covers zh/en/ja only; other packs unaffected).
- **Local models** — `DetectionModelPath` / `RecognitionModelPath` / `RecognitionDictionaryPath` + `Download.Offline = true` for a no-network guarantee.
- **GPU truthfulness** — `GetRuntimeInfo()`, `ActiveExecutionProvider`, `OcrResult.ExecutionProvider`; `UsedGpu` now reports the live provider. The `PaddleOcrNet.Gpu` buildTransitive targets fix makes CUDA actually load (issue #6).
- **~30 new languages**, a dedicated English pack for `en`, East-Slavic routing for `ru`/`uk`/`be`, and the mislabeled "Japanese v5" pack retired in favor of the default PP-OCRv5 recognizer.
- `RecognitionOptions.CropPadding`, `PaddleOcrServiceOptions.DeviceId`, per-call `BatchSize`, `StructureBlock.CellBounds`, `TextGrouping.Line` actually merging.

## Measured parity numbers

Ground truth is Python PaddleOCR 3.7 driving **our own exported ONNX weights** (`engine="onnxruntime"`,
per-module `model_dir`), so a difference is an algorithm difference, not a model difference. Running the
official Paddle weights over the same images produced identical line counts and mean scores, confirming
the ONNX conversion is lossless.

| Measurement | Result |
| --- | --- |
| Character error rate vs Python — like-for-like (41 images: same coordinate frame, same rec pack) | **0.067** macro, **0.022** median |
| Detection F1 @ IoU 0.5, same subset | **0.986** |
| All 61 images, including configuration-confounded rows | 0.182 CER / 0.912 F1 (was **0.776** before this release) |
| Per-script packs vs matching ground truth (Korean, Cyrillic, Arabic) | CER **0.000–0.024** |
| Structure table fidelity (TEDS-proxy) | 0.906–1.000, except one dense borderless table |
| Structure reading order vs Python | block-type sequence match **1.00** on 5 of 6 pages |
| 180°-rotated page (`book_rot180`) | text similarity vs upright reference **0.033 → 0.880** |

The full decomposition — which rows differ for configuration reasons, and which ground-truth files are
themselves corrupted — is in `tools/parity/out/FINAL_REPORT.md`.

### Orientation, and why we deviate

Reaching preprocessing parity on the orientation classifiers exposed a flaw in the upstream default.
PaddleX 3.x acts on the classifier's plain `argmax` with no confidence gate, and both
`PP-LCNet_x1_0_textline_ori` and `PP-LCNet_x1_0_doc_ori` misfire on **upright** text — sometimes
confidently. A wrong verdict hands the recognizer an upside-down crop, so a clean line comes back as
gibberish (`processing` → `ussaooud`). Measured through Python itself on identical weights:

| Page | Classifier off | Classifier on (PaddleX 3.x) | PaddleOcrNet 2.1.0 |
| --- | --- | --- | --- |
| Upright multi-column English | 0.977 | 0.783 — 5 of 17 lines destroyed | **0.978** |
| Devanagari (Hindi) document | 0.970 | 0.827 — body text reduced to noise | **0.968** |
| Page wrongly classified 180° | — | 0.461 — whole page corrupted | **0.945** |

(Mean recognition confidence.) PaddleOcrNet keeps orientation correction on but gates each verdict at
`TextLineOrientationThreshold` (0.9) and then confirms it with `VerifyOrientationByRecognition`:
the crop is recognized both ways and the more confident reading wins, so a wrong verdict costs only the
extra recognition of the flagged lines. A wrong *page* verdict is confirmed the same way, line by line.
Set the threshold to 0 and the verification to false for raw PaddleX 3.x behavior.

Internal 100-image benchmark corpus (`OcrDatasetBenchmarkTests`, 11 categories): all 103 cases pass
against their pinned floors (per-page mean confidence ≥ 0.70, corpus ≥ 0.92, title hit rate ≥ 0.85),
including the 8 dense-mixed/multi-column pages that the orientation guards recovered.

## Upgrade notes — changed defaults

2.1.0 aligns defaults with Python PaddleOCR, which changes out-of-the-box behavior. Everything is
restorable per call/service; the full table with exact one-liners is in `CHANGELOG.md` under
*Changed defaults*. The ones most likely to be noticed:

1. **Throughput on large images.** Detection now runs near native resolution (was: longest side 960).
   More accurate, slower on big scans. Restore: `Detection = new DetectionOptions { LimitSideLen = 960, LimitTypeMax = true }`.
2. **Low-confidence lines are returned.** `DropScore` is now 0 (Python behavior). If you relied on the
   old silent filter, set `DropScore = 0.5` or filter on `Line.Confidence` yourself.
3. **Text-line orientation classification is on**, adding a small per-line cost and fixing upside-down
   lines. Verdicts are gated (`TextLineOrientationThreshold`, default 0.9) and confirmed by recognizing
   both orientations (`VerifyOrientationByRecognition`, default true), which costs one extra recognition
   per flagged line but prevents a misfiring classifier from destroying good text. Turn the stage off
   with `UseTextLineOrientation = false`; set the threshold to 0 and verification to false for raw
   PaddleX 3.x behavior.
4. **Markdown output omits page furniture** (headers, footers, page numbers, footnotes, margin notes)
   and joins pages by paragraph continuation instead of `---`. Restore via `MarkdownRenderOptions`.
5. **`en` routes to the dedicated English pack**, `ru`/`uk`/`be` to the East-Slavic pack, and
   `ja` to the default PP-OCRv5 recognizer. Recognized text may differ (for the better) on those languages.
6. **`UseGpu`/`UsedGpu` are now truthful.** Code that read `UsedGpu == true` as "GPU was requested"
   will now see `false` when CUDA fails to attach — which is the fact that was previously hidden.
7. **GPU consumers should update both packages together** — the Gpu package's buildTransitive targets
   file is what makes the CUDA runtime actually deploy.

No API removals; all 2.0.x code compiles unchanged.
