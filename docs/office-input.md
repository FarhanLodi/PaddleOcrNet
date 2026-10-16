# Office document inputs

`PaddleOcrNet.Office` reads native content from `.docx`, `.xlsx` and `.pptx` files and can OCR their embedded images through the existing OCR service. It is included in the existing `PaddleOcrNet` package. The adapters use built-in ZIP/XML APIs; they do not add an Office SDK or another NuGet dependency.

```csharp
using PaddleOcrNet.Models;
using PaddleOcrNet.Office;
using PaddleOcrNet.Services;

await using var ocr = new PaddleOcrService();
var document = await ocr.ExtractTextFromOfficeAsync(
    "report.docx",
    new OfficeDocumentOptions
    {
        RecognizeEmbeddedImages = true,
        Languages = new[] { OcrLanguage.English },
        Recognition = new RecognitionOptions { ReturnWordBoxes = true }
    },
    cancellationToken);

Console.WriteLine(document.FullText);
foreach (var section in document.Sections)
{
    Console.WriteLine($"{section.Index}: {section.Name} [{section.SourcePart}]");
    foreach (var block in section.Blocks)
        Console.WriteLine(block.Text);
}
foreach (var warning in document.Warnings)
    Console.WriteLine(warning);
```

Pass a readable `Stream` to the same service extension when the input is already available in memory or through another input adapter. Reading starts at its current position and leaves the caller's stream open. `RecognizeEmbeddedImages` defaults to true; `Languages` defaults to `OcrLanguage.Auto`. The recognition options affect embedded-image OCR, not the native document text. Model downloads and normal service configuration apply when image OCR is required.

For native content without OCR or model loading:

```csharp
using PaddleOcrNet.Office;

var document = OfficeDocumentReader.Read(
    "workbook.xlsx",
    new OfficeDocumentOptions(),
    cancellationToken);

// The reader is native-only even though service extraction enables image OCR by default.
Console.WriteLine(document.FullText);
```

The standalone reader also accepts a readable stream. Alternatively, use the service extension with `RecognizeEmbeddedImages = false` to omit image inference explicitly.

## Returned content

The `OfficeDocumentResult` identifies its `Format` (`Docx`, `Xlsx` or `Pptx`), contains one-based `Sections` with a name and source package part, collects `Warnings`, and exposes a combined `FullText` view. Blocks distinguish paragraphs, tables, cells and images. Native tables/cells preserve their available text and structure in `Cells`; spreadsheet cells expose `CellReference` and `Formula` separately from their value text. Image blocks identify `ImagePart` and retain the complete `OcrResult` in `ImageOcr` when the service recognizes them. Inspect these structured fields when source membership matters; `FullText` alone cannot express all table, sheet or slide relationships. `document.ToJson()` uses the library's source-generated metadata and retains these native/image distinctions.

| Format | Native organization | Interpretation limits |
| --- | --- | --- |
| DOCX | Document body and supported header/footer content, paragraphs, tables and embedded images | Sections identify content parts, not rendered pages. Word pagination, floating-object placement and visual reading order are not computed. |
| XLSX | Worksheets and cells, raw cached values, formula strings and supported embedded images | No formula evaluation, Excel calculation engine or formatted display rendering. Cached results can be stale or absent; native value strings are not a guarantee of their on-screen number/date format. |
| PPTX | Slides in presentation order, supported shape/table text and embedded images | Shape XML order is not a guarantee of visual reading order. Slide layout, animation state, font metrics and precise rendered geometry are not reconstructed. |

Embedded-image OCR runs sequentially and uses the guarded decoder before calling the service's decoded-image overload. `PaddleOcrService` contributes its configured loading limits; a custom `IPaddleOcrService` uses the default guards. Only the first frame of an embedded GIF, TIFF or other multiframe image is recognized. Unsupported codecs, including unsupported Office vector-image encodings, fail explicitly when OCR is requested. Decode or recognition failures are surfaced; extraction does not silently return a partial-success result. The coordinates in an image's `ImageOcr` describe that image's own pixel frame, subject to its OCR provenance. They are not Word page positions, worksheet cell coordinates or slide geometry. Native Office text does not receive fabricated OCR confidence or bounding polygons.

Word footnotes/endnotes, imported `altChunk` content, PowerPoint speaker notes, and chart/OLE object contents are not extracted or rendered by these readers. Unsupported relationships and features can produce warnings. Supported table merge representations retain row/column spans; unsupported legacy or inconsistent merge forms retain available cell evidence with warnings. Shared Excel formulas without an inline expression are not expanded; their available cached values remain separate from formula metadata.

## Bounded package parsing

`OfficeDocumentOptions` supplies these defaults:

| Limit | Default |
| --- | ---: |
| `MaxInputBytes` | 64 MiB |
| `MaxExpandedBytes` | 256 MiB |
| `MaxPartBytes` | 32 MiB |
| `MaxEntries` | 10,000 |
| `MaxSections` | 1,000 |
| `MaxBlocks` | 100,000 |
| `MaxImages` | 100 |
| `MaxXmlCharacters` | 16 Mi characters |
| `MaxTextCharacters` | 32 Mi characters |

`MaxExpandedBytes` bounds both the sum of declared uncompressed ZIP entry sizes and the cumulative bytes of parts actually read, including repeated reads of reused images. `MaxBlocks` counts blocks and native table cells together; `MaxImages` counts occurrences admitted for OCR, including reused images. `MaxXmlCharacters` applies per XML part; `MaxTextCharacters` bounds aggregate extracted native/OCR text occurrences. Limits are positive except `MaxImages`, which may be zero; zero does not mean unlimited. `MaxInputBytes` must also fit within `Int32.MaxValue` because the bounded input is buffered. XML DTDs are prohibited, and XML nesting deeper than 128 is rejected.

Limits bound accepted package input, expansion and document traversal; exceeding a configured limit is an error rather than a partial-success result. They are not promises about maximum total process memory or native inference duration. Cancellation is propagated through parsing and image recognition; an active native model invocation remains cooperatively cancellable. Image dimensions and decoding also remain subject to the OCR service's image guards.

External relationships and links are never fetched. The adapters consume document content as data; links, formula strings or embedded instructions are not executed. This API does not evaluate spreadsheet formulas or execute Office automation.

Legacy binary `.doc`, `.xls` and `.ppt`, password-encrypted packages, macro-enabled and template formats, and HTML are outside this adapter's supported input contract. It is native-content extraction plus optional image OCR, not an Office renderer or a promise of complete recovery of every drawing, chart, equation or embedded object. Inspect `Warnings` alongside the structured result and use a separate rendering workflow when exact pagination, visual layout or unsupported object content is required.

Software fixtures can verify parsing, source membership, limits, cancellation and embedded-image routing. They do not establish accuracy on all Office producers or real document collections. Refer to release-specific test artifacts for executed validation; earlier OCR/PDF test counts do not cover this subsequently added Office adapter.

The post-addition Release build passed with zero warnings/errors. The full default suite passed **1,021 tests**, with **275 gated skips** and no failures. The focused Office/export selection passed **54 tests with no skips**, including real embedded-image OCR parity against direct CPU OCR. Logs and TRX are in `artifacts/test-results/office-final-build.*`, `office-default.*` and `office-final-focused.*`. See the [release validation report](release-2.4.0-validation.md) for deployment/package checks.

Format references: Microsoft's [SpreadsheetML structure](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/structure-of-a-spreadsheetml-document), [shared-string table](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/working-with-the-shared-string-table) and [PresentationML structure](https://learn.microsoft.com/en-us/office/open-xml/presentation/structure-of-a-presentationml-document).
