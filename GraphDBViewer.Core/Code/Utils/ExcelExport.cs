using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///Exports a graph table (vertices then edges) as a minimal, valid .xlsx (OpenXML), built by hand as a
///ZIP of XML parts with System.IO.Compression — no NuGet dependency. Vertex rows are filled with the
///color configured for their label (per-label styling); a distinct cell style is emitted per color used.
///</summary>
public static class ExcelExport
{
    public static byte[] BuildXlsx(GraphDataConverter.GraphTable table, IReadOnlyDictionary<string, LabelStyle> styles)
    {
        var propColumns = table.NodePropertyColumns.Concat(table.EdgePropertyColumns).Distinct().ToList();

        var header = new List<string> { "kind", "id", "label", "source", "target" };
        header.AddRange(propColumns);

        //Style 0 = default, 1 = bold header, then one solid-fill style per distinct node color used.
        const int firstColorStyle = 2;
        var fillColors = new List<string>();//ARGB per fill, in style order
        var fillLightText = new List<bool>();
        var styleByColor = new Dictionary<string, int>();

        int StyleForColor(string cssColor)
        {
            var argb = ColorUtil.ToArgb(cssColor);
            if (argb == null)
                return 0;

            if (!styleByColor.TryGetValue(argb, out int index))
            {
                index = firstColorStyle + fillColors.Count;
                styleByColor[argb] = index;
                fillColors.Add(argb);
                fillLightText.Add(ColorUtil.IsDark(cssColor));
            }

            return index;
        }

        var columnChars = header.Select(h => h.Length).ToArray();

        void Track(int columnIndex, string text)
        {
            if (text != null && text.Length > columnChars[columnIndex])
                columnChars[columnIndex] = text.Length;
        }

        var body = new StringBuilder();

        body.Append("<row r=\"1\">");
        for (int c = 0; c < header.Count; c++)
            AppendTextCell(body, ColumnRef(c + 1) + "1", header[c], 1);
        body.Append("</row>");

        int rowIndex = 2;

        foreach (var node in table.Nodes)
        {
            int style = 0;
            if (styles != null && node.Label != null && styles.TryGetValue(node.Label, out var ls) && !string.IsNullOrWhiteSpace(ls.Color))
                style = StyleForColor(ls.Color);

            var cells = new List<string> { "vertex", node.Id, node.Label, "", "" };
            foreach (var col in propColumns)
                cells.Add(PropOrEmpty(node.Properties, col));

            AppendRow(body, rowIndex, cells, style, Track);
            rowIndex++;
        }

        foreach (var edge in table.Edges)
        {
            var cells = new List<string> { "edge", edge.Id, edge.Label, edge.Source ?? "", edge.Target ?? "" };
            foreach (var col in propColumns)
                cells.Add(PropOrEmpty(edge.Properties, col));

            AppendRow(body, rowIndex, cells, 0, Track);
            rowIndex++;
        }

        //Column widths (unit ≈ characters of the default font); +2 pads for cell margins, capped at Excel's max of 255.
        var cols = new StringBuilder();
        cols.Append("<cols>");
        for (int i = 0; i < columnChars.Length; i++)
        {
            double width = Math.Min(255.0, columnChars[i] + 2);
            cols.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        }
        cols.Append("</cols>");

        //<cols> must precede <sheetData> in the schema.
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sheet.Append(cols.ToString());
        sheet.Append("<sheetData>");
        sheet.Append(body.ToString());
        sheet.Append("</sheetData></worksheet>");

        return PackXlsx(sheet.ToString(), BuildStylesXml(fillColors, fillLightText));
    }

    private static void AppendRow(StringBuilder target, int rowIndex, List<string> cells, int style, Action<int, string> track)
    {
        target.Append($"<row r=\"{rowIndex}\">");

        for (int c = 0; c < cells.Count; c++)
        {
            track(c, cells[c]);
            AppendTextCell(target, ColumnRef(c + 1) + rowIndex, cells[c], style);
        }

        target.Append("</row>");
    }

    private static void AppendTextCell(StringBuilder target, string reference, string value, int style)
    {
        target.Append($"<c r=\"{reference}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>");
    }

    private static string PropOrEmpty(Dictionary<string, string> properties, string column)
    {
        if (properties.TryGetValue(column, out var value))
            return value;

        return "";
    }

    //1-based column index to its spreadsheet letters (1→A, 26→Z, 27→AA).
    private static string ColumnRef(int column)
    {
        var s = "";

        while (column > 0)
        {
            int remainder = (column - 1) % 26;
            s = (char)('A' + remainder) + s;
            column = (column - 1) / 26;
        }

        return s;
    }

    private static string XmlEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    //Styles part: fixed formats 0 (default) and 1 (bold header), then one solid-fill format per node color,
    //each with light or dark text for contrast. Indexes line up with the style ids assigned in BuildXlsx.
    private static string BuildStylesXml(IReadOnlyList<string> fillColors, IReadOnlyList<bool> fillLightText)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        //Fonts: 0 default (black), 1 bold header, 2 white (for dark fills).
        sb.Append("<fonts count=\"3\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"Calibri\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>");
        sb.Append("<font><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font>");
        sb.Append("</fonts>");

        //Fills: 0 none, 1 gray125 (both required by the schema), then one solid fill per node color.
        sb.Append($"<fills count=\"{2 + fillColors.Count}\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
        foreach (var argb in fillColors)
            sb.Append($"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"{argb}\"/></patternFill></fill>");
        sb.Append("</fills>");

        sb.Append("<borders count=\"1\"><border/></borders>");
        sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

        //cellXfs: 0 default, 1 header, then one per node color (fill 2+i, white or black font).
        sb.Append($"<cellXfs count=\"{2 + fillColors.Count}\">");
        sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
        sb.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
        for (int i = 0; i < fillColors.Count; i++)
        {
            int fontId;
            if (fillLightText[i])
                fontId = 2;
            else
                fontId = 0;

            sb.Append($"<xf numFmtId=\"0\" fontId=\"{fontId}\" fillId=\"{2 + i}\" borderId=\"0\" xfId=\"0\" applyFill=\"1\" applyFont=\"1\"/>");
        }
        sb.Append("</cellXfs>");

        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("</styleSheet>");

        return sb.ToString();
    }

    //Packs a single worksheet + styles into a minimal, valid .xlsx (OpenXML) zip.
    private static byte[] PackXlsx(string worksheetXml, string stylesXml)
    {
        const string contentTypes = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types""><Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/><Default Extension=""xml"" ContentType=""application/xml""/><Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/><Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/><Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/></Types>";
        const string rootRels = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships""><Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/></Relationships>";
        const string workbook = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""><sheets><sheet name=""Graph"" sheetId=""1"" r:id=""rId1""/></sheets></workbook>";
        const string workbookRels = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships""><Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/><Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/></Relationships>";

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            void AddEntry(string entryPath, string content)
            {
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                writer.Write(content);
            }

            AddEntry("[Content_Types].xml", contentTypes);
            AddEntry("_rels/.rels", rootRels);
            AddEntry("xl/workbook.xml", workbook);
            AddEntry("xl/_rels/workbook.xml.rels", workbookRels);
            AddEntry("xl/styles.xml", stylesXml);
            AddEntry("xl/worksheets/sheet1.xml", worksheetXml);
        }

        return memoryStream.ToArray();
    }
}
