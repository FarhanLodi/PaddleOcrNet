<p align="center">
  <img src="icon.png" alt="PaddleOcrNet Logo" width="160" height="160" />
</p>

# PaddleOcrNet

High-accuracy, **native .NET OCR** powered by [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)'s
PP-OCRv5 neural models, running on [ONNX Runtime](https://onnxruntime.ai/). No Python, no native
PaddlePaddle, no server process — just a NuGet package and ONNX models downloaded on first use.

> **Status:** core OCR engine (DB detection + text-line orientation + SVTR/CTC recognition + the
> multilingual model registry + the det → cls → rec pipeline) is implemented and unit-tested.
> The **document-structure subsystem** (layout analysis, table recognition, formula recognition, seal
> recognition, doc pre-processing, XY-cut reading order, and Markdown/JSON export — surfaced via
> `AnalyzeDocumentAsync`) is now **implemented and unit-tested** in-code; it still needs the maintainer
> to export and upload its ONNX models to validate end-to-end (document unwarp remains a safe
> pass-through stub).

---

## Why PaddleOcrNet (PaddleOCR vs EasyOCR)

EasyOCR and PaddleOCR are both excellent CRAFT/DB-style detector + recognizer stacks, but PaddleOCR's
PP-OCRv5 line is the stronger base for **document** OCR, which is what this library targets:

- **Dense / small-text documents.** PP-OCRv5's detector + SVTR recognizer hold up better on
  tightly-packed invoices, forms, receipts and scanned pages where EasyOCR tends to merge or drop
  lines.
- **Document structure (the roadmap).** PaddleOCR ships first-class **layout analysis**, **table
  structure recognition** (SLANet / SLANeXt → HTML), and **formula recognition** (LaTeX-OCR →
  LaTeX) models. EasyOCR has no equivalent. PaddleOcrNet's export toolchain produces these
  ONNX assets (formula via the MIT [RapidLaTeXOCR](https://github.com/RapidAI/RapidLaTeXOCR) ONNX,
  since PP-FormulaNet is not ONNX-exportable), and the .NET runtime side is **implemented** — see
  `AnalyzeDocumentAsync` and the structure section below.
- **Orientation handling.** Built-in **text-line orientation** (0°/180° per line) and
  **document orientation** (0/90/180/270°) classifiers correct rotated scans before recognition.
- **Smaller footprint.** The PP-OCRv5 *mobile* detector + recognizer are a few MB each, versus
  EasyOCR's larger default models — better for desktop apps, containers and edge deployment.
- **Broad multilingual coverage** from per-script recognizer packs sharing one detector.

If you only need a handful of Latin lines from clean photos, EasyOCR is fine. If you need **dense
documents, mixed scripts, rotated scans, and a path to tables/formulas/layout**, PaddleOcrNet is built
for that.

---

## Quick start

```bash
dotnet add package PaddleOcrNet
# Optional, NVIDIA CUDA 12+ acceleration (used automatically when present):
dotnet add package PaddleOcrNet.Gpu
```

```csharp
using PaddleOcrNet.Services;

// Models (PP-OCRv5 ONNX) download and cache on first use; nothing happens at construction.
await using var ocr = new PaddleOcrService();

OcrResult result = await ocr.ExtractTextFromImage("invoice.png", new[] { "en" });

Console.WriteLine(result.FullText);
foreach (var line in result.Lines)
    Console.WriteLine($"[{line.Confidence:F2}] {line.Text}");
```

### Dependency injection (ASP.NET Core / Generic Host)

```csharp
using PaddleOcrNet.Services;

builder.Services.AddPaddleOcrNet(o =>
{
    o.UseTextLineOrientation = true;        // correct 180°-flipped lines
    o.ModelCachePath = "/var/cache/ocr";    // optional shared cache
});

// Optional readiness probe: Healthy once the models for these languages are cached.
builder.Services.AddHealthChecks()
    .AddPaddleOcrHealthCheck(languages: new[] { "en", "ch" });
```

Inject `IPaddleOcrService` anywhere. The service is registered as a **singleton** (ONNX sessions are
expensive to build and thread-safe to reuse). Call `WarmUp(...)` to pre-load models off the hot path.

### Other entry points

```csharp
// Bytes / streams / already-decoded ImageSharp images:
await ocr.ExtractTextFromImage(byteArray, new[] { "en" });
await ocr.ExtractTextFromImage(stream,    new[] { "en", "de" });

// Detect-only (layout boxes, redaction, field cropping — no recognition):
var regions = await ocr.DetectRegionsAsync("page.png");

// Recognize caller-supplied region polygons (skip detection):
var partial = await ocr.RecognizeRegionsAsync(image, regions, new[] { "en" });
```

Run the bundled demo:

```bash
dotnet run --project test/PaddleOcrNet.Demo -- invoice.png en
```

---

## Supported languages

One shared **DB detector** serves every language; recognition uses a per-script **recognizer pack**
(PP-OCRv5 mobile rec + the matching `ppocr` character dictionary). Pass any of these language codes:

| Pack | Codes (representative) |
| --- | --- |
| Chinese / English / Japanese (default) | `ch` `zh` `en` `ja` |
| Latin | `latin` `fr` `de` `es` `it` `pt` `nl` `pl` `tr` `vi` … |
| Cyrillic | `cyrillic` `ru` `uk` `bg` `sr` `be` `mn` … |
| Arabic | `arabic` `ar` `fa` `ur` `ug` |
| Devanagari | `devanagari` `hi` `mr` `ne` `sa` … |
| Korean | `korean` `ko` |
| Japanese (full) | `japan` |
| Thai | `thai` `th` |
| Greek | `greek` `el` |
| Telugu | `telugu` `te` |
| Tamil | `tamil` `ta` |
| Traditional Chinese | `chinese_cht` `cht` `zh_tra` |
| East-Slavic | `eslav` `ru_eslav` `uk_eslav` `be_eslav` |

Unknown codes are skipped with a warning. See
[`PaddleModelRegistry`](src/PaddleOcrNet/Internal/PaddleModelRegistry.cs) for the full code lists.

---

## Models & the model host (important)

PaddleOcrNet ships **no model weights**. On first use it downloads the PP-OCRv5 ONNX models and their
character dictionaries to a local cache (`%LOCALAPPDATA%`/`~/.local/share`, or the
`PADDLEOCRNET_CACHE` env var / `ModelCachePath` option).

- **The default model host is a placeholder.** `PaddleModelRegistry.DefaultBaseUrl` currently points at
  a placeholder Hugging Face repo
  (`https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models/resolve/main`). **The assets are not
  uploaded yet.**
- **Override the host** at runtime with the **`PADDLEOCRNET_MODEL_BASE_URL`** environment variable, or
  per-service via `ModelDownloadOptions.BaseUrlOverride`. Point it at the real model repo or a private
  mirror; the per-file URL is `{baseUrl}/{fileName}`.
- **Checksums are not published yet.** Until the SHA256 table in the registry is filled in, downloads
  are only accepted when `ModelDownloadOptions.AllowUnverifiedModels` is set (fail-closed by default).
  Once the maintainer uploads the models and pastes the generated checksums, verification is enforced
  automatically with no code change.
- **Exporting the models yourself.** The maintainer-only [`tools/`](tools/) toolchain converts every
  PaddleOCR / PaddleX model to ONNX, computes checksums, and uploads them to Hugging Face. See
  [`tools/README.md`](tools/README.md).

```bash
# Point the library at your own model host:
export PADDLEOCRNET_MODEL_BASE_URL="https://your-host.example/paddleocrnet-models"
```

---

## Requirements

- **.NET 10** (`net10.0`).
- CPU works everywhere. For **NVIDIA CUDA 12+** acceleration, add the `PaddleOcrNet.Gpu` package — it
  is detected and used automatically; otherwise OCR runs on CPU and a one-line hint names the package
  to install.
- Trimming / **Native AOT** friendly (`IsAotCompatible`; source-generated JSON via
  `PaddleOcrJsonContext`).

---

## Status & roadmap

**Works end-to-end (pending the model upload to validate):**

- DB text **detection** with full DB post-processing (unclip, min-area boxes, NMS).
- Text-line **orientation** classification (180° flip).
- SVTR **recognition** + CTC greedy decoding + `ppocr` character dictionaries.
- Multilingual **model registry**, lazy per-language session loading, reading-order sorting,
  paragraph grouping.
- `PaddleOcrService` / `IPaddleOcrService`, DI + health check, export (text/JSON/hOCR/ALTO),
  PDF input/searchable-PDF output plumbing.

**Document-structure subsystem — implemented (`AnalyzeDocumentAsync`):**

- **Layout** analysis (PP-DocLayout, PicoDet-S/M and the RT-DETR `plus-L` variant) mapped onto a shared
  block-type vocabulary.
- **Table** recognition (**SLANet** structure-token decode → HTML, OCR cell-text matched into the
  predicted grid).
- **Formula** recognition (**LaTeX-OCR**: image-resize + split encoder/decoder transformer + the
  autoregressive LaTeX decode loop → LaTeX). This is the MIT
  [RapidLaTeXOCR](https://github.com/RapidAI/RapidLaTeXOCR) ONNX — **not** PaddleOCR's PP-FormulaNet,
  which **cannot** be exported to ONNX.
- **Seal** recognition (PP-OCRv4 seal detector + the shared text recognizer).
- **Document pre-processing** — orientation classify/rotate (0/90/180/270°) is implemented; **UVDoc
  unwarp is a safe pass-through stub** (held/disposed session, no-op remap until its grid I/O contract
  is verified against the real export — see `DocPreprocessor.Unwarp`).
- **Reading order** via **XY-cut** (`XyCutOrderer`) and **Markdown / JSON export**
  (`StructureResult.ToMarkdown()` / `.ToJson()`, the latter AOT-safe via a source-generated context).
- The orchestrator (`PaddleStructureEngine`) lazily loads each model once and reuses the sessions,
  mirroring the core engine's session-cache/dispose patterns.

```csharp
await using var ocr = new PaddleOcrService();

StructureResult doc = await ocr.AnalyzeDocumentAsync("page.png", new StructureOptions
{
    LayoutModel       = LayoutModel.PicoDetS,
    UseDocOrientation = true,
    Languages         = new[] { "en" },
});

string markdown = doc.ToMarkdown();   // headings, paragraphs, tables (HTML), formulas ($$...$$)
string json     = doc.ToJson();
```

**Needs the maintainer's exported + uploaded ONNX models to validate end-to-end:** because no weights
are published yet, both the core OCR engine and the structure subsystem are verified by
**pure-function unit tests** (geometry, CTC decode, dictionary parsing, registry, reading order, layout
output parsing, SLANet structure decode, LaTeX-OCR detokenize, table cell-text matching, XY-cut,
Markdown export) rather than full image-to-text runs. Point `PADDLEOCRNET_MODEL_BASE_URL` at a host with
the PP-OCRv5 / structure ONNX assets to exercise real OCR + structure analysis.

---

## License

MIT (this library) — see [LICENSE](LICENSE). The PaddleOCR models it downloads are Apache-2.0 and
attributed to PaddlePaddle / Baidu; see [NOTICE](NOTICE).
