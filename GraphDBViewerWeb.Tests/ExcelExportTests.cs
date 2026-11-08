using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class ExcelExportTests
{
    private static string ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.NotNull(entry);

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void BuildXlsx_ProducesValidZip_WithColoredNodeRow()
    {
        var json = """
        [
          { "id": "1", "label": "person", "properties": { "name": "Alice" } },
          { "id": "2", "label": "product", "properties": { } }
        ]
        """;
        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
        var styles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Color = "#ff0000" } };

        var bytes = ExcelExport.BuildXlsx(table, styles);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));

        var stylesXml = ReadEntry(zip, "xl/styles.xml");
        var sheetXml = ReadEntry(zip, "xl/worksheets/sheet1.xml");

        //Both parts are well-formed XML.
        XDocument.Parse(stylesXml);
        XDocument.Parse(sheetXml);

        //The person label's color is emitted as an ARGB solid fill.
        Assert.Contains("FFFF0000", stylesXml);

        //Vertex data is present in the sheet.
        Assert.Contains("person", sheetXml);
        Assert.Contains("Alice", sheetXml);
    }

    [Fact]
    public void BuildXlsx_NoStyles_StillValid()
    {
        var json = """
        [ { "id": "1", "label": "person", "properties": {} } ]
        """;
        var table = GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);

        var bytes = ExcelExport.BuildXlsx(table, null);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        XDocument.Parse(ReadEntry(zip, "xl/worksheets/sheet1.xml"));
        XDocument.Parse(ReadEntry(zip, "xl/styles.xml"));
    }

    private static GraphDataConverter.GraphTable TableWithProperty(string value)
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { id = "n1", label = "Item", properties = new { note = value } }
        });

        return GraphDataConverter.ToTable(JsonDocument.Parse(json).RootElement);
    }

    //The text of every cell in the sheet, read through an XML parser as Excel reads it.
    private static List<string> CellTexts(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var doc = XDocument.Parse(ReadEntry(zip, "xl/worksheets/sheet1.xml"));

        return doc.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value).ToList();
    }

    [Fact]
    public void AControlCharacterInAValue_IsDroppedFromTheWorkbook()
    {
        //U+0001 is not a character XML 1.0 allows at all, escaped or not, so Excel refuses the whole workbook over it.
        //A tab and a line feed are control characters XML does allow, and they stay.
        var bytes = ExcelExport.BuildXlsx(TableWithProperty("a\u0001b\u001fc\td\ne"), null);

        var texts = CellTexts(bytes);

        Assert.Contains("abc\td\ne", texts);
        Assert.DoesNotContain(texts, t => t.Contains('\u0001') || t.Contains('\u001f'));
    }

    [Fact]
    public void AValueLongerThanExcelsCellLimit_IsCutToIt()
    {
        //Excel holds at most 32,767 characters in a cell and "repairs" a workbook that has more, with a warning dialog.
        var value = new string('x', 32_767) + new string('y', 7_233);
        var bytes = ExcelExport.BuildXlsx(TableWithProperty(value), null);

        var longest = CellTexts(bytes).OrderByDescending(t => t.Length).First();

        Assert.Equal(ExcelExport.CellLimit, longest.Length);
        Assert.Equal(new string('x', 32_767), longest);
    }

    [Fact]
    public void ACutAtTheCellLimit_DoesNotHalveASurrogatePair()
    {
        //An emoji is two UTF-16 units; cut between them, the half left is a character XML cannot carry either.
        var value = new string('x', 32_766) + "\U0001F600" + new string('y', 10);
        var bytes = ExcelExport.BuildXlsx(TableWithProperty(value), null);

        var longest = CellTexts(bytes).OrderByDescending(t => t.Length).First();

        Assert.Equal(new string('x', 32_766), longest);
    }
}
