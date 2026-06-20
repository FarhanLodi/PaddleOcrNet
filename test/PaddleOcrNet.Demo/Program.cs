using PaddleOcrNet.Services;
// using PaddleOcrNet.Structure;   // uncomment for the AnalyzeDocumentAsync structure snippet below

// Minimal PaddleOcrNet demo / smoke entry point.
//
// It constructs a PaddleOcrService (no model download happens at construction time — the detector,
// classifier and recognizer ONNX sessions are loaded lazily on the first real OCR call) and prints
// usage. Pass an image path to actually run the det -> cls -> rec pipeline; without arguments it just
// prints help and the resolved execution provider, so it stays runnable in CI with no models present.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

// Construct the service. The parameterless-friendly constructor resolves the execution provider
// (Auto -> CUDA / DirectML / CoreML / CPU) but loads no models yet.
await using var service = new PaddleOcrService();

// If a usable GPU was detected but OCR is on CPU, the service exposes an actionable hint.
if (service.GpuAccelerationHint is { } hint)
{
    Console.WriteLine(hint);
    Console.WriteLine();
}

var imagePath = args[0];
var languages = args.Length > 1 ? args[1..] : new[] { "en" };

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"Image not found: {imagePath}");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

Console.WriteLine($"Running OCR on '{imagePath}' (languages: {string.Join(", ", languages)})...");
Console.WriteLine("Note: required PP-OCRv5 ONNX models download on first use; see README for the model host note.");
Console.WriteLine();

var result = await service.ExtractTextFromImage(imagePath, languages);

Console.WriteLine($"--- {result.Lines.Count} line(s), {result.Duration.TotalMilliseconds:F0} ms, GPU={result.UsedGpu} ---");
foreach (var line in result.Lines)
{
    Console.WriteLine($"[{line.Confidence:F2}] {line.Text}");
}
Console.WriteLine();
Console.WriteLine("--- Full text ---");
Console.WriteLine(result.FullText);
return 0;

static void PrintUsage()
{
    Console.WriteLine("PaddleOcrNet demo");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  PaddleOcrNet.Demo <imagePath> [lang ...]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  PaddleOcrNet.Demo invoice.png            # English (default)");
    Console.WriteLine("  PaddleOcrNet.Demo receipt.jpg en de fr   # multi-language");
    Console.WriteLine("  PaddleOcrNet.Demo page.png ch            # Chinese");
    Console.WriteLine();
    Console.WriteLine("Languages: en, ch/zh, ja, ko, latin (fr/de/es/...), cyrillic (ru/uk/...),");
    Console.WriteLine("           arabic, devanagari (hi/mr/...), thai, greek, tamil, telugu, and more.");
    Console.WriteLine();
    Console.WriteLine("Models (PP-OCRv5 ONNX) are downloaded and cached on first use. Override the host with");
    Console.WriteLine("the PADDLEOCRNET_MODEL_BASE_URL environment variable. See the project README for details.");
}

// ---------------------------------------------------------------------------------------------------
// Document-structure analysis (PP-StructureV3): layout detection + table (SLANet) + formula (LaTeX-OCR)
// + seal + optional doc-orientation/unwarp, assembled in reading order (XY-cut) and exportable to
// Markdown / JSON. Shown here as a commented snippet because it needs the structure ONNX models to run
// (layout / SLANet / LaTeX-OCR / seal-det), which are not downloaded in this no-model demo path.
//
//   await using var service = new PaddleOcrService();
//
//   // Pick the layout model and which sub-recognizers to run (all default on).
//   var options = new StructureOptions
//   {
//       LayoutModel       = LayoutModel.PicoDetS,  // or PicoDetM / RtDetrL (highest accuracy)
//       UseDocOrientation = true,                  // upright a rotated scan first (0/90/180/270)
//       RecognizeTables   = true,                  // SLANet table structure -> HTML
//       RecognizeFormulas = true,                  // LaTeX-OCR -> LaTeX
//       RecognizeSeals    = true,                  // seal text
//       Languages         = new[] { "en" },
//   };
//
//   StructureResult doc = await service.AnalyzeDocumentAsync("page.png", options);
//
//   // Reading-ordered blocks (text / title / table / formula / figure / seal ...):
//   foreach (var block in doc.Blocks)
//       Console.WriteLine($"#{block.Order} {block.Type}: {block.Text ?? block.TableHtml ?? block.Latex}");
//
//   // Export the whole analyzed document:
//   string markdown = doc.ToMarkdown();   // headings, paragraphs, tables (HTML), formulas ($$...$$)
//   string json     = doc.ToJson();       // AOT-safe, source-generated
// ---------------------------------------------------------------------------------------------------
