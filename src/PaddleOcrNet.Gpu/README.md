# PaddleOcrNet.Gpu

CUDA GPU acceleration for [**PaddleOcrNet**](https://www.nuget.org/packages/PaddleOcrNet) — high-accuracy
native .NET OCR powered by PaddleOCR's neural models on ONNX Runtime.

This is a **metapackage**. It contains no code of its own: it pulls in `PaddleOcrNet` together with
`Microsoft.ML.OnnxRuntime.Gpu`, so the CUDA execution provider is present at runtime and PaddleOcrNet
can select it.

```bash
dotnet add package PaddleOcrNet.Gpu
```

Installing this package *instead of* `PaddleOcrNet` is all that is required — it brings the core library
with it.

## Usage

There is no separate GPU API. Existing code is unchanged; PaddleOcrNet detects the CUDA provider and
uses it automatically:

```csharp
using PaddleOcrNet.Models;
using PaddleOcrNet.Services;

await using var ocr = new PaddleOcrService();

OcrResult result = await ocr.ExtractTextFromImage("invoice.png", OcrLanguage.English);

Console.WriteLine(result.UsedGpu);   // true when a GPU provider was selected
Console.WriteLine(result.FullText);
```

`ExecutionProvider` defaults to `OcrExecutionProvider.Auto`, which probes the loaded ONNX Runtime and
picks the best accelerator available, falling back to CPU when none is:

| Platform | Auto-detect order |
|---|---|
| Windows | DirectML → CUDA → CPU |
| Linux | CUDA → CPU |
| macOS | CoreML → CPU |

To pin a provider rather than auto-detecting:

```csharp
await using var ocr = new PaddleOcrService(new PaddleOcrServiceOptions
{
    ExecutionProvider = OcrExecutionProvider.Cuda,   // or Cpu / DirectMl / CoreMl / Auto
});
```

`PaddleOcrServiceOptions.UseGpu = true` is kept as shorthand for forcing CUDA, but prefer leaving
`ExecutionProvider` at `Auto` — it already enables a GPU when one is present.

## Requirements

- An NVIDIA GPU with the proprietary driver installed, and **CUDA 12+**.
- **Windows or Linux.** `Microsoft.ML.OnnxRuntime.Gpu` ships native assets for those two platforms only.
  On macOS this package has nothing to contribute; install plain `PaddleOcrNet` and let `Auto` select
  CoreML.

If CUDA is unavailable at runtime — no driver, missing libraries, no compatible device — provider
selection logs the reason and **falls back to CPU** rather than throwing. `OcrResult.UsedGpu` reports
what was actually used, so a silent fallback is still observable.

## Notes

- Install either `PaddleOcrNet` **or** `PaddleOcrNet.Gpu`, not both — this package already references the
  core library and pins it to a matching version.
- The GPU packages add a substantial native payload. For CPU-only deployments, and for Native AOT,
  trimmed or container images where size matters, use plain `PaddleOcrNet`.
- OCR models are downloaded and cached on first use exactly as with the CPU package; GPU selection does
  not change model sourcing.

## Links

- **Source, documentation and issues:** https://github.com/FarhanLodi/PaddleOcrNet
- **Core package:** https://www.nuget.org/packages/PaddleOcrNet

## License

MIT © PaddleOcrNet contributors. Downloaded models are Apache-2.0 / MIT and attributed to their authors.
