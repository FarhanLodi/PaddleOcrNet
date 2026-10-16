# Fitting and evaluating confidence calibration

The offline harness fits monotonic, pack-specific correctness curves with weighted isotonic regression (pool-adjacent-violators). Labels must describe an independently adjudicated correctness event, such as exact-line transcription, consistently across packs. Scores alone cannot supply correctness labels. The runtime interpolates the fitted knots linearly and binds them to model and dictionary SHA-256 hashes.

Prepare JSON with `version`, `assets` (pack names mapped to `modelSha256` and `dictionarySha256`) and `samples`. Every sample contains `documentId`, `pack`, `split` (`calibration` or `test`), `rawConfidence` (finite 0–1), and `correct` (boolean). Use originating document/template IDs to prevent related pages leaking between splits. Include both correct and incorrect examples across the deployment confidence range and script/degradation slices; a single-score dataset is accepted mathematically but cannot establish useful discrimination.

```powershell
$env:PADDLEOCRNET_CALIBRATION_INPUT = 'D:\data\adjudicated-confidence.json'
$env:PADDLEOCRNET_CALIBRATION_OUTPUT = 'D:\data\calibration-results'
dotnet test test/PaddleOcrNet.Tests -c Release --filter FullyQualifiedName~FitAndEvaluateLabeledCalibrationManifest
```

The command writes `curves.json`, directly deserializable into `Dictionary<string, RecognitionCalibration>` for `AutoRoutingOptions.Calibration`, and `evaluation.json` with overall and per-pack held-out reliability bins, expected calibration error, Brier score and error-versus-accepted-coverage points. Tied confidence values are accepted together. It rejects empty splits, nonfinite scores, missing asset hashes and documents appearing in both fitting and held-out evaluation. Missing input explicitly skips the command test; it is not evidence that calibration has passed.

`PairedDocumentBootstrap` additionally evaluates paired baseline/candidate document errors with reproducible document-level resampling and 95% percentile intervals. Aggregate correlated pages and crops before supplying scores. Each subgroup reports `pass`, `regression`, `inconclusive` or `insufficient-evidence`; the default minimum is 20 independent documents and 2,000 replicates. Negative deltas favor the candidate. These intervals are descriptive bootstrap estimates, not a guarantee or a substitute for prospective sample-size planning. Enforce every required subgroup's gate, not just an aggregate mean, and pin the seed and dataset revision.

No real calibration dataset or fitted production curve ships with this change. Synthetic unit tests establish algorithm and leakage-check behavior only. Routing calibrated correctness is appropriate only when all admitted packs use comparable adjudicated events and representative fitting data.
