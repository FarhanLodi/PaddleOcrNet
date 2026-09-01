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

- An NVIDIA GPU with the proprietary driver installed, and the **CUDA 13.x** toolkit with **cuDNN 9**
  (CUDA 12 is supported by pinning ONNX Runtime yourself — see below).
- **Windows or Linux.** `Microsoft.ML.OnnxRuntime.Gpu` ships native assets for those two platforms only.
  On macOS this package has nothing to contribute; install plain `PaddleOcrNet` and let `Auto` select
  CoreML.

### Running on CUDA 12

This package brings in **ONNX Runtime 1.27**, whose GPU build targets **CUDA 13** — as does every ONNX
Runtime release after it:

| ONNX Runtime | CUDA | cuDNN |
|---|---|---|
| 1.27.x and later | 13.0 | 9.x |
| 1.21.x – 1.26.x | 12.8 | 9.x |

On a machine with only the **CUDA 12** toolkit, that runtime looks for `cublasLt64_13.dll`
(`libcublasLt.so.13`), fails to attach the CUDA provider, and PaddleOcrNet falls back to CPU. The warning it
logs names the missing library and the CUDA major version the runtime wanted, so the mismatch is visible
without decoding `Error 126`.

There are three ways to get a GPU out of a CUDA 12 machine. Installing PaddleOcrNet 2.0.2 or older does not
help — every release so far has referenced ONNX Runtime 1.27.

**1. Install the CUDA 13 runtime next to CUDA 12.** The two majors coexist: their libraries are suffixed
(`cublasLt64_12.dll` vs `cublasLt64_13.dll`), so adding CUDA 13 leaves existing CUDA 12 workloads alone. This
keeps you on the current ONNX Runtime and needs no project changes.

**2. Pin ONNX Runtime 1.26 in your own project.** Pin **both** packages — they ship the same managed assembly
and must not split — and suppress `NU1605`, which NuGet raises because a direct reference below a transitive
one counts as a downgrade:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);NU1605</NoWarn>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.ML.OnnxRuntime.Gpu" Version="1.26.0" />
  <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.26.0" />
</ItemGroup>
```

A direct reference always wins over the transitive one, so this is all that is needed — nothing about
PaddleOcrNet itself changes, and `ExecutionProvider` still resolves CUDA automatically.

**3. Use DirectML instead of CUDA (Windows only).** DirectML runs on any DirectX 12 GPU — NVIDIA included —
with no CUDA toolkit at all. Install `Microsoft.ML.OnnxRuntime.DirectML` instead of this package;
`ExecutionProvider.Auto` already prefers DirectML over CUDA on Windows, so no code changes are needed:

```bash
dotnet add package Microsoft.ML.OnnxRuntime.DirectML
```

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
