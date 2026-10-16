using System.IO.Compression;
using System.Text;
using PaddleOcrNet.Office;
using PaddleOcrNet.Office.Internal;
using Xunit;

namespace PaddleOcrNet.Tests;

public class WordPresentationReaderTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    [Fact]
    public void Word_preserves_native_text_merges_headers_and_images_without_table_paragraph_duplication()
    {
        using var stream = Archive(new Dictionary<string, string>
        {
            ["word/document.xml"] = $"""
                <w:document xmlns:w="{W}" xmlns:a="{A}" xmlns:r="{R}"><w:body>
                <w:p><w:r><w:t>Hello</w:t><w:tab/><w:t>world</w:t><w:br/><w:t>next</w:t></w:r><w:del><w:r><w:t>deleted</w:t></w:r></w:del><w:r><w:drawing><a:blip r:embed="img"/></w:drawing></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>Merged</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/><w:vMerge/></w:tcPr><w:p/></w:tc></w:tr></w:tbl>
                <w:sectPr><w:headerReference r:id="head" w:type="default"/><w:footerReference r:id="foot" w:type="default"/></w:sectPr></w:body></w:document>
                """,
            ["word/_rels/document.xml.rels"] = $"""
                <Relationships xmlns="{Rel}"><Relationship Id="img" Type="{R}/image" Target="media/image.png"/><Relationship Id="head" Type="{R}/header" Target="header1.xml"/><Relationship Id="foot" Type="{R}/footer" Target="footer1.xml"/></Relationships>
                """,
            ["word/header1.xml"] = $"<w:hdr xmlns:w=\"{W}\"><w:p><w:r><w:t>Header</w:t></w:r></w:p></w:hdr>",
            ["word/footer1.xml"] = $"<w:ftr xmlns:w=\"{W}\"><w:p><w:r><w:t>Footer</w:t></w:r></w:p></w:ftr>",
            ["word/media/image.png"] = "image-placeholder",
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        var sections = WordPresentationReader.ReadWord(package, "word/document.xml", CancellationToken.None);
        Assert.Equal(3, sections.Count);
        Assert.Equal("Hello\tworld\nnext", Assert.Single(sections[0].Blocks, b => b.Kind == OfficeContentKind.Paragraph).Text);
        Assert.Equal("word/media/image.png", Assert.Single(sections[0].Blocks, b => b.Kind == OfficeContentKind.Image).ImagePart);
        var cell = Assert.Single(Assert.Single(sections[0].Blocks, b => b.Kind == OfficeContentKind.Table).Cells);
        Assert.Equal("Merged", cell.Text);
        Assert.Equal(2, cell.RowSpan);
        Assert.Equal(2, cell.ColumnSpan);
        Assert.Contains("Merged", sections[0].FullText);
        Assert.Equal("Header", Assert.Single(sections[1].Blocks).Text);
        Assert.Equal("Footer", Assert.Single(sections[2].Blocks).Text);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Presentation_respects_slide_relationship_order_group_text_and_merged_table_cells()
    {
        string slide = $"""
            <p:sld xmlns:p="{P}" xmlns:a="{A}" xmlns:r="{R}"><p:cSld><p:spTree>
            <p:grpSp><p:sp><p:txBody><a:p><a:r><a:t>Grouped</a:t></a:r><a:br/><a:r><a:t>text</a:t></a:r></a:p></p:txBody></p:sp></p:grpSp>
            <p:graphicFrame><a:graphic><a:graphicData><a:tbl><a:tr><a:tc gridSpan="2"><a:txBody><a:p><a:r><a:t>Heading</a:t></a:r></a:p></a:txBody></a:tc><a:tc hMerge="1"><a:txBody><a:p/></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>
            <p:pic><p:blipFill><a:blip r:embed="img"/></p:blipFill></p:pic>
            </p:spTree></p:cSld></p:sld>
            """;
        using var stream = Archive(new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{R}\"><p:sldIdLst><p:sldId id=\"2\" r:id=\"s2\"/><p:sldId id=\"1\" r:id=\"s1\"/></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{Rel}\"><Relationship Id=\"s1\" Type=\"{R}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"s2\" Type=\"{R}/slide\" Target=\"slides/slide2.xml\"/></Relationships>",
            ["ppt/slides/slide1.xml"] = slide,
            ["ppt/slides/slide2.xml"] = slide,
            ["ppt/slides/_rels/slide1.xml.rels"] = $"<Relationships xmlns=\"{Rel}\"><Relationship Id=\"img\" Type=\"{R}/image\" Target=\"../media/a.png\"/></Relationships>",
            ["ppt/slides/_rels/slide2.xml.rels"] = $"<Relationships xmlns=\"{Rel}\"><Relationship Id=\"img\" Type=\"{R}/image\" Target=\"../media/a.png\"/></Relationships>",
            ["ppt/media/a.png"] = "image-placeholder",
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        var sections = WordPresentationReader.ReadPresentation(package, "ppt/presentation.xml", CancellationToken.None);
        Assert.Equal("ppt/slides/slide2.xml", sections[0].SourcePart);
        Assert.Equal("ppt/slides/slide1.xml", sections[1].SourcePart);
        Assert.Equal("Grouped\ntext", sections[0].Blocks[0].Text);
        var cell = Assert.Single(Assert.Single(sections[0].Blocks, b => b.Kind == OfficeContentKind.Table).Cells);
        Assert.Equal("Heading", cell.Text);
        Assert.Equal(2, cell.ColumnSpan);
        Assert.Contains("Heading", sections[0].FullText);
        Assert.Equal("ppt/media/a.png", Assert.Single(sections[0].Blocks, b => b.Kind == OfficeContentKind.Image).ImagePart);
    }

    [Fact]
    public void Word_compatibility_fallback_is_read_once_and_deleted_table_rows_are_omitted()
    {
        using var stream = Archive(new Dictionary<string, string>
        {
            ["word/document.xml"] = $"""
                <w:document xmlns:w="{W}" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"><w:body>
                <mc:AlternateContent><mc:Choice Requires="w"><w:p><w:r><w:t>duplicate</w:t></w:r></w:p></mc:Choice><mc:Fallback><w:p><w:r><w:t>fallback</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent>
                <w:tbl><w:tr><w:trPr><w:del/></w:trPr><w:tc><w:p><w:r><w:t>deleted row</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>kept</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                </w:body></w:document>
                """,
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        var blocks = Assert.Single(WordPresentationReader.ReadWord(package, "word/document.xml", CancellationToken.None)).Blocks;
        Assert.Equal("fallback", Assert.Single(blocks, b => b.Kind == OfficeContentKind.Paragraph).Text);
        Assert.Equal("kept", Assert.Single(Assert.Single(blocks, b => b.Kind == OfficeContentKind.Table).Cells).Text);
    }

    private static MemoryStream Archive(Dictionary<string, string> files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Strict_word_namespaces_are_supported_without_accepting_spoofed_namespaces()
    {
        string xml = "<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\"><w:body><w:p><w:r><w:t>Strict text</w:t></w:r></w:p></w:body></w:document>";
        using var stream = Archive(new Dictionary<string, string> { ["word/document.xml"] = xml });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        Assert.Equal("Strict text", Assert.Single(Assert.Single(WordPresentationReader.ReadWord(package, "word/document.xml", CancellationToken.None)).Blocks).Text);
        using var spoof = Archive(new Dictionary<string, string> { ["word/document.xml"] = xml.Replace("http://purl.oclc.org/ooxml/wordprocessingml/main", "urn:spoof", StringComparison.Ordinal) });
        using var bad = new OfficePackage(spoof, new OfficeDocumentOptions(), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => WordPresentationReader.ReadWord(bad, "word/document.xml", CancellationToken.None));
    }

    [Fact]
    public void Strict_presentation_relationships_and_drawing_namespaces_are_supported()
    {
        const string sp = "http://purl.oclc.org/ooxml/presentationml/main", sa = "http://purl.oclc.org/ooxml/drawingml/main", sr = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        using var stream = Archive(new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{sp}\" xmlns:r=\"{sr}\"><p:sldIdLst><p:sldId r:id=\"slide\"/></p:sldIdLst></p:presentation>",
            ["ppt/_rels/presentation.xml.rels"] = $"<Relationships xmlns=\"{Rel}\"><Relationship Id=\"slide\" Type=\"{sr}/slide\" Target=\"slides/s.xml\"/></Relationships>",
            ["ppt/slides/s.xml"] = $"<p:sld xmlns:p=\"{sp}\" xmlns:a=\"{sa}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Strict slide</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>",
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        Assert.Equal("Strict slide", Assert.Single(Assert.Single(WordPresentationReader.ReadPresentation(package, "ppt/presentation.xml", CancellationToken.None)).Blocks).Text);
    }

    [Fact]
    public void Native_text_budget_counts_paragraph_separators_inside_table_cells()
    {
        string xml = $"<w:document xmlns:w=\"{W}\"><w:body><w:tbl><w:tr><w:tc><w:p><w:r><w:t>a</w:t></w:r></w:p><w:p><w:r><w:t>b</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>";
        using var stream = Archive(new Dictionary<string, string> { ["word/document.xml"] = xml });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions { MaxTextCharacters = 2 }, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => WordPresentationReader.ReadWord(package, "word/document.xml", CancellationToken.None));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("header")]
    [InlineData("footer")]
    [InlineData("slide")]
    public void Content_references_reject_misbound_relationship_types_even_with_valid_target_parts(string kind)
    {
        bool presentation = kind == "slide";
        string main = presentation ? "ppt/presentation.xml" : "word/document.xml";
        string relationships = presentation ? "ppt/_rels/presentation.xml.rels" : "word/_rels/document.xml.rels";
        string target = presentation ? "ppt/target.xml" : "word/target.xml";
        string body = kind switch
        {
            "image" => "<w:p><w:r><w:drawing><a:blip r:embed=\"bad\"/></w:drawing></w:r></w:p>",
            "header" => "<w:sectPr><w:headerReference r:id=\"bad\"/></w:sectPr>",
            _ => "<w:sectPr><w:footerReference r:id=\"bad\"/></w:sectPr>",
        };
        string mainXml = presentation
            ? $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{R}\"><p:sldIdLst><p:sldId r:id=\"bad\"/></p:sldIdLst></p:presentation>"
            : $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\"><w:body>{body}</w:body></w:document>";
        string targetXml = kind switch
        {
            "header" => $"<w:hdr xmlns:w=\"{W}\"/>",
            "footer" => $"<w:ftr xmlns:w=\"{W}\"/>",
            "slide" => $"<p:sld xmlns:p=\"{P}\"><p:cSld><p:spTree/></p:cSld></p:sld>",
            _ => "image-placeholder",
        };
        using var stream = Archive(new Dictionary<string, string>
        {
            [main] = mainXml, [target] = targetXml,
            [relationships] = $"<Relationships xmlns=\"{Rel}\"><Relationship Id=\"bad\" Type=\"{R}/hyperlink\" Target=\"target.xml\"/></Relationships>",
        });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions(), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => presentation
            ? WordPresentationReader.ReadPresentation(package, main, CancellationToken.None)
            : WordPresentationReader.ReadWord(package, main, CancellationToken.None));
    }

    [Fact]
    public void Word_table_full_text_uses_sparse_row_column_order_without_expanding_spans()
    {
        string xml = $"""
            <w:document xmlns:w="{W}"><w:body><w:tbl>
            <w:tr><w:tc><w:tcPr><w:gridSpan w:val="3"/></w:tcPr><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr>
            </w:tbl></w:body></w:document>
            """;
        using var stream = Archive(new Dictionary<string, string> { ["word/document.xml"] = xml });
        using var package = new OfficePackage(stream, new OfficeDocumentOptions { MaxTextCharacters = 5 }, CancellationToken.None);
        var section = Assert.Single(WordPresentationReader.ReadWord(package, "word/document.xml", CancellationToken.None));
        Assert.Equal("A\tB\nC", Assert.Single(section.Blocks).Text);
        Assert.Equal("A\tB\nC", section.FullText);
    }
}
