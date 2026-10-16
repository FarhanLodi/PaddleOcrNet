# EasyImageSharp 1.2.0 source review

Reviewed 2026-10-14 directly from `D:/Projects/EasyImageSharp/src`, including implementation files and package projects. PaddleOcrNet now references the published `EasyImageSharp` 1.2.0 package; restore succeeds without a local project reference or private feed.

## Features verified in source

| Area | Source in the EasyImageSharp repository | Behavior and relevance |
| --- | --- | --- |
| Tensor output | `EasyImageSharp/Tensors/ImageTensorExtensions.cs`, `TensorWriteOptions.cs`, `ResizeTensorExtensions.cs` | `WriteTensor` supports caller-owned CHW/HWC buffers, RGB/BGR/grayscale, row/plane strides, batch offsets and normalization. `ResizeToTensor` fuses resizing and packing and returns coordinate mapping. Existing PaddleOcrNet lookup-table packers preserve exact arithmetic and source-window handling; replacement needs parity and throughput measurements. |
| Decode execution | `EasyImageSharp/Formats/DecoderOptions.cs`, `ImageExecutionOptions.cs` | Encoded-byte limits, custom format providers and request-local cancellation/parallelism/pixel-buffer allocation policies. Allocation budgets count cumulative library pixel-buffer allocations, not all managed/native memory. |
| Large inputs | `EasyImageSharp/Formats/Jpeg/JpegDecoder.cs`, `Formats/Tiff/TiffDecoder.Pages.cs`, `TiffDecoder.Regions.cs` | Reduced JPEG IDCT supports denominators 1, 2, 4 and 8. TIFF adds lazy page decoding and seekable region access for supported layouts. These require explicit input-adapter decisions; a version upgrade alone does not make PaddleOcrNet's buffered frame API incremental. |
| Adaptive document preprocessing | `EasyImageSharp/Processing/Document/DocumentPreprocessor.cs`, `DocumentPreprocessingTypes.cs`, `DocumentCoordinateMap.cs` | NeuralOcr, ScannedDocument, PhoneDocument and Screenshot profiles, sampled analysis, stage decisions/timings and source/output mappings. These remain opt-in upstream; no automatic profile is enabled in PaddleOcrNet because transforms can change OCR evidence and geometry. |
| Optional AI | `EasyImageSharp.AI/Operations/DocumentAiPreprocessor.cs`, `DocumentDewarpRunner.cs`, `DocumentAiOptions.cs` | Async document preprocessing, explicit image/grid dewarp contracts and mapping availability. Requested model errors propagate. The package references ONNX Runtime 1.29.0; PaddleOcrNet already owns model/provider behavior with ONNX Runtime 1.31.0. Adding another model pipeline requires separate calibration and ownership decisions. |
| Optional SkiaSharp | `EasyImageSharp.SkiaSharp/SkiaImageExtensions.cs`, `EasyImageSharp.SkiaSharp.csproj` | Copied native bitmap interop, drawing, shaped text runs and explicit RGB color-space conversion. References SkiaSharp and SkiaSharp.HarfBuzz 4.153.1. It is not needed for OCR decoding or the current exporters. |
| Optional LittleCMS | `EasyImageSharp.LittleCms/IccTransform.cs`, `IccImageExtensions.cs`, `EasyImageSharp.LittleCms.csproj` | ICC transforms including RGB/CMYK/Gray/Lab/XYZ with native LittleCMS; rendering intents and black-point compensation are configurable. The project packages its prepared win-x64 native runtime. Other platform deployment needs explicit consideration. No current PaddleOcrNet API requests ICC conversion. |

All four package projects declare version 1.2.0 and target .NET 8 and .NET 10. Only the core package is required here; optional packages were reviewed but not added as unused dependencies. Core remains the managed image dependency.

## Integration changes

- Upgrade the existing core package reference from 1.1.0 to 1.2.0.
- Pass the service request cancellation token into `DecoderOptions.ExecutionOptions` for both single-image and all-frame decoding. Header inspection also receives the decoder options, and pre-canceled requests are rejected even when the pixel guard and transparency flattening are disabled.
- Propagate cancellation through path, stream, byte-memory and frame entry points, including the no-copy `MemoryStream` path. Cancellation stays request-local and is not translated into `ImageTooLargeException`.
- Preserve existing limits, EXIF orientation, transparency, interpolation and tensor arithmetic. Cancellation of decoding is cooperative at EasyImageSharp checkpoints; this change does not add cancellation checkpoints to PaddleOcrNet's separate transparency flattening loop.

## Validation

`dotnet restore PaddleOcrNet.sln` resolved the published package successfully. `dotnet build PaddleOcrNet.sln --no-restore` passed with zero warnings and errors after the dependency and cancellation integration. Image loading tests cover cancellation-token identity and isolation in addition to existing orientation, alpha and frame guards. The targeted run passed 46/46 tests: `ServiceImageLoadingTests`, `ServiceFramesTests`, `PerspectiveWarpTests`, `TextLineClassifierPreprocessTests`, `StructureDocPreprocessorOwnershipTests`, `StageDiagnosticsTests` and `ServiceBatchTests`.

This review establishes available APIs and compatibility, not a measured accuracy or speed improvement. Optional preprocessing, fused tensor packing and reduced-resolution decode should be evaluated against labeled OCR output before enabling behavior changes.
