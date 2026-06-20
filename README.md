<p align="center">
  <img src="icon.png" alt="PaddleOcrNet" width="140" height="140" />
</p>

<h1 align="center">PaddleOcrNet</h1>

<p align="center">
  <strong>Native .NET document OCR — powered by PaddleOCR's PP-OCRv5 models, running on ONNX Runtime.</strong><br/>
  No Python. No native PaddlePaddle. No sidecar server. Just a NuGet package.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/PaddleOcrNet"><img src="https://img.shields.io/nuget/v/PaddleOcrNet.svg?label=NuGet&color=004880" alt="NuGet"/></a>
  <a href="https://www.nuget.org/packages/PaddleOcrNet"><img src="https://img.shields.io/nuget/dt/PaddleOcrNet.svg?label=Downloads&color=004880" alt="Downloads"/></a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/AOT-ready-2ea44f" alt="AOT ready"/>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License: MIT"/></a>
</p>

---

PaddleOcrNet turns scanned documents, photos, and PDFs into structured text. It runs the full
**PP-OCRv5 + PP-StructureV3** pipeline — text detection, recognition, orientation correction, layout
analysis, table extraction, and formula recognition — entirely in managed .NET on
[ONNX Runtime](https://onnxruntime.ai/). Models download and cache on first use; everything after that
runs in-process, offline-capable, and trim/AOT-friendly.

## Highlights

- **High-accuracy text OCR** — DB detection + SVTR recognition (PP-OCRv5) handles dense invoices, forms,
  receipts, handwriting, rotated scans, and curved text.
- **80+ languages** across 12 script families, with one shared detector and per-script recognizer packs.
- **Automatic language detection** — pass `"auto"` and PaddleOcrNet identifies the script and pulls the
  right model on demand (Python PaddleOCR requires you to name the language up front).
- **Document understanding** — `AnalyzeDocumentAsync` returns layout regions, reading order, tables as
  HTML, and formulas as LaTeX, and serializes the whole document to **Markdown or JSON**.
- **PDF in, searchable PDF out** — rasterize and OCR PDFs, or emit a searchable PDF with an invisible
  text layer.
- **Robust by design** — singleton-safe, thread-safe ONNX sessions; DI + health checks; OpenTelemetry
  metrics; typed exceptions; input/decompression-bomb guards; checksum-verified model downloads.
- **Deploys anywhere** — pure-managed (no OpenCV), CPU by default, optional CUDA, **Native AOT** and
  single-file publish supported. Mobile models are a few MB each.

---

## Installation

```bash
dotnet add package PaddleOcrNet

# Optional — NVIDIA CUDA 12+ acceleration (used automatically when present):
dotnet add package PaddleOcrNet.Gpu
```

Requires **.NET 10** (`net10.0`). Windows, Linux, and macOS (x64/arm64).

---

## Quick start

```csharp
using PaddleOcrNet.Services;

// ONNX models download + cache on first use; construction itself loads nothing.
await using var ocr = new PaddleOcrService();

OcrResult result = await ocr.ExtractTextFromImage("invoice.png", new[] { "en" });

Console.WriteLine(result.FullText);
foreach (var line in result.Lines)
    Console.WriteLine($"[{line.Confidence:F2}] {line.Text}");
```

Input can be a file path, `byte[]`, `Stream`, or an already-decoded `Image<Rgb24>`:

```csharp
await ocr.ExtractTextFromImage(bytes,  new[] { "en" });
await ocr.ExtractTextFromImage(stream, new[] { "en", "de" });

// Detect-only (bounding boxes for redaction / cropping — no recognition):
var regions = await ocr.DetectRegionsAsync("page.png");

// Recognize caller-supplied regions (skip detection):
var partial = await ocr.RecognizeRegionsAsync(image, regions, new[] { "en" });
```

### Automatic language detection

```csharp
// "auto" → PaddleOcrNet detects the dominant script, downloads the matching pack, and reports it.
OcrResult r = await ocr.ExtractTextFromImage("multilingual.png", new[] { "auto" });

Console.WriteLine(string.Join(", ", r.DetectedLanguages)); // e.g. "arabic, latin, ch"
```

---

## Document structure analysis

`AnalyzeDocumentAsync` runs the PP-StructureV3 pipeline — orientation → layout detection → per-region
OCR / table / formula → reading-order reconstruction — and returns a structured document you can export
straight to Markdown or JSON.

```csharp
using PaddleOcrNet.Services;
using PaddleOcrNet.Structure;

await using var ocr = new PaddleOcrService();

StructureResult doc = await ocr.AnalyzeDocumentAsync("report.png", new StructureOptions
{
    Languages         = new[] { "en" },
    UseDocOrientation = true,    // auto-rotate skewed scans (0/90/180/270°)
    RecognizeTables   = true,    // tables → HTML
    RecognizeFormulas = true,    // formulas → LaTeX
});

foreach (var block in doc.Blocks)
    Console.WriteLine($"#{block.Order} {block.Type} — {block.Text}");

string markdown = doc.ToMarkdown();  // titles, paragraphs, tables (HTML), formulas ($$…$$)
string json     = doc.ToJson();      // structured blocks with bounding boxes + reading order
```

| Stage | Model | Output |
| --- | --- | --- |
| Layout analysis | PP-DocLayoutV3 (RT-DETR) | region boxes + 25 block types |
| Table recognition | SLANet_plus | `<table>` HTML with cell text matched into the grid |
| Formula recognition | LaTeX-OCR | LaTeX string |
| Orientation / unwarp | PP-LCNet · UVDoc | de-skewed, de-warped page |
| Reading order | XY-cut | multi-column document order |

---

## Supported languages

A single **DB detector** serves every language; recognition selects a per-script **recognizer pack**
(PP-OCRv5 mobile + the matching character dictionary). Pass any representative code:

| Pack | Codes |
| --- | --- |
| Chinese / English / Japanese (default) | `ch` `zh` `en` `ja` |
| Latin | `latin` `fr` `de` `es` `it` `pt` `nl` `pl` `tr` `vi` … |
| Cyrillic | `cyrillic` `ru` `uk` `bg` `sr` `be` `mn` … |
| Arabic | `arabic` `ar` `fa` `ur` `ug` |
| Devanagari | `devanagari` `hi` `mr` `ne` `sa` … |
| Korean | `korean` `ko` |
| Japanese (full) | `japan` |
| Thai · Greek · Telugu · Tamil | `thai`/`th` · `greek`/`el` · `telugu`/`te` · `tamil`/`ta` |
| Traditional Chinese | `chinese_cht` `cht` `zh_tra` |
| East-Slavic | `eslav` `ru_eslav` `uk_eslav` `be_eslav` |

Or pass `"auto"` to detect the script automatically. Unknown codes are skipped with a warning.

---

## ASP.NET Core / dependency injection

```csharp
builder.Services.AddPaddleOcrNet(o =>
{
    o.UseTextLineOrientation = true;       // correct 180°-flipped lines
    o.ModelCachePath         = "/var/cache/ocr";
});

// Readiness probe — Healthy once models for these languages are cached:
builder.Services.AddHealthChecks()
    .AddPaddleOcrHealthCheck(languages: new[] { "en", "ch" });
```

`IPaddleOcrService` is registered as a **singleton** — ONNX sessions are expensive to build and safe to
share across threads. Call `WarmUp(...)` to pre-load models off the request path.

---

## Configuration

| Concern | How |
| --- | --- |
| **GPU** | Add `PaddleOcrNet.Gpu`; CUDA 12+ is detected and used automatically, otherwise CPU. |
| **Model cache** | `%LOCALAPPDATA%` / `~/.local/share` by default; override via `ModelCachePath` or `PADDLEOCRNET_CACHE`. |
| **Model host** | Defaults to the public Hugging Face repo; point at a private mirror via `PADDLEOCRNET_MODEL_BASE_URL` or `ModelDownloadOptions.BaseUrlOverride`. |
| **Offline / air-gapped** | Pre-seed the cache (or a mirror) and run fully offline; downloads are SHA-256 verified. |
| **Throughput** | `BatchSize`, `MaxDegreeOfParallelism`, and reading-order / paragraph grouping via `RecognitionOptions`. |
| **Input limits** | Built-in max-pixel / PDF page guards against decompression bombs. |

### Output formats

`OcrResult` exports to plain text, **JSON**, **hOCR**, **ALTO XML**, and TSV; documents export to
**Markdown** and **JSON**; PDFs can be re-emitted as **searchable PDFs**. All exporters are AOT-safe via a
source-generated JSON context.

---

## Models & licensing

PaddleOcrNet ships **no weights** — on first use it downloads PP-OCRv5 / PP-StructureV3 ONNX models and
their dictionaries (SHA-256 verified) to the local cache. The models are derived from
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (**Apache-2.0**, © PaddlePaddle/Baidu); the formula
model is [RapidLaTeXOCR](https://github.com/RapidAI/RapidLaTeXOCR) (**MIT**). See [NOTICE](NOTICE) for
attribution. The library itself is **MIT** — see [LICENSE](LICENSE).

> **Note on formula recognition:** PaddleOCR's PP-FormulaNet cannot be exported to ONNX, so PaddleOcrNet
> uses the equivalent LaTeX-OCR model for formula → LaTeX.

---

## Roadmap

Already shipped: detection, recognition (multilingual + auto-detect), orientation, unwarp, layout, tables,
formulas, reading order, Markdown/JSON export, and the PDF pipeline. Under consideration:

- Seal text recognition (pending a hostable ONNX model)
- Key-information extraction (SER/RE)
- DOCX / XLSX document export
- Additional per-language recognizer packs

---

## Why PaddleOcrNet?

- **vs. Python PaddleOCR** — same models and accuracy, but no Python runtime, no `paddlepaddle` native
  dependency, and no server process. Ships as a single NuGet package with first-class .NET ergonomics
  (DI, health checks, AOT) and adds automatic language detection.
- **vs. cloud OCR APIs** — runs entirely in-process and offline; no per-page fees, no data leaving your
  infrastructure.
- **vs. EasyOCR-based libraries** — PP-OCRv5 is materially stronger on dense documents, tables, rotated
  scans, handwriting, and CJK, and adds full document-structure understanding.

---

## License

MIT © PaddleOcrNet contributors. Downloaded models are Apache-2.0 / MIT and attributed to their authors
(see [NOTICE](NOTICE)).
