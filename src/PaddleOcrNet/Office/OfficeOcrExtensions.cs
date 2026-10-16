using PaddleOcrNet.Office.Internal;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Office;

/// <summary>Native Office extraction plus sequential, guarded OCR for referenced embedded images.</summary>
public static class OfficeOcrExtensions
{
    /// <summary>Extracts native DOCX/XLSX/PPTX content and optionally OCRs embedded raster images.</summary>
    public static async Task<OfficeDocumentResult> ExtractTextFromOfficeAsync(this IPaddleOcrService service,
        string path, OfficeDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await service.ExtractTextFromOfficeAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads from the current stream position, leaving it open. Image decode/OCR failures propagate;
    /// no external relationship is fetched. OCR coordinates belong to each decoded image, not a page/slide.
    /// </summary>
    public static async Task<OfficeDocumentResult> ExtractTextFromOfficeAsync(this IPaddleOcrService service,
        Stream stream, OfficeDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        options ??= new();
        options.Validate();
        // Snapshot the mutable caller-owned language collection before any asynchronous work.
        options = options with { Languages = options.Languages.ToArray() };
        using var package = await OfficePackage.OpenAsync(stream, options, cancellationToken).ConfigureAwait(false);
        var result = OfficeDocumentReader.Read(package, cancellationToken);
        if (!options.RecognizeEmbeddedImages) return result;
        int images = result.Sections.SelectMany(s => s.Blocks).Count(b => b.Kind == OfficeContentKind.Image);
        if (images > options.MaxImages) throw new InvalidDataException("Office image OCR occurrence limit exceeded.");
        var sections = new List<OfficeDocumentSection>(result.Sections.Count);
        foreach (var section in result.Sections)
        {
            var blocks = new List<OfficeContentBlock>(section.Blocks.Count);
            foreach (var block in section.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.Kind != OfficeContentKind.Image) { blocks.Add(block); continue; }
                if (block.ImagePart is null) throw new InvalidDataException("Embedded image has no source part.");
                var bytes = package.ReadBytes(block.ImagePart);
                var settings = service is PaddleOcrService concrete ? concrete.LoadSettings : ImageLoadSettings.Default;
                using var image = GuardedImageLoader.LoadFirstFrame(bytes, settings, cancellationToken);
                var ocr = await service.ExtractTextFromImage(image, options.Languages, options.Recognition, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                package.AddText(ocr.FullText);
                blocks.Add(block with { Text = ocr.FullText, ImageOcr = ocr });
            }
            sections.Add(section with { Blocks = blocks.ToArray() });
        }
        return result with
        {
            Sections = sections.ToArray(),
            Warnings = package.Warnings.Where(w => w != "Embedded images are identified; image text is absent unless image OCR is requested through the service extension.").ToArray(),
        };
    }
}
