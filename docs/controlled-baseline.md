# Controlled CPU OCR baseline

`ControlledBaselineTests.Run_controlled_cpu_baseline` executes OCR against annotated image pages, then scores every pass with the full-transcription evaluator. It skips unless `PADDLEOCRNET_BASELINE_INPUT` names an input JSON file. Once opted in, missing files/models, malformed annotations and inference failures fail the test. Models are never downloaded.

Example input (image paths are relative to this JSON file):

```json
{
  "RepositoryRevision": "your-tested-git-revision-plus-description-of-local-changes",
  "WarmRuns": 3,
  "Threads": 1,
  "Languages": ["English"],
  "ModelCachePath": "D:/model-cache",
  "Images": { "page-1": "images/page-1.png" },
  "Manifest": {
    "DatasetRevision": "adjudicated-corpus-v1",
    "License": "private evaluation; redistribution prohibited",
    "Pages": [{
      "Id": "page-1",
      "DocumentId": "document-1",
      "OriginGroup": "template-provider-1",
      "SourceKind": "real",
      "Split": "heldout",
      "Category": "clean-english",
      "Reference": "Exact full page transcription",
      "SensitiveTokens": {}
    }]
  }
}
```

The reference is human-adjudicated full transcription, not an OCR-generated label. Label generated fixtures `synthetic`. Origin groups and documents cannot cross dataset splits. See [transcription-benchmarks.md](transcription-benchmarks.md) for scoring rules. Languages default to English, warm runs to 3 and threads to 1. When ModelCachePath is omitted, the normal `PADDLEOCRNET_CACHE`/application cache resolution applies. Required registry-resolved assets must already exist.

Run from the repository in a dedicated process with competing workloads stopped:

```powershell
$env:PADDLEOCRNET_BASELINE_INPUT = 'D:/corpus/baseline-input.json'
$env:PADDLEOCRNET_BASELINE_OUTPUT = 'D:/corpus/results/current'
dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release --filter 'FullyQualifiedName~ControlledBaselineTests' --logger 'console;verbosity=normal'
```

Outputs are `predictions.json` (every run), `reports.json` (every run's raw/normalized scores and dataset slices), and `measurements.json` (per-page timing, provenance and resource samples). The default output directory is `baseline-output` beside the input. Rerunning replaces these artifacts; choose distinct output directories for comparisons.

The harness pins CPU execution, ONNX intra-op threads, ONNX inter-op threads to one, recognition preprocessing parallelism and EasyImageSharp parallelism. Internal detector `Parallel.For` still uses the runtime thread pool: these settings are not a process-wide thread cap. It excludes parallel xUnit collections and restores EasyImageSharp's configuration afterward. Pages use deterministic shuffled order per pass. Run 0 includes lazy session startup and is reported separately; later pages in that pass may reuse sessions. Subsequent passes reuse the same service. This is not a process-cold measurement for every page. Three warm observations are a smoke baseline, not enough for trustworthy tail latency or statistical accuracy claims.

Stage samples come from the actual `paddleocr.stage.duration` meter; nested or concurrent stages must not be summed as total latency. Managed allocation deltas cover the process; working set is sampled after each call; peak working set is the process-lifetime high-water mark. These do not isolate per-call native memory or GPU memory. Use an external profiler for those measurements.

Model/dictionary hashes are explicitly labeled **configured registry cache inventory**. The registry may include assets that a particular page never loads; the harness does not claim an inventory entry proves a loaded session. Images, input JSON, runtime/provider/options and the supplied repository revision are recorded. Runtime hardware metadata describes the visible host; record additional physical CPU/container/power configuration alongside the run when comparing machines.

Sensitive-field predictions remain empty because this harness performs OCR, not a field extractor. Annotated sensitive tokens therefore count as unrecovered, rather than being copied from ground truth or inferred by substring matching. Add an independently defined extraction stage before interpreting sensitive-field accuracy.

This runner makes a controlled baseline executable. It does not supply a licensed, adjudicated 300–500-page real corpus or establish the advancement plan's release gates by itself.

## Repository smoke fixture

The checked-in [smoke input](../benchmarks/smoke/input.json) references `test/Assets/ocr_test1.png` and contains one manually transcribed synthetic multilingual/punctuation page. It verifies execution and artifact generation; it is neither a representative corpus nor an independently adjudicated accuracy baseline. Its license field explicitly limits this evaluation to local smoke use without asserting redistribution rights.

```powershell
$env:PADDLEOCRNET_BASELINE_INPUT = (Resolve-Path 'benchmarks/smoke/input.json').Path
$env:PADDLEOCRNET_BASELINE_OUTPUT = Join-Path $PWD 'artifacts/baseline-smoke'
dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release --filter 'FullyQualifiedName~ControlledBaselineTests'
```

Pre-seed the configured cache before running; offline mode remains enforced. The sample revision is explicitly a `WORKTREE` placeholder. Replace it with the tested revision and local-diff identification before making controlled comparisons. The smoke execution completed successfully during implementation; its small size does not establish accuracy or throughput release gates.
