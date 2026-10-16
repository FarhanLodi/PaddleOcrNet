# Full-reference transcription evaluation

The test-side evaluator in `test/PaddleOcrNet.Tests/Evaluation` scores complete
transcriptions independently of model confidence. It does not supply an annotated
real-document corpus. The existing anchor-based integration corpus remains a
separate smoke test, not evidence of full transcription accuracy.

Run its deterministic unit tests:

```powershell
dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj --filter FullyQualifiedName~TranscriptionBenchmarkTests
```

To evaluate externally generated predictions, supply JSON files and a report path:

```powershell
$env:PADDLEOCRNET_BENCHMARK_MANIFEST = 'D:\datasets\ocr\manifest.json'
$env:PADDLEOCRNET_BENCHMARK_PREDICTIONS = 'D:\datasets\ocr\candidate-run.json'
$env:PADDLEOCRNET_BENCHMARK_REPORT = 'D:\datasets\ocr\candidate-report.json'
dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj --filter FullyQualifiedName~Evaluate_supplied_full_transcription_manifest
```

Without a manifest the external-corpus test is explicitly skipped. Once supplied,
missing files, empty datasets, missing or extra predictions, duplicate IDs and
document/origin groups spanning splits fail evaluation. This command measures
transcription fidelity; it does not itself measure inference latency or memory.

Manifest fields (JSON names are case insensitive):

| Field | Meaning |
| --- | --- |
| `DatasetRevision` | Immutable dataset revision, preferably a content hash |
| `License` | License or internal usage authorization identifier |
| `Pages` | Nonempty array of page annotations |
| `Pages[].Id`, `DocumentId` | Unique page ID and originating document ID |
| `Pages[].OriginGroup` | Shared template/provider/document family used to prevent split leakage |
| `Pages[].SourceKind` | `real` or `synthetic` |
| `Pages[].Split` | `training`, `calibration` or `heldout` |
| `Pages[].Category` | Stable evaluation category such as `receipts` or `mixed_script` |
| `Pages[].Reference` | Full adjudicated transcription, including punctuation and line breaks; empty string for blank pages |
| `Pages[].SensitiveTokens` | Dictionary from annotated field IDs to exact reference values; use `{}` when absent |

Prediction-run fields are `RepositoryRevision`, `Runtime`, `Provider`, `Precision`,
`Hardware`, `Threads` (positive integer), `OptionsJson` (serialized options string),
`ModelAndDictionarySha256` (artifact name to 64-character SHA-256), and `Predictions`.
Each prediction contains `Id`, full `Text`, and `SensitiveTokens` keyed by the same
annotated field IDs. Record every actual model/dictionary hash, rather than a model
family name. The evaluator validates hash syntax, not artifact existence.

Sensitive fields require explicit extraction/alignment before scoring. A value
appearing elsewhere on the page cannot satisfy a missing field. Missing fields
count as incorrect; matching is ordinal and is never normalized. The current
metric reports labeled-field correctness, not precision of extra predicted fields.

The machine-readable report contains page scores, source/split/category slices,
the normalization definition, dataset revision and run provenance. No aggregate
combines real and synthetic results or training and held-out results. CER/WER are
micro-averages of edit counts and reference lengths within each slice. Insertions
can make an error rate greater than one. Blank references have null CER/WER and
their invented text is reported separately as false-positive scalar counts and
pages. Whitespace is significant in the raw score, including on otherwise blank
pages; the explicit normalized view collapses it.

CER uses Unicode scalar values, not UTF-16 code units. WER splits on Unicode
whitespace; it is not a language-specific segmenter for CJK or other unspaced
scripts. Raw exact-page match, line edit distance, and exact-line recall are
available per page. Exact-line recall counts the longest order-preserving sequence
of identical lines divided by reference line count, so omission of an earlier line
does not shift all later matches. Extra lines remain visible in line edit distance,
CER/WER, and page exact match. Raw line boundaries are LF characters.

Normalization is explicitly NFC, CRLF/CR to LF, and whitespace trimming/collapse
within each LF-delimited line. It preserves case, accents, compatibility characters,
punctuation, signs, decimal separators and line breaks. Raw and normalized results
remain separate. Grapheme scoring, polygon detection recall, document-level
confidence intervals, calibration, tables, formulas and release thresholds still
need their own annotated evaluators; this foundation does not claim those gates.

The structure golden harness now supports an enforced cold/warm output stability
check with `PADDLEOCRNET_GOLDEN_REQUIRE_STABLE=1`, in addition to its existing
`PADDLEOCRNET_RUN_INTEGRATION=1` gate. A selected but missing fixture fails, and an
asset filter that selects nothing fails. Reports are written before stability is
asserted. This compares the same run's complete JSON and Markdown outputs; it is
not a persisted cross-revision semantic golden or a statistically reliable p95
benchmark. Use the explicit stability gate for deterministic provider comparisons.

## Labeled retry damage checks

Set `PADDLEOCRNET_RETRY_MANIFEST` to a JSON file with `datasetRevision` and
`pages`, each containing `id`, relative `imagePath`, full `reference`, and a
`criticalTokens` array. Set `PADDLEOCRNET_RUN_INTEGRATION=1` and run:

```powershell
dotnet test test/PaddleOcrNet.Tests --filter 'FullyQualifiedName~Labeled_retry_reports_corrections_damage_and_critical_token_regressions'
```

Missing requested manifests/images fail. `PADDLEOCRNET_GOLDEN_DIR`
selects the output directory for `retry-labeled-measurement.json`; the report also
appears in test output. The report includes raw/normalized scores, corrected and
damaged page counts, and losses/recoveries of exact bounded critical-token
occurrences. The gate rejects damaged pages or lost critical tokens. This token
occurrence check is distinct from the evaluator's labeled-field exact matches.
Retry remains internal and experimental; this harness does not change its policy.
