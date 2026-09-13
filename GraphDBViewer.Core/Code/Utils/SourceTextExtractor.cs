using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///Turns an uploaded file into plain text for knowledge-graph generation. Office formats that are
///really ZIP + XML (.docx, .xlsx) are unpacked with the framework's own ZipArchive/XDocument — no new
///dependency — PDFs go through PdfPig, and anything else is read as text with a binary sniff, so any
///text-based format (.csv, .json, .log, source code, …) just works. Pure; the caller hands in a
///seekable stream.
///
///PDF used to be refused, on the grounds that reading one needs a parser "the browser" does not have.
///That was a statement about this project rather than about WebAssembly: PdfPig is managed code and
///runs here like everything else.
///</summary>
public static class SourceTextExtractor
{
    ///<summary>The file as text, or a clear reason it can't be. Never both.</summary>
    public static (string Text, string Error) Extract(string fileName, MemoryStream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension == ".pdf")
            return ExtractVia(fileName, content, "PDF", FromPdf);

        if (extension == ".docx")
            return ExtractVia(fileName, content, "Word", FromDocx);

        if (extension == ".xlsx")
            return ExtractVia(fileName, content, "Excel", FromXlsx);

        content.Position = 0;
        using var reader = new StreamReader(content);
        var raw = reader.ReadToEnd();

        if (LooksLikeBinary(raw))
            return (null, $"\"{fileName}\" doesn't look like a text file.");

        if (string.IsNullOrWhiteSpace(raw))
            return (null, $"\"{fileName}\" contains no readable text.");

        return (raw.Trim(), null);
    }

    ///<summary>
    ///The text of a PDF, page by page.
    ///
    ///ContentOrderTextExtractor follows the document's own content order rather than stitching words
    ///together by position, which is what keeps a two-column page from interleaving its columns into
    ///nonsense. It is still a flattening: a PDF's layout carries meaning that plain text cannot, and
    ///tables come out as runs of words. Good enough to pull entities and relationships out of, which is
    ///all this is for.
    ///
    ///A page that cannot be read is skipped rather than failing the file: one broken page in a hundred
    ///should cost that page, not the document.
    ///</summary>
    private static string FromPdf(Stream content)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(content);
        var sb = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            string text;

            try
            {
                text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
            }
            catch (Exception)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static (string Text, string Error) ExtractVia(string fileName, MemoryStream content, string kind, Func<Stream, string> extractor)
    {
        string text;

        try
        {
            content.Position = 0;
            text = extractor(content);
        }
        catch (Exception)
        {
            return (null, $"Could not read \"{fileName}\" as a {kind} file.");
        }

        if (string.IsNullOrWhiteSpace(text))
            return (null, $"\"{fileName}\" contains no readable text.");

        return (text, null);
    }

    ///<summary>The document's paragraph text — every w:t run, one line per w:p.</summary>
    public static string FromDocx(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("word/document.xml");

        if (entry == null)
            return null;

        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var sb = new StringBuilder();

        foreach (var paragraph in doc.Descendants(w + "p"))
        {
            foreach (var text in paragraph.Descendants(w + "t"))
                sb.Append(text.Value);

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    ///<summary>
    ///Every sheet's rows as comma-joined lines, shared strings resolved and date cells written as
    ///dates — the same minimal OpenXML the app's own ExcelExport writes, read back, plus what a real
    ///spreadsheet has that the app's own export does not.
    ///
    ///A spreadsheet's numbers are the one file format that needs no locale guessing: OOXML stores a
    ///number as an invariant decimal in its <c>&lt;v&gt;</c> whatever the machine that wrote it was set
    ///to, so <c>1,5</c> typed on a Slovene machine is <c>1.5</c> in the file. **A date is the opposite**
    ///— it is stored as a bare serial number and only a style makes it a date, so reading the value
    ///alone hands a model <c>45000</c> where the spreadsheet plainly showed 2023-03-15. That is a wrong
    ///fact reaching an extraction, not a formatting nicety, which is why the styles are read.
    ///</summary>
    public static string FromXlsx(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var shared = new List<string>();
        var sharedEntry = zip.GetEntry("xl/sharedStrings.xml");

        if (sharedEntry != null)
        {
            using var stream = sharedEntry.Open();
            var doc = XDocument.Load(stream);

            foreach (var si in doc.Root.Elements(ns + "si"))
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        }

        var styles = DateStyles(zip, ns);
        var epoch = Epoch(zip, ns);

        var sb = new StringBuilder();
        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => e.FullName);

        foreach (var entry in sheets)
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);

            foreach (var row in doc.Descendants(ns + "row"))
            {
                var cells = new List<string>();

                foreach (var cell in row.Elements(ns + "c"))
                {
                    var value = cell.Element(ns + "v")?.Value;

                    if (value == null)
                    {
                        //Inline string cell.
                        var inline = cell.Element(ns + "is");

                        if (inline != null)
                            cells.Add(string.Concat(inline.Descendants(ns + "t").Select(t => t.Value)));

                        continue;
                    }

                    if ((string)cell.Attribute("t") == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count)
                        cells.Add(shared[index]);
                    else
                        cells.Add(Cell(value, (string)cell.Attribute("t"), (string)cell.Attribute("s"), styles, epoch));
                }

                if (cells.Count > 0)
                    sb.AppendLine(string.Join(", ", cells));
            }
        }

        return sb.ToString().Trim();
    }

    ///<summary>
    ///One cell's value as text: a serial number written as a date when its style says it is one, and
    ///the stored value untouched otherwise. A cell whose type says it is text is never a serial, even
    ///when it carries a date style.
    ///</summary>
    private static string Cell(string value, string type, string style, IReadOnlyDictionary<int, bool> styles, DateTime epoch)
    {
        if (type != null && type != "n")
            return value;

        if (style == null || !int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return value;

        if (!styles.TryGetValue(index, out var isDate) || !isDate)
            return value;

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            return value;

        var when = Moment(serial, epoch);

        if (when == null)
            return value;

        //ISO 8601, and the time only when there is one — a date cell with a zero fraction is a date.
        if (when.Value.TimeOfDay == TimeSpan.Zero)
            return when.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return when.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
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
    private static Dictionary<int, bool> DateStyles(ZipArchive zip, XNamespace ns)
    {
        var styles = new Dictionary<int, bool>();
        var entry = zip.GetEntry("xl/styles.xml");

        if (entry == null)
            return styles;

        XDocument doc;

        using (var stream = entry.Open())
            doc = XDocument.Load(stream);

        //The custom formats, by id. Anything not here is one of the built-ins.
        var custom = new Dictionary<int, string>();

        foreach (var format in doc.Descendants(ns + "numFmt"))
        {
            if (int.TryParse((string)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                custom[id] = (string)format.Attribute("formatCode") ?? "";
        }

        var cellXfs = doc.Descendants(ns + "cellXfs").FirstOrDefault();

        if (cellXfs == null)
            return styles;

        var index = 0;

        foreach (var xf in cellXfs.Elements(ns + "xf"))
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

            if (c == '[')
            {
                var close = code.IndexOf(']', i);

                if (close < 0)
                    return false;

                //[h], [m] and [s] are elapsed-time tokens, which are durations rather than dates.
                i = close;

                continue;
            }

            //"m" is minutes or months depending on where it sits; either way the cell is a moment.
            if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M')
                return true;
        }

        return false;
    }

    ///<summary>
    ///Day zero for this workbook. Excel's default is the 1900 system, whose serial 1 is 1900-01-01, so
    ///day zero is 1899-12-31; a workbook saved by an older Mac Excel says <c>date1904</c> and counts
    ///from 1904-01-01.
    ///</summary>
    private static DateTime Epoch(ZipArchive zip, XNamespace ns)
    {
        var entry = zip.GetEntry("xl/workbook.xml");

        if (entry == null)
            return new DateTime(1899, 12, 31);

        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var properties = doc.Descendants(ns + "workbookPr").FirstOrDefault();
            var flag = (string)properties?.Attribute("date1904");

            if (flag == "1" || flag == "true")
                return new DateTime(1904, 1, 1);
        }
        catch (System.Xml.XmlException)
        {
            //A workbook part this reader cannot parse is not a reason to fail the file.
        }

        return new DateTime(1899, 12, 31);
    }


    ///<summary>True when decoded text is riddled with NULs or replacement characters — a binary file
    ///opened as text, not something a model should ever see.</summary>
    public static bool LooksLikeBinary(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        int suspicious = 0;
        int sample = Math.Min(text.Length, 4000);

        for (int i = 0; i < sample; i++)
        {
            if (text[i] == '\0' || text[i] == '�')
                suspicious++;
        }

        return suspicious > sample / 100;
    }
}
