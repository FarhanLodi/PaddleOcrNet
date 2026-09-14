using PaddleOcrNet.Models;
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

// Parity harness mode (tools/parity): emits the shared result JSON schema documented in
// tools/parity/README.md. Kept separate from the human-facing demo path below.
if (args[0] == "--parity")
{
    return await ParityMode.RunAsync(args);
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
var languageCodes = args.Length > 1 ? args[1..] : new[] { "en" };

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"Image not found: {imagePath}");
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

// Parse the CLI string codes (e.g. "en", "ch", "auto") into the strongly-typed OcrLanguage enum.
IReadOnlyList<OcrLanguage> languages;
try
{
    languages = OcrLanguageExtensions.FromCodes(languageCodes);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    PrintUsage();
    return 2;
}

Console.WriteLine($"Running OCR on '{imagePath}' (languages: {string.Join(", ", languageCodes)})...");
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
// Parity harness mode — tools/parity/README.md "Shared result JSON schema".
//
//   PaddleOcrNet.Demo --parity <imagePath> --pipeline ocr|structure --out <json>
//                     [--lang <code> [<code> ...]] [--variant mobile|server]
//
// ocr:       PaddleOcrService.ExtractTextFromImage with RecognitionOptions defaults (text-line
//            orientation ON, doc orientation ON, unwarp OFF, DropScore 0) → lines (4-pt polys,
//            python sorted_boxes order) + fullText.
// structure: AnalyzeDocumentAsync with StructureOptions defaults (tables/formulas/seals ON, doc
//            preprocessing OFF) → lines + fullText + blocks (paddle label vocabulary) + tables + markdown.
// JSON is written UTF-8 without BOM, non-ASCII verbatim.
// ---------------------------------------------------------------------------------------------------
internal static class ParityMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? imagePath = null;
        string? outPath = null;
        string pipeline = "ocr";
        string variant = "mobile";
        string tableModel = "slanet";
        bool noFormulas = false;
        var langCodes = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipeline":
                    pipeline = Next(args, ref i, "--pipeline");
                    break;
                case "--out":
                    outPath = Next(args, ref i, "--out");
                    break;
                case "--variant":
                    variant = Next(args, ref i, "--variant");
                    break;
                case "--table-model":
                    tableModel = Next(args, ref i, "--table-model");
                    break;
                case "--no-formulas":
                    noFormulas = true;
                    break;
                case "--lang":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        langCodes.Add(args[++i]);
                    break;
                default:
                    if (imagePath is null) { imagePath = args[i]; break; }
                    Console.Error.WriteLine($"Unexpected argument '{args[i]}'.");
                    return 2;
            }
        }

        if (imagePath is null || outPath is null)
        {
            Console.Error.WriteLine("Usage: --parity <imagePath> --pipeline ocr|structure --out <json> [--lang <codes>] [--variant mobile|server]");
            return 2;
        }
        if (!File.Exists(imagePath))
        {
            Console.Error.WriteLine($"Image not found: {imagePath}");
            return 2;
        }
        if (pipeline is not ("ocr" or "structure"))
        {
            Console.Error.WriteLine($"Unknown pipeline '{pipeline}' (expected ocr|structure).");
            return 2;
        }
        if (variant is not ("mobile" or "server"))
        {
            Console.Error.WriteLine($"Unknown variant '{variant}' (expected mobile|server).");
            return 2;
        }
        if (tableModel is not ("slanet" or "slanext"))
        {
            Console.Error.WriteLine($"Unknown table model '{tableModel}' (expected slanet|slanext).");
            return 2;
        }
        if (langCodes.Count == 0) langCodes.AddRange(new[] { "ch", "en" });

        var modelVariant = variant == "server" ? OcrModelVariant.Server : OcrModelVariant.Mobile;
        var serviceOptions = new PaddleOcrServiceOptions
        {
            DetectionModel = modelVariant,
            RecognitionModel = modelVariant,
        };

        var initSw = System.Diagnostics.Stopwatch.StartNew();
        await using var service = new PaddleOcrService(serviceOptions);
        initSw.Stop();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        var buffer = new MemoryStream();
        await using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            if (pipeline == "ocr")
            {
                await RunOcrAsync(service, writer, imagePath, langCodes, variant, initSw.Elapsed.TotalSeconds);
            }
            else
            {
                await RunStructureAsync(service, writer, imagePath, variant, noFormulas, tableModel, initSw.Elapsed.TotalSeconds);
            }
        }

        // UTF-8 without BOM (MemoryStream + File.WriteAllBytes never adds one).
        File.WriteAllBytes(outPath, buffer.ToArray());
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {flag}.");
        return args[++i];
    }

    private static async Task RunOcrAsync(
        PaddleOcrService service, System.Text.Json.Utf8JsonWriter w, string imagePath,
        List<string> langCodes, string variant, double initSeconds)
    {
        var languages = OcrLanguageExtensions.FromCodes(langCodes);
        // Textline orientation ON, doc orientation ON, DropScore 0 (the defaults) — and Word grouping so
        // lines[] correspond 1:1 to raw detected boxes, matching what the Python pipeline emits (the
        // default Line grouping merges same-row boxes, which Python never does).
        var options = RecognitionOptions.Default with { Grouping = TextGrouping.Word };

        var predictSw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.ExtractTextFromImage(imagePath, languages, options);
        predictSw.Stop();

        // Python-parity emission order: the engine's own FullText uses a column-aware reading order (a
        // deliberate C# feature, left untouched here); for byte-comparable parity JSON the lines are
        // re-sorted with the exact Python sorted_boxes convention — stable sort by the quad's top-left
        // point (y, then x) plus one adjacent bubble pass swapping pairs whose y differs by < 10px —
        // and fullText below is those sorted lines joined with newlines, independent of engine grouping.
        // When the doc-orientation stage uprighted the page, the emitted quads are in the ORIGINAL
        // image's frame (a C# deviation; Python reports the rotated frame) but the sort KEY is each
        // quad's first point rotated forward into the uprighted frame — so line order matches Python's.
        int appliedRotation = (360 - result.DetectedOrientation) % 360;
        List<PaddleOcrNet.Models.OcrLine> parityLines;
        if (appliedRotation != 0 && result.SourceWidth > 0 && result.SourceHeight > 0)
        {
            parityLines = result.Lines.ToList();
            PaddleOcrNet.Internal.SortedBoxes.Sort(parityLines, l => PaddleOcrNet.Internal.Geometry.OrientationMapper.RotatePoint(
                PaddleOcrNet.Internal.SortedBoxes.KeyPoint(l), appliedRotation, result.SourceWidth, result.SourceHeight));
        }
        else
        {
            parityLines = PaddleOcrNet.Internal.SortedBoxes.SortLines(result.Lines);
        }

        w.WriteStartObject();
        w.WriteString("image", imagePath);
        w.WriteString("pipeline", "ocr");
        w.WriteString("engine", "csharp-ort");

        w.WriteStartObject("params");
        w.WriteString("variant", variant);
        w.WriteString("lang", string.Join(",", langCodes));
        w.WriteBoolean("use_textline_orientation", options.UseTextLineOrientation);
        w.WriteNumber("drop_score", options.DropScore);
        w.WriteBoolean("doc_preprocessing", options.UseDocOrientation || options.UseDocUnwarp);
        w.WriteNumber("detected_orientation", result.DetectedOrientation);
        w.WriteString("grouping", options.Grouping.ToString());
        w.WriteString("provider", service.ActiveExecutionProvider.ToString());
        w.WriteEndObject();

        w.WriteStartArray("lines");
        foreach (var line in parityLines)
        {
            WriteLine(w, line);
        }
        w.WriteEndArray();

        w.WriteString("fullText", string.Join("\n", parityLines.Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));

        WriteTiming(w, initSeconds, predictSw.Elapsed.TotalSeconds);
        w.WriteEndObject();
    }

    private static async Task RunStructureAsync(
        PaddleOcrService service, System.Text.Json.Utf8JsonWriter w, string imagePath,
        string variant, bool noFormulas, string tableModel, double initSeconds)
    {
        // tables/seals ON, doc preprocessing OFF, lang ch; formulas and the table model are CLI-selectable
        // so the emitted JSON can mirror the Python GT configuration (formulas OFF there).
        var options = PaddleOcrNet.Structure.StructureOptions.Default with
        {
            RecognizeFormulas = !noFormulas,
            TableModel = tableModel == "slanext"
                ? PaddleOcrNet.Structure.TableRecognitionModel.SlaNeXt
                : PaddleOcrNet.Structure.TableRecognitionModel.SlanetPlus,
        };

        var predictSw = System.Diagnostics.Stopwatch.StartNew();
        var doc = await service.AnalyzeDocumentAsync(imagePath, options);
        predictSw.Stop();

        var ordered = doc.Blocks.OrderBy(b => b.Order).ToList();

        w.WriteStartObject();
        w.WriteString("image", imagePath);
        w.WriteString("pipeline", "structure");
        w.WriteString("engine", "csharp-ort");

        w.WriteStartObject("params");
        w.WriteString("variant", variant);
        w.WriteBoolean("use_doc_orientation_classify", options.UseDocOrientation);
        w.WriteBoolean("use_doc_unwarping", options.UseUnwarp);
        w.WriteBoolean("use_table_recognition", options.RecognizeTables);
        w.WriteBoolean("use_formula_recognition", options.RecognizeFormulas);
        w.WriteBoolean("use_seal_recognition", options.RecognizeSeals);
        w.WriteString("layout_model", options.LayoutModel.ToString());
        w.WriteString("table_model", options.TableModel.ToString());
        w.WriteString("provider", service.ActiveExecutionProvider.ToString());
        w.WriteEndObject();

        // Aggregate the underlying OCR lines from the blocks, then emit them PAGE-LEVEL in the exact
        // Python sorted_boxes order (the Python GT's `lines` is overall_ocr_res, not block-grouped), so
        // fullText/lines compare block-layout-independently.
        var collected = new List<PaddleOcrNet.Models.OcrLine>();
        foreach (var block in ordered)
        {
            if (block.Lines is { Count: > 0 } blockLines)
                collected.AddRange(blockLines);
        }
        var lines = PaddleOcrNet.Internal.SortedBoxes.SortLines(collected);

        w.WriteStartArray("lines");
        foreach (var line in lines)
        {
            WriteLine(w, line);
        }
        w.WriteEndArray();

        w.WriteString("fullText", string.Join("\n", lines.Select(l => l.Text).Where(t => !string.IsNullOrEmpty(t))));

        w.WriteString("markdown", doc.ToMarkdown());

        w.WriteStartArray("tables");
        foreach (var block in ordered)
        {
            if (block.Type == PaddleOcrNet.Structure.StructureBlockType.Table && !string.IsNullOrEmpty(block.TableHtml))
                w.WriteStringValue(block.TableHtml);
        }
        w.WriteEndArray();

        w.WriteStartArray("blocks");
        foreach (var block in ordered)
        {
            w.WriteStartObject();
            w.WriteString("type", PaddleLabel(block.Type));
            w.WriteStartArray("bbox");
            w.WriteNumberValue(block.Bounds.MinX);
            w.WriteNumberValue(block.Bounds.MinY);
            w.WriteNumberValue(block.Bounds.MaxX);
            w.WriteNumberValue(block.Bounds.MaxY);
            w.WriteEndArray();
            w.WriteNumber("order", block.Order);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        WriteTiming(w, initSeconds, predictSw.Elapsed.TotalSeconds);
        w.WriteEndObject();
    }

    private static void WriteLine(System.Text.Json.Utf8JsonWriter w, PaddleOcrNet.Models.OcrLine line)
    {
        w.WriteStartObject();
        w.WriteStartArray("poly");
        foreach (var (x, y) in QuadPoints(line))
        {
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(x, 2));
            w.WriteNumberValue(Math.Round(y, 2));
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteString("text", line.Text);
        w.WriteNumber("score", Math.Round(line.Confidence, 6));
        w.WriteEndObject();
    }

    /// <summary>
    /// The 4-point polygon for a line: its quad when the detector produced exactly four points,
    /// otherwise the axis-aligned box corners clockwise from top-left.
    /// </summary>
    private static IEnumerable<(double X, double Y)> QuadPoints(PaddleOcrNet.Models.OcrLine line)
    {
        if (line.BoundingPolygon is { Count: 4 } poly)
        {
            foreach (var p in poly) yield return (p.X, p.Y);
            yield break;
        }
        var b = line.BoundingBox;
        yield return (b.MinX, b.MinY);
        yield return (b.MaxX, b.MinY);
        yield return (b.MaxX, b.MaxY);
        yield return (b.MinX, b.MaxY);
    }

    private static void WriteTiming(System.Text.Json.Utf8JsonWriter w, double initSeconds, double predictSeconds)
    {
        w.WriteStartObject("timing");
        w.WriteNumber("init_s", Math.Round(initSeconds, 3));
        w.WriteNumber("predict_s", Math.Round(predictSeconds, 3));
        w.WriteEndObject();
    }

    /// <summary>
    /// Maps the C# block-type enum onto the PaddleOCR layout label vocabulary the Python side emits,
    /// so reading-order sequences are comparable (see tools/parity/README.md).
    /// </summary>
    private static string PaddleLabel(PaddleOcrNet.Structure.StructureBlockType type) => type switch
    {
        PaddleOcrNet.Structure.StructureBlockType.Text or PaddleOcrNet.Structure.StructureBlockType.Paragraph => "text",
        PaddleOcrNet.Structure.StructureBlockType.Title => "paragraph_title",
        PaddleOcrNet.Structure.StructureBlockType.DocTitle => "doc_title",
        PaddleOcrNet.Structure.StructureBlockType.List => "content",
        PaddleOcrNet.Structure.StructureBlockType.Table => "table",
        PaddleOcrNet.Structure.StructureBlockType.TableCaption => "table_title",
        PaddleOcrNet.Structure.StructureBlockType.Figure => "image",
        PaddleOcrNet.Structure.StructureBlockType.FigureCaption => "figure_title",
        PaddleOcrNet.Structure.StructureBlockType.Formula => "formula",
        PaddleOcrNet.Structure.StructureBlockType.FormulaNumber => "formula_number",
        PaddleOcrNet.Structure.StructureBlockType.Seal => "seal",
        PaddleOcrNet.Structure.StructureBlockType.Chart => "chart",
        PaddleOcrNet.Structure.StructureBlockType.Header => "header",
        PaddleOcrNet.Structure.StructureBlockType.Footer => "footer",
        PaddleOcrNet.Structure.StructureBlockType.Reference => "reference",
        PaddleOcrNet.Structure.StructureBlockType.Footnote => "footnote",
        PaddleOcrNet.Structure.StructureBlockType.PageNumber => "number",
        PaddleOcrNet.Structure.StructureBlockType.Abstract => "abstract",
        PaddleOcrNet.Structure.StructureBlockType.Algorithm => "algorithm",
        PaddleOcrNet.Structure.StructureBlockType.Aside => "aside_text",
        _ => "unknown",
    };
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
//       Languages         = new[] { OcrLanguage.English },
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
