using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using PaddleOcrNet.Office;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Export;
using Xunit;

namespace PaddleOcrNet.Tests;

public class OfficeInputTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Type = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    internal static MemoryStream Package(string body = "Native text", bool image = false,
        string imageTarget = "media/picture.png", bool external = false,
        string contentType = Type, string? rawDocument = null, string rootTarget = "word/document.xml")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(zip, "[Content_Types].xml", $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"{contentType}\"/></Types>");
            Text(zip, "_rels/.rels", $"<Relationships xmlns=\"{P}\"><Relationship Id=\"main\" Type=\"{R}/officeDocument\" Target=\"{rootTarget}\"/></Relationships>");
            string drawing = image ? "<w:r><w:drawing><a:blip r:embed=\"pic\"/></w:drawing></w:r>" : "";
            Text(zip, "word/document.xml", rawDocument ?? $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><w:body><w:p><w:r><w:t>{body}</w:t></w:r>{drawing}</w:p></w:body></w:document>");
            if (image)
            {
                Text(zip, "word/_rels/document.xml.rels", $"<Relationships xmlns=\"{P}\"><Relationship Id=\"pic\" Type=\"{R}/image\" Target=\"{imageTarget}\" {(external ? "TargetMode=\"External\"" : "")}/></Relationships>");
                using var png = new Image<Rgb24>(12, 7);
                using var output = zip.CreateEntry("word/media/picture.png").Open();
                png.SaveAsPng(output);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static void Text(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Public_reader_roundtrips_existing_Word_and_Excel_exports(bool word)
    {
        var structure = new StructureResult
        {
            Blocks = new[] { new StructureBlock(StructureBlockType.Table, new OcrBoundingBox(0, 0, 200, 100), 0,
                TableHtml: "<table><tr><td>Name</td><td>Value</td></tr><tr><td>Alice</td><td>97</td></tr></table>") },
        };
        using var input = new MemoryStream(word ? structure.ToDocx() : structure.ToXlsx());
        var result = OfficeDocumentReader.Read(input);
        Assert.Equal(word ? OfficeDocumentFormat.Docx : OfficeDocumentFormat.Xlsx, result.Format);
        Assert.Contains("Alice", result.FullText);
        Assert.Contains("97", result.FullText);
        Assert.Contains(result.Sections.SelectMany(s => s.Blocks).SelectMany(b => b.Cells), c => c.Text == "Alice");
    }

    [Fact]
    public void Public_presentation_reader_uses_slide_relationship_order()
    {
        using var input = new MemoryStream();
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        using (var zip = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/></Types>");
            Text(zip, "_rels/.rels", $"<Relationships xmlns=\"{P}\"><Relationship Id=\"main\" Type=\"{R}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
            Text(zip, "ppt/presentation.xml", $"<p:presentation xmlns:p=\"{p}\" xmlns:r=\"{R}\"><p:sldIdLst><p:sldId r:id=\"second\"/><p:sldId r:id=\"first\"/></p:sldIdLst></p:presentation>");
            Text(zip, "ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{P}\"><Relationship Id=\"first\" Type=\"{R}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"second\" Type=\"{R}/slide\" Target=\"slides/slide2.xml\"/></Relationships>");
            foreach (var number in new[] { 1, 2 })
                Text(zip, $"ppt/slides/slide{number}.xml", $"<p:sld xmlns:p=\"{p}\" xmlns:a=\"{a}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Slide {number}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>");
        }
        input.Position = 0;
        var result = OfficeDocumentReader.Read(input);
        Assert.Equal(OfficeDocumentFormat.Pptx, result.Format);
        Assert.Equal("Slide 2\n\nSlide 1", result.FullText);
        Assert.Equal(new[] { 1, 2 }, result.Sections.Select(s => s.Index));
    }

    [Fact]
    public async Task Cancellation_after_image_inference_and_OCR_text_limits_fail_without_partial_result()
    {
        using var input = Package(image: true);
        using var cancellation = new CancellationTokenSource();
        using var canceledService = new ServiceFakeOcrService { OnImage = _ => { cancellation.Cancel(); return ServiceFakeOcrService.Result("OCR"); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledService.ExtractTextFromOfficeAsync(input, cancellationToken: cancellation.Token));
        input.Position = 0;
        using var largeTextService = new ServiceFakeOcrService { OnImage = _ => ServiceFakeOcrService.Result(new string('X', 50)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => largeTextService.ExtractTextFromOfficeAsync(input, new() { MaxTextCharacters = 20 }));
    }

    [Fact]
    public void Native_reader_detects_package_and_source_generated_json_roundtrips()
    {
        using var input = Package();
        var result = OfficeDocumentReader.Read(input);
        Assert.True(input.CanRead);
        Assert.Equal(OfficeDocumentFormat.Docx, result.Format);
        Assert.Equal("Native text", result.FullText);
        Assert.Equal("word/document.xml", Assert.Single(result.Sections).SourcePart);
        var restored = JsonSerializer.Deserialize(result.ToJson(), PaddleOcrNet.Models.PaddleOcrJsonContext.Default.OfficeDocumentResult);
        Assert.Equal(result.FullText, restored!.FullText);
    }

    [Fact]
    public async Task Image_ocr_is_separate_evidence_and_native_only_never_calls_ocr()
    {
        int calls = 0;
        using var service = new ServiceFakeOcrService { OnImage = image => { calls++; return ServiceFakeOcrService.Result($"Image {image.Width}x{image.Height}", image.Width, image.Height); } };
        using var input = Package(image: true);
        var result = await service.ExtractTextFromOfficeAsync(input);
        Assert.True(input.CanRead);
        Assert.Equal(1, calls);
        Assert.Equal("Native text\nImage 12x7", result.FullText);
        var image = Assert.Single(result.Sections[0].Blocks, b => b.Kind == OfficeContentKind.Image);
        Assert.Equal("word/media/picture.png", image.ImagePart);
        Assert.Equal(12, image.ImageOcr!.SourceWidth);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("image text is absent"));
        input.Position = 0;
        var native = await service.ExtractTextFromOfficeAsync(input, new() { RecognizeEmbeddedImages = false, MaxImages = 0 });
        Assert.Equal(1, calls);
        Assert.Equal("Native text", native.FullText);
        Assert.Null(Assert.Single(native.Sections[0].Blocks, b => b.Kind == OfficeContentKind.Image).ImageOcr);
    }

    [Fact]
    public async Task External_images_are_reported_without_fetching_or_ocr()
    {
        using var input = Package(image: true, imageTarget: "https://invalid.example/image.png", external: true);
        using var service = new ServiceFakeOcrService { OnImage = _ => throw new Exception("Unexpected OCR") };
        var result = await service.ExtractTextFromOfficeAsync(input);
        Assert.Equal("Native text", result.FullText);
        Assert.Contains(result.Warnings, w => w.Contains("External", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../../escape.png")]
    [InlineData("%2e%2e/%2e%2e/escape.png")]
    [InlineData("https://invalid.example/image.png")]
    [InlineData("media/missing.png")]
    public void Invalid_internal_targets_fail_explicitly(string target)
    {
        using var input = Package(image: true, imageTarget: target);
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input));
    }

    [Fact]
    public void Rejects_dtd_depth_duplicate_parts_and_macro_formats()
    {
        using var dtd = Package(rawDocument: $"<!DOCTYPE x [<!ENTITY x 'secret'>]><w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>");
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(dtd));
        using var depth = Package(rawDocument: "<root>" + string.Concat(Enumerable.Repeat("<a>", 140)) + string.Concat(Enumerable.Repeat("</a>", 140)) + "</root>");
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(depth));
        using var duplicate = Package();
        using (var zip = new ZipArchive(duplicate, ZipArchiveMode.Update, leaveOpen: true)) Text(zip, "WORD/document.xml", "duplicate");
        duplicate.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(duplicate));
        using var macro = Package(contentType: "application/vnd.ms-word.document.macroEnabled.main+xml");
        Assert.Throws<NotSupportedException>(() => OfficeDocumentReader.Read(macro));
    }

    [Fact]
    public async Task Bounds_fail_before_image_ocr_and_cancellation_is_observed()
    {
        using var input = Package(image: true);
        using var service = new ServiceFakeOcrService { OnImage = _ => throw new Exception("Unexpected OCR") };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractTextFromOfficeAsync(input, new() { MaxImages = 0 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxInputBytes = input.Length - 1 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxEntries = 1 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxPartBytes = 10 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxExpandedBytes = 10 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxXmlCharacters = 10 }));
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => OfficeDocumentReader.Read(input, new() { MaxTextCharacters = 2 }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractTextFromOfficeAsync(input, cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task Nonseekable_stream_and_path_inputs_work_and_keep_caller_stream_open()
    {
        using var source = Package();
        using var nonseek = new Nonseekable(source);
        using var service = new ServiceFakeOcrService();
        Assert.Equal("Native text", (await service.ExtractTextFromOfficeAsync(nonseek)).FullText);
        Assert.True(source.CanRead);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            await File.WriteAllBytesAsync(path, source.ToArray());
            Assert.Equal("Native text", OfficeDocumentReader.Read(path).FullText);
            Assert.Equal("Native text", (await service.ExtractTextFromOfficeAsync(path)).FullText);
        }
        finally { File.Delete(path); }
    }

    private sealed class Nonseekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
