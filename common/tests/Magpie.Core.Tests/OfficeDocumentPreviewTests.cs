using System.IO.Compression;
using System.Text;
using System.Xml;
using Magpie.Core.Mail;

namespace Magpie.Core.Tests;

public sealed class OfficeDocumentPreviewTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [Fact]
    public void Word_preserves_headings_emphasis_and_table_structure()
    {
        using var package = Package(("word/document.xml", $"""
            <w:document xmlns:w="{W}"><w:body>
            <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
            <w:p><w:r><w:rPr><w:b/><w:i/><w:u w:val="single"/></w:rPr><w:t>Important</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>Merged heading</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>Amount</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>125</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """));
        var result = OfficeDocumentPreview.ReadDocument(package, "report.docx");
        Assert.Contains("<h2 class=\"left\">Overview</h2>", result.Html);
        Assert.Contains("<strong><em><u>Important</u></em></strong>", result.Html);
        Assert.Contains("<td colspan=\"2\">", result.Html);
        Assert.Contains("<table><tbody>", result.Html);
        Assert.Contains("Amount", result.PlainText);
        Assert.False(result.Truncated);
        Assert.True(package.CanRead);
    }

    [Fact]
    public void Word_uses_custom_outline_levels_without_trusting_style_names_as_markup()
    {
        using var package = Package(
            ("word/styles.xml", $"<w:styles xmlns:w=\"{W}\"><w:style w:styleId=\"Custom\"><w:pPr><w:outlineLvl w:val=\"2\"/></w:pPr></w:style></w:styles>"),
            ("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:pPr><w:pStyle w:val=\"Custom\"/></w:pPr><w:r><w:t>Custom heading</w:t></w:r></w:p></w:body></w:document>"));
        var result = OfficeDocumentPreview.ReadDocument(package, "custom.docx");
        Assert.Contains("<h3 class=\"left\">Custom heading</h3>", result.Html);
    }

    [Fact]
    public void Office_text_and_names_are_encoded_and_external_resources_are_not_emitted()
    {
        using var package = Package(
            ("word/document.xml", $"""
                <w:document xmlns:w="{W}" xmlns:r="{R}"><w:body><w:p>
                <w:r><w:t>&lt;script&gt;bad()&lt;/script&gt;&amp; safe</w:t></w:r>
                <w:hyperlink r:id="external"><w:r><w:t>Visible link text</w:t></w:r></w:hyperlink>
                </w:p></w:body></w:document>
                """),
            ("word/_rels/document.xml.rels", "<Relationships><Relationship Id=\"external\" Target=\"https://invalid.example/private\" TargetMode=\"External\"/></Relationships>"));
        var result = OfficeDocumentPreview.ReadDocument(package, "<img onerror=bad()>.docx");
        Assert.Contains("&lt;script&gt;bad()&lt;/script&gt;&amp; safe", result.Html);
        Assert.Contains("&lt;img onerror=bad()&gt;.docx", result.Html);
        Assert.DoesNotContain("<script", result.Html);
        Assert.DoesNotContain("<img", result.Html);
        Assert.DoesNotContain("https://invalid.example/private", result.Html);
        Assert.DoesNotContain("href=", result.Html);
        Assert.Contains("default-src 'none'", result.Html);
        Assert.Contains("base-uri 'none'", result.Html);
        Assert.Contains("form-action 'none'", result.Html);
        Assert.Contains("<script>bad()</script>& safe", result.PlainText);
    }

    [Fact]
    public void Excel_preserves_sheet_names_order_and_empty_column_positions()
    {
        using var package = Package(
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"First named sheet\" r:id=\"second\"/><sheet name=\"Later sheet\" r:id=\"first\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", Relationships(("first", "worksheet", "worksheets/sheet1.xml"), ("second", "worksheet", "worksheets/sheet2.xml"))),
            ("xl/worksheets/sheet1.xml", Worksheet("<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Later value</t></is></c></row>")),
            ("xl/worksheets/sheet2.xml", Worksheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Left</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>Right</t></is></c></row>")));
        var result = OfficeDocumentPreview.ReadDocument(package, "budget.xlsx");
        Assert.True(result.Html.IndexOf("First named sheet", StringComparison.Ordinal) < result.Html.IndexOf("Later sheet", StringComparison.Ordinal));
        Assert.Contains("<td>Left</td><td></td><td>Right</td>", result.Html);
        Assert.Contains("<th scope=\"col\">C</th>", result.Html);
        Assert.Contains("<th scope=\"row\">3</th>", result.Html);
    }

    [Fact]
    public void Excel_displays_cached_values_without_executing_or_emitting_formulas()
    {
        using var package = Package(
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{S}\"><si><r><t>Shared </t></r><r><t>text</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", Worksheet("""
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="b"><v>1</v></c>
                <c r="C1"><f>HYPERLINK("https://invalid.example/x")</f><v>125</v></c><c r="D1"><f>SUM(A1:B1)</f></c></row>
                """)));
        var result = OfficeDocumentPreview.ReadDocument(package, "cached.xlsx");
        Assert.Contains("Shared text", result.Html);
        Assert.Contains("<td>TRUE</td>", result.Html);
        Assert.Contains("Cached formula result; not recalculated\">125</td>", result.Html);
        Assert.Contains("[Formula result unavailable]", result.Html);
        Assert.DoesNotContain("HYPERLINK", result.Html);
        Assert.DoesNotContain("https://invalid.example", result.Html);
        Assert.DoesNotContain("SUM(A1", result.Html);
    }

    [Fact]
    public void PowerPoint_follows_presentation_order_and_retains_titles_and_tables()
    {
        using var package = Package(
            ("ppt/presentation.xml", $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{R}\"><p:sldIdLst><p:sldId r:id=\"nine\"/><p:sldId r:id=\"one\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", Relationships(("one", "slide", "slides/slide1.xml"), ("nine", "slide", "slides/slide9.xml"))),
            ("ppt/slides/slide1.xml", Slide("Later slide")),
            ("ppt/slides/slide9.xml", $"""
                <p:sld xmlns:p="{P}" xmlns:a="{A}"><p:cSld><p:spTree>
                <p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>First slide title</a:t></a:r></a:p></p:txBody></p:sp>
                <p:graphicFrame><a:graphic><a:graphicData><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Table cell</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>
                </p:spTree></p:cSld></p:sld>
                """));
        var result = OfficeDocumentPreview.ReadDocument(package, "deck.pptx");
        Assert.True(result.Html.IndexOf("First slide title", StringComparison.Ordinal) < result.Html.IndexOf("Later slide", StringComparison.Ordinal));
        Assert.Contains("<h3>First slide title</h3>", result.Html);
        Assert.Contains("<table><tbody><tr><td><p>Table cell</p></td></tr></tbody></table>", result.Html);
        Assert.Contains("<h2>Slide 1</h2>", result.Html);
        Assert.Contains("<h2>Slide 2</h2>", result.Html);
    }

    [Fact]
    public void Metadata_free_packages_have_deterministic_numeric_part_order()
    {
        using var package = Package(("ppt/slides/slide10.xml", Slide("Ten")), ("ppt/slides/slide2.xml", Slide("Two")));
        var result = OfficeDocumentPreview.ReadDocument(package, "simple.pptx");
        Assert.True(result.PlainText.IndexOf("Two", StringComparison.Ordinal) < result.PlainText.IndexOf("Ten", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TargetMode=\"External\" Target=\"worksheets/sheet1.xml\"")]
    [InlineData("Target=\"../../../../escape.xml\"")]
    [InlineData("Target=\"https://invalid.example/sheet.xml\"")]
    [InlineData("Target=\"%2f%2finvalid.example/sheet.xml\"")]
    public void Excel_never_resolves_external_or_outside_package_relationships(string attributes)
    {
        using var package = Package(
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Blocked\" r:id=\"bad\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships><Relationship Id=\"bad\" Type=\"{R}/worksheet\" {attributes}/></Relationships>"),
            ("xl/worksheets/sheet1.xml", Worksheet("<row/>")));
        Assert.Throws<InvalidDataException>(() => OfficeDocumentPreview.ReadDocument(package, "unsafe.xlsx"));
    }

    [Fact]
    public void Xml_dtd_is_rejected()
    {
        using var package = Package(("word/document.xml", "<!DOCTYPE doc [<!ENTITY x SYSTEM 'file:///private'>]><doc>&x;</doc>"));
        Assert.ThrowsAny<XmlException>(() => OfficeDocumentPreview.ReadDocument(package, "unsafe.docx"));
    }

    [Fact]
    public void Oversized_document_xml_is_rejected_even_when_highly_compressed()
    {
        using var package = Package(("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>" + new string('a', 3 * 1024 * 1024) + "</w:t></w:r></w:p></w:body></w:document>"));
        Assert.True(package.Length < OfficeTextPreview.MaximumPackageBytes);
        Assert.Throws<InvalidDataException>(() => OfficeDocumentPreview.ReadDocument(package, "large.docx"));
    }

    [Fact]
    public void Cumulative_xml_expansion_is_bounded_across_slides()
    {
        var comment = new string('x', 2_600_000);
        using var package = Package(Enumerable.Range(1, 5).Select(n => ($"ppt/slides/slide{n}.xml", Slide("Small visible text") + "<!--" + comment + "-->" )).ToArray());
        Assert.Throws<InvalidDataException>(() => OfficeDocumentPreview.ReadDocument(package, "large.pptx"));
    }

    [Fact]
    public void Preview_output_is_truncated_and_remains_below_embedded_browser_limit()
    {
        var text = new string('x', OfficeDocumentPreview.MaximumTextLength + 50);
        using var package = Package(("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>"));
        var result = OfficeDocumentPreview.ReadDocument(package, "long.docx");
        Assert.True(result.Truncated);
        Assert.Contains("Preview shortened", result.Html);
        Assert.True(result.PlainText.Length <= OfficeDocumentPreview.MaximumTextLength + Environment.NewLine.Length);
        Assert.True(Encoding.UTF8.GetByteCount(result.Html) < 1_900_000);
        Assert.EndsWith("</main></body></html>", result.Html);
    }

    [Fact]
    public void Excel_columns_outside_the_preview_window_are_reported_as_truncated()
    {
        using var package = Package(("xl/worksheets/sheet1.xml", Worksheet("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"AZ1\"><v>999</v></c></row>")));
        var result = OfficeDocumentPreview.ReadDocument(package, "wide.xlsx");
        Assert.True(result.Truncated);
        Assert.Contains("<td>1</td>", result.Html);
        Assert.DoesNotContain(">999<", result.Html);
    }

    [Fact]
    public void Package_byte_and_entry_limits_are_enforced()
    {
        using var oversized = new MemoryStream();
        oversized.SetLength(OfficeTextPreview.MaximumPackageBytes + 1);
        Assert.Throws<InvalidDataException>(() => OfficeDocumentPreview.ReadDocument(oversized, "large.docx"));
        using var many = Package(Enumerable.Range(0, 2001).Select(n => ($"empty{n}.xml", "<x/>" )).ToArray());
        Assert.Throws<InvalidDataException>(() => OfficeDocumentPreview.ReadDocument(many, "many.docx"));
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("legacy.xls")]
    [InlineData("legacy.ppt")]
    [InlineData("macro.docm")]
    [InlineData("macro.xlsm")]
    [InlineData("macro.pptm")]
    public void Unsupported_legacy_and_macro_extensions_are_not_guessed(string name)
    {
        using var package = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => OfficeDocumentPreview.ReadDocument(package, name));
    }

    [Fact]
    public void Dark_theme_uses_static_styling_without_scripts()
    {
        using var package = Package(("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>"));
        var result = OfficeDocumentPreview.ReadDocument(package, "theme.docx");
        Assert.Contains("<html lang=\"en\" class=\"dark\">", result.HtmlForTheme(true));
        Assert.DoesNotContain("<script", result.HtmlForTheme(true));
        Assert.Equal(result.Html, result.HtmlForTheme(false));
    }

    private static string Worksheet(string rows) => $"<worksheet xmlns=\"{S}\"><sheetData>{rows}</sheetData></worksheet>";
    private static string Slide(string text) => $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
    private static string Relationships(params (string Id, string Kind, string Target)[] relationships) =>
        "<Relationships>" + string.Concat(relationships.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{R}/{r.Kind}\" Target=\"{r.Target}\"/>")) + "</Relationships>";

    private static MemoryStream Package(params (string Path, string Xml)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var output = new StreamWriter(archive.CreateEntry(entry.Path).Open());
                output.Write(entry.Xml);
            }
        stream.Position = 0;
        return stream;
    }
}
