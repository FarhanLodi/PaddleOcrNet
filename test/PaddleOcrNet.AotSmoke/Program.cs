using System.Text.Json;
using PaddleOcrNet.Export;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Export;
using PaddleOcrNet.Office;

var raw = OcrResult.Empty with
{
    FullText = "cafe\u0301  8:47",
    Coordinates = new OcrCoordinateProvenance { Frame = OcrCoordinateFrame.SourcePixels },
};
var document = new DocumentProcessingResult
{
    RawResult = raw,
    Quality = new PageQualityReport { RequiresReview = true, ReviewReasons = new[] { "smoke" } },
};
var json = JsonSerializer.Serialize(document, PaddleOcrJsonContext.Default.DocumentProcessingResult);
var restored = JsonSerializer.Deserialize(json, PaddleOcrJsonContext.Default.DocumentProcessingResult)
    ?? throw new InvalidOperationException("Missing generated JSON result.");
if (restored.RawResult.FullText != raw.FullText || restored.RawResult.Coordinates?.SourceMappingAvailable != true)
    throw new InvalidOperationException("Source-generated document roundtrip failed.");
var normalized = raw.ToNormalizedText(new TextNormalizationOptions { CanonicalUnicode = true, CleanWhitespace = true });
if (normalized.Text != "café 8:47" || normalized.RawText != raw.FullText)
    throw new InvalidOperationException("Evidence-preserving normalization failed.");
var structure = new StructureResult
{
    Blocks = Array.Empty<StructureBlock>(),
    OrphanLines = new[] { new OcrLine { Text = "Unowned 8:47", Confidence = .8 } },
};
var assembled = DocumentAssembler.Assemble(new[] { new DocumentPage { PageNumber = 1, Structure = structure } });
using var structureJson = JsonDocument.Parse(structure.ToJson());
using var documentJson = JsonDocument.Parse(assembled.ToJson());
if (structureJson.RootElement.GetProperty("OrphanLines")[0].GetProperty("Text").GetString() != "Unowned 8:47"
    || documentJson.RootElement.GetProperty("Pages")[0].GetProperty("OrphanLines").GetArrayLength() != 1)
    throw new InvalidOperationException("Source-generated orphan evidence failed.");
Console.WriteLine("Document JSON, normalization and orphan evidence smoke passed.");

var officeSource = new StructureResult
{
    Blocks = new[] { new StructureBlock(StructureBlockType.Table, new OcrBoundingBox(0, 0, 100, 40), 0,
        TableHtml: "<table><tr><td>Office 8:47</td></tr></table>") },
};
foreach (var bytes in new[] { officeSource.ToDocx(), officeSource.ToXlsx() })
{
    using var input = new MemoryStream(bytes);
    var office = OfficeDocumentReader.Read(input);
    if (!office.FullText.Contains("Office 8:47", StringComparison.Ordinal))
        throw new InvalidOperationException("Native Office extraction failed.");
    using var officeJson = JsonDocument.Parse(office.ToJson());
    if (officeJson.RootElement.GetProperty("Sections").GetArrayLength() != 1)
        throw new InvalidOperationException("Office source-generated JSON failed.");
}
Console.WriteLine("DOCX/XLSX native extraction and Office JSON smoke passed.");
