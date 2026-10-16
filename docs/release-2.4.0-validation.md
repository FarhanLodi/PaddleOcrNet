# Release 2.4.0 validation

Recorded 2026-10-15. This report distinguishes completed checks from unavailable validation. Artifact paths are local generated evidence, not committed release assets.

These checks cover the uncommitted 2.4.0 working tree based on `14fc1027d1be6c18eed6a38cf65e2b61e4a3045b`, not that base commit alone. `artifacts/release-2.4.0/validation-provenance.json` records package and assembly SHA-256 hashes; the core DLL extracted from the package matches the DLL used by the test suite. Packages have been prepared locally, not published.

## Office input addition: latest checkpoint

DOCX/XLSX/PPTX native input and embedded-image OCR were added after the broad OCR run below. These are the post-addition checks, rather than a claim that the older broad run exercised Office inputs:

| Check | Result and evidence |
| --- | --- |
| Release solution build | **0 warnings, 0 errors**, `artifacts/test-results/office-final-build.log`. |
| Full default suite | **1,021 passed, 275 gated skips, 0 failed**, `artifacts/test-results/office-default.trx`. |
| Office/export selection, models enabled | **54 passed, 0 skipped, 0 failed**, `artifacts/test-results/office-final-focused.trx`. Includes all three input formats, public Word/Excel export round trips, table text, relationship/namespace validation, parsing limits, cancellation and real embedded receipt OCR parity against direct CPU OCR. |
| Linux Native AOT | Published with warnings treated as errors, then executed successfully: `artifacts/test-results/office-linux-aot.log`. Smoke covers native DOCX/XLSX extraction, Office JSON and the prior normalization/orphan contracts. It does not execute Office embedded-image OCR or PPTX reading under AOT. |
| Packages and fresh consumer | Both local 2.4.0 packages rebuilt; core DLL matches the tested DLL. A fresh consumer restored the local core package and passed DOCX/XLSX extraction and JSON smoke: `artifacts/test-results/office-consumer.log`. No Office SDK or additional NuGet dependency was added. |

See [Office inputs](office-input.md) for format and rendering limitations. CUDA hardware parity remains unvalidated.

## Earlier OCR advancement checkpoint (before Office inputs)

| Check | Result and evidence |
| --- | --- |
| Release solution build | Passed with **0 warnings, 0 errors**. Log: `artifacts/test-results/release240-final-build.log`. |
| Linux Native AOT | Publish and executable smoke passed. Log: `artifacts/test-results/release240-linux-aot-final.log`. The smoke covers JSON serialization, normalization and orphan evidence; it does **not** run OCR inference or establish model/provider compatibility under AOT. |
| NuGet packages | Both `PaddleOcrNet` and `PaddleOcrNet.Gpu` were packed as **2.4.0**, with the changelog included. Package dependency checks confirmed EasyImageSharp **1.2.0** and the GPU package's core dependency **2.4.0**. |
| Fresh package consumer | Passed against the generated core package, including serialization, normalization, verification and resource-option APIs. Log: `artifacts/test-results/release240-consumer.log`. |
| CUDA parity tooling | Tool build passed with **0 warnings**; missing-manifest rejection was verified to fail closed. No CUDA device was available, so GPU inference, parity and throughput remain **unvalidated**. |
| Earlier default suite | **979 passed, 274 skipped**. This is a prior checkpoint, not the result of the final model-enabled run below. |
| Earlier focused checks | **27 passed**, plus **21 passed** in the seal-cancellation/cache/resize selection. These are scoped checkpoints, not complete model-suite coverage. |
| Complete model-enabled integration run before Office addition | **1,245 passed, 10 skipped, 0 failed**, across all 29 groups / 1,255 cases. Artifacts: `artifacts/release240-final-integration/20261015T033011103Z-f52c8489/`; `summary.json`, per-group TRX and stdout/stderr logs retain exact filters and outcomes. |

An earlier full validation attempt was interrupted under memory pressure and is **not counted as passing**. The retry constrains the process-visible processor count to **4** to bound concurrency. An interrupted run cannot establish complete coverage even when individual tests previously reported success.

The ten skips are four commands requiring external labeled/baseline manifests, two optional detection performance measurements, and four known true-en-dash diagnostics. None are counted as passing coverage. The completed run includes real-model residency/reload and tensor reuse parity, cancellation, long lines, PDF fusion/selective recognition, orphan retention, structure golden stability and export contracts.

Software checks do not establish the roadmap's proposed accuracy or performance improvements. Independent labeled evaluation, representative calibration/retry data and actual CUDA hardware measurements remain separate requirements.
