# Support and validation boundaries

This matrix describes implemented paths and reproducible checks. A configured job is not evidence that it has run successfully. Use the artifacts from the release revision to establish the actual passing platforms, models and fixtures. Skipped tests are recorded separately and never counted as successful coverage.

Completed local release checks are recorded in the [2.4.0 validation report](release-2.4.0-validation.md).

| Capability | Implemented path | Validation mechanism | Boundary |
| --- | --- | --- | --- |
| CPU image OCR | Core package with the CPU ONNX Runtime dependency, decoded images and guarded encoded inputs | Unit CI on Linux/Windows/macOS; explicit model integration workflow | Model-backed coverage depends on supplied assets and enabled tests; platform CI unit success alone does not establish recognition accuracy. |
| DOCX/XLSX/PPTX inputs | Built-in bounded native ZIP/XML readers plus optional embedded-image OCR in the core package | Office parser/export regressions, real receipt image OCR parity, package consumer smoke | No Office rendering or Excel formula calculation. Charts/OLE/notes and other unsupported content have documented limits. No extra Office SDK dependency. |
| Native AOT, linux-x64 | Dedicated `PaddleOcrNet.AotSmoke` executable | Required CI job installs clang/zlib development files, publishes with `PublishAot=true`, then executes the native binary | Checks source-generated document JSON, normalization, structure/orphan serialization; does not claim native OCR/PDF/model execution coverage. Windows/macOS native publishing is not covered by this job. |
| Adaptive recognition | Opt-in source-frame recovery and bounded service-call admission | `DocumentProcessingTests` and model-specific integration checks | Source-pixel/service-call limits are not hard native memory/time bounds or accuracy guarantees. |
| Long lines and batch budgets | Opt-in segmented recognition and normalized-pixel batching | Model-free geometry tests and gated long-line model integration | Representative script/critical-token gates remain separate from synthetic correctness checks. |
| Hybrid PDFs | Full-page visual verification by default; optional uncovered-region recognition | PDFium deterministic tests, real receipt Hybrid and selective-region integration tests | Native glyph validity does not prove native text correctness; selective mode explicitly reports skipped visual verification. Platform-native PDFium must load. |
| Confidence calibration | Externally fitted/versioned/hash-bound curves and offline fit/evaluation tools | Calibration validation, split/leakage tests and manifest evaluator | No bundled curve is asserted to be representative production correctness calibration. |
| CUDA | GPU package/runtime plus driver and matching CUDA/cuDNN dependencies | Explicit self-hosted CUDA parity job and `PaddleOcrNet.GpuParity` tool | Not exercised on ordinary hosted CPU runners. Missing CUDA, CPU fallback, missing fixtures and parity failures fail the tool. A job left disabled supplies no GPU evidence. |
| TensorRT, DirectML, CoreML | Provider selection paths already exposed by the library | Existing resolver/configuration tests | This release workflow adds no real-device parity gate for these providers. Do not infer support validation from enum presence or CPU fallback. |
| FP16/INT8, learned restoration, specialist training | Experimental/research work requiring models and labeled evaluation | No new claimed release gate here | Not established by provider parity or smaller tensors. |

## Bounded integration runs

From a PowerShell 7 shell at the repository root:

```powershell
./tools/validation/Run-IntegrationGroups.ps1 -ClassesPerGroup 4 -ProcessorCount 4 -TimeoutSeconds 1200
```

The runner builds Release once, discovers tests, sorts distinct test classes, and executes nonoverlapping groups serially in separate `dotnet test` processes. It enables `PADDLEOCRNET_RUN_INTEGRATION=1`; other data/measurement gates remain explicit and their skips appear in TRX. The default includes the entire discovered suite. Use `-Filter 'Category=Integration'` for a deliberate subset, or `-NoBuild` only with an already-current Release build. Each group has a process timeout; timeout kills its process tree and marks the group failed. The runner continues other groups so one failing case does not hide later results, then exits unsuccessfully if any group fails. Empty discovery, invalid/missing TRX and no matched tests cannot produce a passing run.

`-ProcessorCount` defaults to 4 and sets `DOTNET_PROCESSOR_COUNT` for every child `dotnet` process and its descendants, including build, discovery and testhosts. The explicit parameter overrides any inherited value for those children; it does not change the calling shell's `DOTNET_PROCESSOR_COUNT`. `summary.json` records the requested count and environment value so runs can be reproduced. Lower it on constrained/shared hosts. This limits the processor count visible to managed runtime scheduling and library defaults; it is not a hard CPU-affinity limit, ONNX native-thread limit or process-memory cap. Run memory-heavy AOT publishing and model integration sequentially when they share a host.

Each invocation creates a unique directory under `artifacts/integration-groups` containing discovery/build/console logs, per-group TRX, runtime information, and `summary.json` with exact filters, class names, elapsed times, exit codes and passed/failed/skipped counters. Four classes is a scheduling bound, not a guarantee of equal runtime or memory use; lower it for specialist workloads. Native model calls may require killing the test process to enforce the outer timeout.

`.github/workflows/release-validation.yml` exposes this as a manual release workflow because models may download and the full integration suite is expensive. The ordinary CI AOT job remains independent and blocking; it no longer publishes the demo as an informational-only check.

## CUDA parity

On a provisioned CUDA host:

```text
dotnet run --project test/PaddleOcrNet.GpuParity/PaddleOcrNet.GpuParity.csproj -c Release -- benchmarks/gpu-parity/manifest.json artifacts/gpu-parity
```

The required manifest specifies image paths relative to itself, one explicit language, full-transcription Unicode-scalar CER tolerance, maximum source-coordinate geometry displacement and maximum raw-confidence difference. The checked-in manifest demands exact text, at most one pixel geometry displacement and at most 0.02 raw-confidence difference on the bundled receipt. These are explicit smoke tolerances, not validated accuracy release thresholds. Use a reviewed representative fixture manifest for a candidate release; do not relax tolerances merely to turn a failure green.

CPU and CUDA run the same model registry, cache, explicit language, one-thread/one-item recognition settings, word-box option and disabled page preprocessing/orientation. The tool requires the runtime to advertise CUDA and the actual CUDA result to report `ExecutionProvider=Cuda` and `UsedGpu=true`; the CPU result must report CPU. Both outputs must contain text, so matching empty output is not a pass. It compares all `FullText`, corresponding line polygons and word boxes, line/word counts and raw confidence. Different line counts or polygon topology fail geometry parity rather than being silently realigned. This is numerical/provider parity against CPU, not comparison with annotated ground truth or a speed benchmark.

`parity.json` retains actual providers, full transcriptions, fixture hashes, cache asset hashes, runtime/OS, options, tolerances and per-fixture outcomes. The manual workflow also stores `nvidia-smi` and console output. Its optional `run_cuda` input requires a runner labeled `self-hosted`, `linux`, `x64`, `cuda`; a missing runner leaves the job waiting and cannot establish GPU validation. The tool is deliberately separate from default tests so a normal CPU environment neither downloads the large GPU runtime unnecessarily nor reports a misleading skipped GPU test as a pass.
