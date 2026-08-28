## What this changes

<!-- What it does and why. Link the issue if there is one: Fixes #123 -->

## How it was verified

<!-- Delete what does not apply. -->

- [ ] `dotnet build PaddleOcrNet.sln -c Release` is clean — 0 errors, no new warnings
- [ ] Unit tests pass: `dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release --filter "Category!=Integration"`
- [ ] Full suite pass (downloads models): `PADDLEOCRNET_RUN_INTEGRATION=1 dotnet test test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj -c Release`
- [ ] Added or updated tests covering this change

> CI only runs the non-integration filter, so it will not catch an accuracy regression. If this
> touches preprocessing, detection, recognition, decoding, orientation/unwarp or the structure
> pipeline, please run the full suite locally and say so.

## Compatibility

- [ ] No public type, signature or default changed
- [ ] This changes public API or behaviour — explained below

<!-- A break is a major-version decision: say what breaks, who it affects, and why an additive
     change would not work. -->

## Does OCR output change?

- [ ] No — output is identical
- [ ] Yes — before/after shown below on real input

<!-- Refactors on the inference path can shift results in ways the tests do not catch. If output
     moves at all, show it rather than assuming it is noise. -->

## Anything else

<!-- New dependency? Changed model checksum or download URL? Touched the structure pipeline (which is
     hand-synced with EasyOcrSharp)? Say so here — all three get extra scrutiny. A changed checksum
     needs to say what produced the new file. -->
