using System.IO.Compression;
using System.Text;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class SourceTextExtractorTests
{
    private static MemoryStream ZipWith(params (string Path, string Xml)[] entries)
    {
        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in entries)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(xml);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream TextStream(string text)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Extract_Docx_ReadsParagraphText()
    {
        using var docx = ZipWith(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Alice works at</w:t></w:r><w:r><w:t> Acme.</w:t></w:r></w:p>
                <w:p><w:r><w:t>Bob works there too.</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));

        var (text, error) = SourceTextExtractor.Extract("notes.docx", docx);

        Assert.Null(error);
        Assert.Contains("Alice works at Acme.", text);
        Assert.Contains("Bob works there too.", text);
    }

    [Fact]
    public void Extract_Xlsx_ResolvesSharedStringsAndNumbers()
    {
        using var xlsx = ZipWith(
            ("xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>Acme</t></si><si><t>Robotics</t></si>
                </sst>
                """),
            ("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row><c t="s"><v>0</v></c><c t="s"><v>1</v></c><c><v>1999</v></c></row>
                  </sheetData>
                </worksheet>
                """));

        var (text, error) = SourceTextExtractor.Extract("companies.xlsx", xlsx);

        Assert.Null(error);
        Assert.Contains("Acme, Robotics, 1999", text);
    }

    //Builds a real PDF so the reader is tested against one, rather than against a fixture that agrees
    //with whatever the reader happens to do.
    private static MemoryStream PdfSaying(params string[] lines)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);

        int y = 700;

        foreach (var line in lines)
        {
            page.AddText(line, 12, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 24;
        }

        return new MemoryStream(builder.Build());
    }

    //The panel used to turn a PDF away saying the browser had no extractor. It has one now, and it is
    //the same managed parser the desktop side of this codebase uses.
    [Fact]
    public void Extract_Pdf_ReadsTheText()
    {
        using var stream = PdfSaying("Acme Robotics was founded in 1999.", "Alice Chen leads the lab.");

        var (text, error) = SourceTextExtractor.Extract("report.pdf", stream);

        Assert.Null(error);
        Assert.Contains("Acme Robotics", text);
        Assert.Contains("1999", text);
        Assert.Contains("Alice Chen", text);
    }

    //Every page, not just the first: a datasheet's interesting part is rarely on page one.
    [Fact]
    public void Extract_Pdf_ReadsEveryPage()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        var first = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        first.AddText("Page one mentions Bolt.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);

        var second = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        second.AddText("Page two mentions Cog.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);

        using var stream = new MemoryStream(builder.Build());

        var (text, error) = SourceTextExtractor.Extract("two-pager.pdf", stream);

        Assert.Null(error);
        Assert.Contains("Bolt", text);
        Assert.Contains("Cog", text);
    }

    //Something that only claims to be a PDF fails the way an unreadable .docx does — named, and without
    //a stack trace — rather than being refused before anything is tried.
    [Fact]
    public void Extract_Pdf_ThatIsNotOne_SaysSo()
    {
        using var stream = TextStream("%PDF-1.7 whatever");

        var (text, error) = SourceTextExtractor.Extract("report.pdf", stream);

        Assert.Null(text);
        Assert.Contains("report.pdf", error);
        Assert.Contains("PDF", error);
    }

    [Fact]
    public void Extract_BinaryFile_Refused()
    {
        using var stream = new MemoryStream(new byte[] { 0, 1, 2, 0, 0, 5, 0, 0, 0, 9, 0, 0 });

        var (text, error) = SourceTextExtractor.Extract("blob.dat", stream);

        Assert.Null(text);
        Assert.Contains("doesn't look like a text file", error);
    }

    //"Other file endings, as long as they have text inside" — the fallback opens anything as text.
    [Fact]
    public void Extract_UnknownTextExtension_LoadsAsText()
    {
        using var stream = TextStream("2026-07-18 Alice deployed the payment service.");

        var (text, error) = SourceTextExtractor.Extract("build.log", stream);

        Assert.Null(error);
        Assert.Contains("payment service", text);
    }

    [Fact]
    public void Extract_CorruptDocx_FailsWithClearError()
    {
        using var stream = TextStream("this is not a zip");

        var (text, error) = SourceTextExtractor.Extract("broken.docx", stream);

        Assert.Null(text);
        Assert.Contains("Word", error);
    }
}
