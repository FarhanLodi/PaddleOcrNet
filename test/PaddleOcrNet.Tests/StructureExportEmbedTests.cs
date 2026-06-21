using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using PaddleOcrNet.Models;
using PaddleOcrNet.Structure;
using PaddleOcrNet.Structure.Export;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the image-aware export overloads
/// <see cref="StructureHtmlExporter.ToHtml(StructureResult, Image{Rgb24}, string?)"/> and
/// <see cref="StructureDocxExporter.ToDocx(StructureResult, Image{Rgb24})"/>. These overloads embed the actual
/// pixels of figure/chart/seal regions (HTML: a base64 <c>data:image/png</c> <c>&lt;img&gt;</c>; DOCX: a
/// <c>word/media/image*.png</c> part + inline drawing) and render recovered formula LaTeX as native Word
/// equations (OMML). The tests assert the embedding wiring structurally (substring / ZIP-entry presence) and
/// verify the no-image overloads keep their original placeholder/text behavior.
/// </summary>
public class StructureExportEmbedTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    // A 300x200 white canvas; the figure block (10,10)-(200,150) crops cleanly inside it.
    private static Image<Rgb24> Canvas() => new(300, 200, new Rgb24(255, 255, 255));

    private static StructureResult Build(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 300, SourceHeight = 200 };

    // A figure region (embeddable pixels) plus a formula (LaTeX -> OMML in the image-aware DOCX path).
    private static StructureResult Sample() => Build(
        new StructureBlock(StructureBlockType.Figure, Box(10, 10, 200, 150), Order: 0, Text: "Figure 1"),
        new StructureBlock(StructureBlockType.Formula, Box(10, 160, 200, 195), Order: 1, Latex: "\\frac{a}{b}"));

    private static IReadOnlyDictionary<string, string> ReadTextParts(byte[] package)
    {
        var parts = new Dictionary<string, string>();
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            // Read only the XML/rels text parts; binary media parts are checked separately by name.
            if (entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)) continue;
            using var s = entry.Open();
            using var reader = new StreamReader(s);
            parts[entry.FullName] = reader.ReadToEnd();
        }
        return parts;
    }

    private static string[] EntryNames(byte[] package)
    {
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    // ---- HTML ------------------------------------------------------------------------------------------

    [Fact]
    public void ToHtml_with_image_embeds_figure_pixels_as_data_uri()
    {
        using var image = Canvas();
        var html = Sample().ToHtml(image);

        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("<img src=\"data:image/png;base64,", html);
        // The recovered caption survives as a <figcaption>.
        Assert.Contains("Figure 1", html);
    }

    [Fact]
    public void ToHtml_without_image_keeps_the_placeholder()
    {
        // Unchanged behavior: no <img>, the bbox placeholder figure is emitted instead.
        var html = Sample().ToHtml();

        Assert.DoesNotContain("data:image/png;base64,", html);
        Assert.Contains("data-type=\"Figure\"", html);
        Assert.Contains("data-bbox=\"10 10 200 150\"", html);
    }

    [Fact]
    public void ToHtml_with_image_stays_well_formed()
    {
        using var image = Canvas();
        var html = Sample().ToHtml(image);

        int start = html.IndexOf("<body>", StringComparison.Ordinal);
        int end = html.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = html.Substring(start, (end - start) + "</body>".Length);
        XDocument.Parse(body); // throws if not well-formed
    }

    // ---- DOCX ------------------------------------------------------------------------------------------

    [Fact]
    public void ToDocx_with_image_embeds_a_media_part_wired_to_document_and_rels()
    {
        using var image = Canvas();
        var bytes = Sample().ToDocx(image);

        var names = EntryNames(bytes);
        // A PNG media part exists.
        Assert.Contains(names, n => n.StartsWith("word/media/image", StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal));

        var parts = ReadTextParts(bytes);

        // The relationship part wires rId1 -> media/image1.png with the image relationship type.
        var rels = parts["word/_rels/document.xml.rels"];
        Assert.Contains("Id=\"rId1\"", rels);
        Assert.Contains("Target=\"media/image1.png\"", rels);
        Assert.Contains("/relationships/image", rels);

        // The content types declare the png default extension.
        Assert.Contains("Extension=\"png\"", parts["[Content_Types].xml"]);
        Assert.Contains("image/png", parts["[Content_Types].xml"]);

        // The document body references the relationship through a drawing/blip.
        var doc = parts["word/document.xml"];
        Assert.Contains("<w:drawing>", doc);
        Assert.Contains("r:embed=\"rId1\"", doc);
    }

    [Fact]
    public void ToDocx_with_image_renders_formula_as_omml()
    {
        using var image = Canvas();
        var doc = ReadTextParts(Sample().ToDocx(image))["word/document.xml"];

        // The formula becomes a native Word equation rather than $$…$$ text.
        Assert.Contains("<m:oMath", doc);
    }

    [Fact]
    public void ToDocx_with_image_parts_are_well_formed_xml()
    {
        using var image = Canvas();
        var parts = ReadTextParts(Sample().ToDocx(image));
        foreach (var (name, xml) in parts)
        {
            var ex = Record.Exception(() => XDocument.Parse(xml));
            Assert.True(ex is null, $"Part '{name}' is not well-formed XML: {ex?.Message}");
        }
    }

    [Fact]
    public void ToDocx_without_image_has_no_media_and_keeps_text_rendering()
    {
        var bytes = Sample().ToDocx();

        // Unchanged behavior: no media parts, no png content-type, no drawing/OMML.
        var names = EntryNames(bytes);
        Assert.DoesNotContain(names, n => n.StartsWith("word/media/", StringComparison.Ordinal));

        var parts = ReadTextParts(bytes);
        Assert.DoesNotContain("Extension=\"png\"", parts["[Content_Types].xml"]);

        var doc = parts["word/document.xml"];
        Assert.DoesNotContain("<w:drawing>", doc);
        Assert.DoesNotContain("<m:oMath", doc);
        // The formula and figure still appear as the original text placeholders.
        Assert.Contains("$$", doc);
        Assert.Contains("[Figure region]", doc);
    }

    [Fact]
    public void ToDocx_without_image_is_still_a_valid_package()
    {
        var parts = ReadTextParts(Sample().ToDocx());

        Assert.Contains("[Content_Types].xml", parts.Keys);
        Assert.Contains("word/document.xml", parts.Keys);
        Assert.Contains("word/_rels/document.xml.rels", parts.Keys);
        foreach (var (name, xml) in parts)
        {
            var ex = Record.Exception(() => XDocument.Parse(xml));
            Assert.True(ex is null, $"Part '{name}' is not well-formed XML: {ex?.Message}");
        }
    }
}
