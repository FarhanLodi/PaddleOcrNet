# Contributing to PaddleOcrNet

Thanks for taking the time. This guide covers the things that are specific to this repository — the
parts that are easy to get wrong because they are not obvious from the code.

## Getting set up

You need the **.NET 10 SDK**. Everything targets `net10.0` only; please don't add older target
frameworks.

```bash
git clone https://github.com/FarhanLodi/PaddleOcrNet
cd PaddleOcrNet
dotnet build PaddleOcrNet.sln -c Release
```

The build should be clean — no new warnings. `GenerateDocumentationFile` is on, so a public member
without an XML doc comment shows up as a warning; treat that as something to fix, not noise.

## Running the tests

There are two suites, and the difference matters.

```bash
# Unit tests. No network, no models, runs in seconds. This is what CI gates on.
dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release --filter "Category!=Integration"

# Everything, including tests that download models and run real inference.
PADDLEOCRNET_RUN_INTEGRATION=1 dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release
```

Integration tests are **double-gated**: they carry `[Trait("Category", "Integration")]` *and* skip
themselves unless `PADDLEOCRNET_RUN_INTEGRATION` is `1`. Without that variable they report as skipped
even when you don't pass the filter — that is expected, not a failure. On a cold cache the full run
downloads several hundred MB of ONNX models from
[the model repository](https://huggingface.co/PaddleOcrNet/PaddleOcrNet-models) into
`%LOCALAPPDATA%\PaddleOcrNet` (or `PADDLEOCRNET_CACHE`, or the platform equivalent).

CI runs the non-integration filter on Linux, Windows and macOS, so **an accuracy regression will not
be caught by CI**. If you touch anything on the inference path — preprocessing, the detector, the
recognizer, the CTC decoder, orientation/unwarp, or the structure pipeline — run the full suite
locally and say so in the pull request.

CI also runs a Native AOT publish smoke test and a `dotnet pack` job. The AOT job is
`continue-on-error` and informational: it won't block your PR, but if it starts failing on your
change, that is a real trim/AOT break worth fixing — the library advertises `IsAotCompatible`, so
avoid unguarded reflection, dynamic codegen and unannotated generic instantiation on public paths.

## Things that will get a pull request sent back

**Breaking the public API.** Renaming or moving a public type, changing a signature, or changing a
default is a major-version decision, not a pull-request decision. Add an overload instead of changing
one. If you believe a break is genuinely necessary, open an issue first and make the case.

**Changing model checksums without saying why.** Every model download is verified against a pinned
SHA-256 in `PaddleModelRegistry` and fails closed. If a pin changes, the pull request must explain
what produced the new file and why the old one is wrong. Checksums are regenerated with
`tools/stage_and_checksum.py` (core models) and `tools/stage_structure_models.py` (structure models) —
say which you ran.

**Silently changing OCR output.** Small refactors on the inference path can shift results in ways the
unit tests don't catch. If output changes, show before/after on real images.

## Code style

Match the file you are editing. The codebase has a consistent voice — explanatory comments that say
*why* rather than restating the code, XML docs on public members, and no dead code. A few specifics:

* Every public member needs an XML doc comment.
* Comments should explain intent and constraints, not narrate. If a value is non-obvious — a
  threshold, a magic number, a fallback — say where it came from. Where a constant mirrors upstream
  PaddleOCR, name the upstream source.
* Don't add a dependency without discussing it in an issue first. Keeping the graph small and
  permissively licensed is a deliberate goal of this project: every runtime dependency is MIT or
  BSL-1.0, with no commercial tier or revenue threshold anywhere in the stack.

## The structure engine is shared with EasyOcrSharp by hand

`src/PaddleOcrNet/Structure/` implements PaddleOCR's PP-StructureV3 pipeline. The same engine was
ported into [EasyOcrSharp](https://github.com/FarhanLodi/EasyOcrSharp) under
`src/EasyOcrSharp/Structure/`, and the two trees are kept in sync **by hand** — a fix here does not
reach the other automatically. Types carrying a `Paddle` prefix here were renamed there
(`PaddleStructureEngine` → `StructureEngine`, `PaddleOcrService` → `StructureService`, and so on), so
porting a change needs that mapping in mind. If you fix something in the structure pipeline, please
mention it in the PR so the change can be mirrored.

## Reporting bugs

Include the PaddleOcrNet version, the .NET version, your OS, and — if at all possible — an image or
PDF that reproduces it. "OCR is wrong on my document" is very hard to act on without the document; a
cropped region that still shows the problem is ideal and avoids sharing anything sensitive.

## Security

Please don't open a public issue for a security problem. See [SECURITY.md](SECURITY.md).

## Code of conduct

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Licensing

By contributing you agree that your contributions are licensed under the MIT License, the same terms
that cover the rest of the project.
