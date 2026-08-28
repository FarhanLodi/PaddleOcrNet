# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 2.0.x   | ✅ Yes    |
| < 2.0   | ❌ No     |

Fixes ship in the latest 2.0.x release. If you are on an older version, upgrading is the fix.

## Reporting a vulnerability

**Please do not open a public issue.**

Report privately through
[GitHub Security Advisories](https://github.com/FarhanLodi/PaddleOcrNet/security/advisories/new).
That reaches the maintainers directly and stays private until a fix is available.

Please include the affected version, what an attacker can achieve, and — if you have one — a sample
input that reproduces it. A small file is far more useful than a description.

You can expect an acknowledgement within a few days. Once a fix ships, you will be credited in the
advisory unless you would rather not be.

## What this library does with untrusted input

Most of the attack surface is **the documents you feed it**. If you run OCR over files from
untrusted sources, these are the areas worth knowing about:

**Image decoding.** Decoding runs on [EasyImageSharp](https://github.com/FarhanLodi/EasyImageSharp).
Malformed images are a decoder-level concern; oversized dimensions are rejected from the image header
before pixels are allocated, so a small file claiming enormous dimensions is refused rather than
exhausting memory.

**PDF parsing.** PDFs are rasterized through PDFium (`Docnet.Core`), a native library. Untrusted PDFs
are the highest-risk input here, because parsing happens in native code. Consider process isolation
or resource limits if your workload accepts arbitrary PDFs.

**Model downloads.** Models are fetched over HTTPS and verified against pinned SHA-256 hashes,
**fail-closed** — a file whose hash does not match is rejected and never loaded, and an asset with no
pinned hash is refused rather than loaded unverified (the one escape hatch,
`ModelDownloadOptions.AllowUnverifiedModels`, is opt-in and off by default; don't enable it against a
mirror you don't control). A mirror configured through
`ModelDownloadOptions.BaseUrl` or the `PADDLEOCRNET_MODEL_BASE_URL` environment variable must still
serve byte-identical files, and non-`https://` URLs are rejected. For air-gapped deployments,
pre-seed the cache directory (`%LOCALAPPDATA%/PaddleOcrNet`, or `PADDLEOCRNET_CACHE`) instead.

**ONNX model files.** If you point the library at your own model files, treat them as code: ONNX
Runtime executes the graph they contain. Only load models you trust.

**LLM-backed document intelligence.** Key-information extraction, document Q&A and chart-to-data send
recognized text — and, on the vision path, page or chart **images** — to whatever OpenAI-compatible
endpoint you configure. That is a deliberate outbound data flow: the endpoint, its operator and its
retention policy are yours to choose. Nothing leaves the process unless you configure a model. Keep
API keys out of source control and prefer configuration or environment variables.

**Structured output.** Recognized text is written into output formats (Markdown, HTML, JSON, DOCX,
XLSX). If you render that output in a browser or open it in Office, escape or sanitize it first —
text recovered from an image is untrusted input to whatever consumes it, and a document can be
crafted to contain markup or formula-looking cell content.

## What is out of scope

* OCR being inaccurate, or recognizing text incorrectly. That is a bug, not a vulnerability — please
  open a normal issue.
* Vulnerabilities in ONNX Runtime, PDFium or other dependencies. Report those upstream; we will pick
  up fixed versions. Do tell us if we are pinning a version with a known advisory.
* Resource exhaustion from inputs you control yourself. OCR is inherently expensive; size limits are
  the caller's responsibility.
