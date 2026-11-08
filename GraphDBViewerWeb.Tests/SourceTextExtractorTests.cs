using System.Collections.Generic;
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

    //── Dates in a spreadsheet (2026-09-07) ──────────────────────────────────────
    //
    //A spreadsheet's numbers need no locale guessing: OOXML stores them invariant, so 1,5 typed on a
    //Slovene machine is 1.5 in the file. A DATE is the opposite — a bare serial with a style over it —
    //so reading the value alone hands a model 45000 where the sheet plainly showed a date.

    private const string DateStyles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2">
            <numFmt numFmtId="164" formatCode="dd\.mm\.yyyy"/>
            <numFmt numFmtId="165" formatCode="&quot;dollars&quot;\ #,##0.00"/>
          </numFmts>
          <cellXfs count="5">
            <xf numFmtId="0"/>
            <xf numFmtId="14"/>
            <xf numFmtId="22"/>
            <xf numFmtId="164"/>
            <xf numFmtId="165"/>
          </cellXfs>
        </styleSheet>
        """;

    private static MemoryStream Sheet(string rows, string styles = DateStyles, string workbook = null)
    {
        var entries = new List<(string, string)>
        {
            ("xl/styles.xml", styles),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>{rows}</sheetData>
                </worksheet>
                """)
        };

        if (workbook != null)
            entries.Add(("xl/workbook.xml", workbook));

        return ZipWith(entries.ToArray());
    }

    [Fact]
    public void Extract_Xlsx_WritesADateCellAsADate_NotItsSerialNumber()
    {
        //45000 is 2023-03-15. Style 1 is the built-in short date; style 3 is a custom d.m.y.
        using var xlsx = Sheet("""<row><c s="1"><v>45000</v></c><c s="3"><v>45000</v></c></row>""");

        var (text, error) = SourceTextExtractor.Extract("dates.xlsx", xlsx);

        Assert.Null(error);
        Assert.Equal("2023-03-15, 2023-03-15", text);
    }

    [Fact]
    public void Extract_Xlsx_KeepsTheTimeWhenTheSerialHasOne()
    {
        //Style 2 is the built-in date-and-time format, and .5 of a day is noon.
        using var xlsx = Sheet("""<row><c s="2"><v>45000.5</v></c></row>""");

        var (text, _) = SourceTextExtractor.Extract("dates.xlsx", xlsx);

        Assert.Equal("2023-03-15 12:00:00", text);
    }

    [Fact]
    public void Extract_Xlsx_LeavesANumberANumber_EvenOneThatCouldBeASerial()
    {
        //Style 0 is General and style 4 is a currency format whose literal text contains a "d".
        using var xlsx = Sheet("""<row><c s="0"><v>45000</v></c><c s="4"><v>1999.5</v></c></row>""");

        var (text, _) = SourceTextExtractor.Extract("prices.xlsx", xlsx);

        Assert.Equal("45000, 1999.5", text);
    }

    [Fact]
    public void Extract_Xlsx_LeavesTextAlone_EvenUnderADateStyle()
    {
        //A formula that answered with text, and an inline string: neither is a serial number.
        using var xlsx = Sheet("""<row><c s="1" t="str"><v>not a date</v></c><c s="1" t="inlineStr"><is><t>nor this</t></is></c></row>""");

        var (text, _) = SourceTextExtractor.Extract("mixed.xlsx", xlsx);

        Assert.Equal("not a date, nor this", text);
    }

    [Fact]
    public void Extract_Xlsx_CountsFrom1904WhenTheWorkbookSaysSo()
    {
        //A Mac-saved workbook counts from 1904-01-01: the same serial is 1462 days later, which is the
        //four years between the two epochs — and one more, because the 1904 system has no phantom
        //1900-02-29 to subtract.
        var workbook = """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <workbookPr date1904="1"/>
            </workbook>
            """;

        using var xlsx = Sheet("""<row><c s="1"><v>45000</v></c></row>""", workbook: workbook);

        var (text, _) = SourceTextExtractor.Extract("mac.xlsx", xlsx);

        Assert.Equal("2027-03-16", text);
    }

    [Fact]
    public void Extract_Xlsx_WithNoStyles_ReadsEveryValueAsItIsStored()
    {
        //The app's own ExcelExport writes no styles part; nothing there is a date, and nothing breaks.
        using var xlsx = ZipWith(("xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row><c s="1"><v>45000</v></c></row></sheetData>
            </worksheet>
            """));

        var (text, _) = SourceTextExtractor.Extract("plain.xlsx", xlsx);

        Assert.Equal("45000", text);
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

    //A PDF the browser names by its type alone, as a file dragged out of another page arrives, is read as a PDF, as
    //AiAttachment.KindOf takes it for one (the audit of 2026-11-07: the extractor went by the extension alone and read
    //it as a text file). The type decides a PDF, whatever its case, and the extension second.
    [Fact]
    public void Extract_APdfKnownOnlyByItsType_ReadsItAsAPdf()
    {
        using var stream = PdfSaying("Acme Robotics was founded in 1999.");

        var (text, error) = SourceTextExtractor.Extract("scan", stream, "application/pdf");

        Assert.Null(error);
        Assert.Contains("Acme Robotics", text);

        using var broken = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4\n").Concat(new byte[] { 0, 1, 2, 0, 0, 0 }).ToArray());

        var (none, why) = SourceTextExtractor.Extract("scan", broken, "Application/PDF");

        Assert.Null(none);
        Assert.Contains("as a PDF file", why);
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
