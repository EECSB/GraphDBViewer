using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///One sheet of an .xlsx workbook as the grid it is: every row at its own row number and every cell in its own column,
///the empty ones empty. <see cref="SourceTextExtractor"/> flattens a workbook into lines of text for a model, which
///loses where a value was; a table read out of a sheet needs its header where the sheet has it and each value under
///the column it was typed in, so a gap stays a gap here. Pure, and the framework's own ZipArchive and XDocument, as
///the extractor is.
///
///The elements are matched by their local names, so a workbook saved as Strict Open XML, whose namespaces differ, reads
///as one saved as the usual Transitional does.
///</summary>
public static class XlsxTable
{
    ///<summary>
    ///More rows than any sheet a person reads holds: a row number past this is a file built to make a reader count, and
    ///the grid stops there rather than filling a million empty rows into the browser.
    ///</summary>
    public const int MaxRows = 100_000;

    ///<summary>Excel's own last column, XFD.</summary>
    public const int MaxColumns = 16_384;

    ///<summary>The most a sheet's XML may unpack to, since its size is read from the file before anything is parsed.</summary>
    public const long MaxSheetBytes = 128L * 1024 * 1024;

    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipsNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    ///<summary>A sheet: its name as its tab shows it, how many sheets the workbook has, and its rows.</summary>
    public sealed record Sheet(string Name, int Sheets, IReadOnlyList<IReadOnlyList<string>> Rows);

    ///<summary>
    ///The first sheet a person sees in the workbook's tabs, hidden ones passed over, or null when the file is not a
    ///workbook this can read. The values are as the sheet shows them: shared strings resolved, a formula's last result,
    ///a date cell as its date, a number in up to fifteen significant digits as Excel's General format shows it, and
    ///TRUE or FALSE.
    ///</summary>
    public static Sheet First(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var sheets = SheetsOf(zip);
        var first = sheets.FirstOrDefault(s => !s.Hidden);

        if (first == null)
            return null;

        var entry = zip.GetEntry(first.Path);

        if (entry == null || entry.Length > MaxSheetBytes)
            return null;

        var shared = SharedStrings(zip);
        var styles = DateStyles(zip);
        var epoch = Epoch(zip);
        XDocument doc;

        using (var stream = entry.Open())
            doc = XDocument.Load(stream);

        var rows = new List<IReadOnlyList<string>>();
        var data = Children(doc.Root, "sheetData").FirstOrDefault();
        var next = 1;

        foreach (var row in Children(data, "row"))
        {
            var number = Number((string)row.Attribute("r"), next);

            if (number < next || number > MaxRows)
                break;

            while (rows.Count < number - 1)
                rows.Add([]);

            rows.Add(Cells(row, shared, styles, epoch));
            next = number + 1;
        }

        while (rows.Count > 0 && rows[^1].Count == 0)
            rows.RemoveAt(rows.Count - 1);

        return new Sheet(first.Name, sheets.Count, rows);
    }

    ///<summary>One row's cells, each in the column its reference names, the ones between empty and the empty ones at its end left off.</summary>
    private static List<string> Cells(XElement row, IReadOnlyList<string> shared, IReadOnlyDictionary<int, bool> styles, DateTime epoch)
    {
        var cells = new List<string>();
        var next = 0;

        foreach (var cell in Children(row, "c"))
        {
            var column = Column((string)cell.Attribute("r"), next);

            if (column < next || column >= MaxColumns)
                break;

            while (cells.Count < column)
                cells.Add("");

            cells.Add(Value(cell, shared, styles, epoch));
            next = column + 1;
        }

        while (cells.Count > 0 && cells[^1].Length == 0)
            cells.RemoveAt(cells.Count - 1);

        return cells;
    }

    ///<summary>A cell's value as the sheet shows it, by the type the cell says it is.</summary>
    private static string Value(XElement cell, IReadOnlyList<string> shared, IReadOnlyDictionary<int, bool> styles, DateTime epoch)
    {
        var type = (string)cell.Attribute("t");

        if (type == "inlineStr")
            return Text(Children(cell, "is").FirstOrDefault());

        var value = Children(cell, "v").FirstOrDefault()?.Value;

        if (value == null)
            return "";

        if (type == "s")
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count)
                return shared[index];

            return "";
        }
        else if (type == "b")
        {
            if (value.Trim() == "1")
                return "TRUE";

            return "FALSE";
        }
        else if (type == "str" || type == "e")
            return value;

        if (TryDate(value, type, (string)cell.Attribute("s"), styles, epoch, out var date))
            return date;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number.ToString("G15", CultureInfo.InvariantCulture);

        return value;
    }

    ///<summary>
    ///A string item's text: its own text, or its runs' joined, and never its phonetic guide (<c>rPh</c>), which Japanese
    ///workbooks carry beside a name to say how it is read and which the cell does not show.
    ///</summary>
    private static string Text(XElement item)
    {
        if (item == null)
            return "";

        var text = new StringBuilder();

        foreach (var child in item.Elements())
        {
            var name = child.Name.LocalName;

            if (name == "t")
                text.Append(child.Value);
            else if (name == "r")
                foreach (var run in Children(child, "t"))
                    text.Append(run.Value);
        }

        return Unescaped(text.ToString());
    }

    ///<summary>
    ///Open XML's escapes undone: a character XML cannot carry, a carriage return the commonest, is written <c>_x000D_</c>,
    ///and an underscore that would read as the start of one is written <c>_x005F_</c>.
    ///</summary>
    private static string Unescaped(string text)
    {
        if (text.IndexOf("_x", StringComparison.Ordinal) < 0)
            return text;

        var result = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_'
                && i + 6 < text.Length
                && text[i + 1] == 'x'
                && text[i + 6] == '_'
                && int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                result.Append((char)code);
                i += 6;

                continue;
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    ///<summary>A row's number from its reference, or the one after the row before when it has none.</summary>
    private static int Number(string reference, int next)
    {
        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0)
            return number;

        return next;
    }

    ///<summary>A cell's column, counting from zero, from the letters of its reference (<c>AB12</c> is 27), or the one after the cell before when it has none.</summary>
    private static int Column(string reference, int next)
    {
        if (string.IsNullOrEmpty(reference))
            return next;

        var column = 0;
        var letters = 0;

        foreach (var c in reference)
        {
            if (c >= 'A' && c <= 'Z')
                column = column * 26 + (c - 'A' + 1);
            else if (c >= 'a' && c <= 'z')
                column = column * 26 + (c - 'a' + 1);
            else
                break;

            letters++;

            if (letters > 3)
                return MaxColumns;
        }

        if (letters == 0)
            return next;

        return column - 1;
    }

    //── The workbook's parts ───────────────────────────────────────────────────────────────────

    private sealed record SheetPart(string Name, string Path, bool Hidden);

    ///<summary>
    ///The workbook's sheets in the order of its tabs, each with the part that holds it: the workbook names them and its
    ///relationships say where each is. A workbook missing either is read by its sheet parts' own numbers.
    ///</summary>
    private static List<SheetPart> SheetsOf(ZipArchive zip)
    {
        var sheets = new List<SheetPart>();
        var workbook = Load(zip, "xl/workbook.xml");
        var targets = Targets(Load(zip, "xl/_rels/workbook.xml.rels"));

        foreach (var sheet in Children(Children(workbook?.Root, "sheets").FirstOrDefault(), "sheet"))
        {
            var id = (string)sheet.Attribute(XName.Get("id", RelationshipsNamespace)) ?? (string)sheet.Attribute(XName.Get("id", StrictRelationshipsNamespace));

            if (id == null || !targets.TryGetValue(id, out var path))
                continue;

            var state = (string)sheet.Attribute("state");

            sheets.Add(new SheetPart((string)sheet.Attribute("name") ?? "", path, state == "hidden" || state == "veryHidden"));
        }

        if (sheets.Count > 0)
            return sheets;

        var parts = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => PartNumber(e.FullName))
            .ThenBy(e => e.FullName, StringComparer.Ordinal);

        foreach (var entry in parts)
            sheets.Add(new SheetPart(Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName, false));

        return sheets;
    }

    ///<summary>
    ///Where each of the workbook's worksheets is, as a path inside the package. A tab that holds a chart alone is a
    ///chartsheet, which has no cells, so it is not among them and the first tab with cells is the first sheet.
    ///</summary>
    private static Dictionary<string, string> Targets(XDocument relationships)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var relationship in Children(relationships?.Root, "Relationship"))
        {
            var id = (string)relationship.Attribute("Id");
            var target = (string)relationship.Attribute("Target");
            var type = (string)relationship.Attribute("Type") ?? "";

            if (id == null || string.IsNullOrEmpty(target) || !type.EndsWith("/worksheet", StringComparison.Ordinal))
                continue;

            if (target.StartsWith('/'))
                targets[id] = target.TrimStart('/');
            else
                targets[id] = "xl/" + target;
        }

        return targets;
    }

    ///<summary>The number in a part's name, <c>sheet12.xml</c> being 12, so sheet10 comes after sheet9.</summary>
    private static int PartNumber(string path)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(path).Where(char.IsAsciiDigit).ToArray());

        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number;

        return int.MaxValue;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var shared = new List<string>();
        var doc = Load(zip, "xl/sharedStrings.xml");

        foreach (var item in Children(doc?.Root, "si"))
            shared.Add(Text(item));

        return shared;
    }

    private static XDocument Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);

        if (entry == null || entry.Length > MaxSheetBytes)
            return null;

        try
        {
            using var stream = entry.Open();

            return XDocument.Load(stream);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<XElement> Children(XElement element, string localName)
    {
        if (element == null)
            return [];

        return element.Elements().Where(e => e.Name.LocalName == localName);
    }

    //── Dates, shared with SourceTextExtractor ─────────────────────────────────────────────────

    ///<summary>
    ///One number cell's value as the date it shows, when its style says it is one: ISO 8601, with the time only when
    ///there is one. A cell whose type says it is text is never a serial, even under a date style.
    ///</summary>
    internal static bool TryDate(string value, string type, string style, IReadOnlyDictionary<int, bool> styles, DateTime epoch, out string date)
    {
        date = null;

        if (type != null && type != "n")
            return false;

        if (style == null || !int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return false;

        if (!styles.TryGetValue(index, out var isDate) || !isDate)
            return false;

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return false;

        var when = Moment(serial, epoch);

        if (when == null)
            return false;

        //A date cell with a zero fraction is a date.
        if (when.Value.TimeOfDay == TimeSpan.Zero)
            date = when.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else
            date = when.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return true;
    }

    ///<summary>
    ///A serial number as a moment. Excel's day 1 is 1900-01-01 and it also believes in 1900-02-29, a
    ///day that never existed — so every serial from 61 onwards is one greater than the real day count,
    ///and serials below 61 are off by that day. Both are handled the way every reader handles them:
    ///subtract the phantom day above 60. Serial 60 is the phantom itself and comes back as 1900-03-01,
    ///a day later than the impossible date Excel prints for it; no real spreadsheet holds it.
    ///</summary>
    private static DateTime? Moment(double serial, DateTime epoch)
    {
        if (serial < 0 || serial > 2958465)
            return null;

        var days = serial;

        //The 1900 workbook's phantom leap day. A 1904 workbook has no such thing.
        if (epoch.Year == 1899 && days >= 61)
            days -= 1;

        try
        {
            return epoch.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    ///<summary>
    ///Which style indexes are dates. A cell names a style, the style names a number format, and the
    ///format says whether the number is a date: the built-in ids that are dates by definition, or a
    ///custom format code with a date or time token outside its quoted literals.
    ///</summary>
    internal static Dictionary<int, bool> DateStyles(ZipArchive zip)
    {
        var styles = new Dictionary<int, bool>();
        var doc = Load(zip, "xl/styles.xml");

        if (doc == null)
            return styles;

        //The custom formats, by id. Anything not here is one of the built-ins.
        var custom = new Dictionary<int, string>();

        foreach (var format in doc.Descendants().Where(e => e.Name.LocalName == "numFmt"))
            if (int.TryParse((string)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                custom[id] = (string)format.Attribute("formatCode") ?? "";

        var cellXfs = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");

        if (cellXfs == null)
            return styles;

        var index = 0;

        foreach (var xf in Children(cellXfs, "xf"))
        {
            //A missing or unparseable id is 0, which is "General" and never a date.
            int.TryParse((string)xf.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);

            if (custom.TryGetValue(id, out var code))
                styles[index] = IsDateFormat(code);
            else
                styles[index] = BuiltInDateFormats.Contains(id);

            index++;
        }

        return styles;
    }

    ///<summary>The built-in number format ids that are dates or times. Fixed by the standard, so they are a list rather than a guess.</summary>
    private static readonly HashSet<int> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58];

    ///<summary>
    ///Whether a custom format code draws a date. The tokens are y, m, d, h and s — but only outside
    ///quoted text and escapes, or a currency format naming "dollars" would read as a date, and only
    ///when the code is not the "General"/text one.
    ///</summary>
    private static bool IsDateFormat(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var quoted = false;

        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];

            if (c == '"')
            {
                quoted = !quoted;

                continue;
            }

            if (quoted)
                continue;

            //A backslash escapes the next character, and [Red] and friends are conditions, not tokens.
            if (c == '\\' || c == '_' || c == '*')
            {
                i++;

                continue;
            }
            else if (c == '[')
            {
                var close = code.IndexOf(']', i);

                if (close < 0)
                    return false;

                //[h], [m] and [s] are elapsed-time tokens, which are durations rather than dates.
                i = close;

                continue;
            }
            else if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M')
                //"m" is minutes or months depending on where it sits; either way the cell is a moment.
                return true;
        }

        return false;
    }

    ///<summary>
    ///Day zero for this workbook. Excel's default is the 1900 system, whose serial 1 is 1900-01-01, so
    ///day zero is 1899-12-31; a workbook saved by an older Mac Excel says <c>date1904</c> and counts
    ///from 1904-01-01.
    ///</summary>
    internal static DateTime Epoch(ZipArchive zip)
    {
        var doc = Load(zip, "xl/workbook.xml");
        var properties = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
        var flag = (string)properties?.Attribute("date1904");

        if (flag == "1" || flag == "true")
            return new DateTime(1904, 1, 1);

        return new DateTime(1899, 12, 31);
    }
}
