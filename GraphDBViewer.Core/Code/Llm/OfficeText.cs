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
///An Excel workbook or a Word document read out as words, which is how a model is sent either: no provider takes one as
///a file. Ported from GDS Viewer's OfficeText (2026-11-02), for the files a question in Ask AI carries.
///
///<strong>Read in the page</strong>: an .xlsx or a .docx is a zip of XML parts, which .NET opens in the browser as it is.
///What comes out is every sheet a row to a line, the row's number first and its cells between tabs, so an answer can say
///"row 214"; a document's paragraphs and tables, with its headers and footers. Knowledge-graph generation has a reader of
///its own (<see cref="SourceTextExtractor"/>), which joins a row's cells with commas and numbers nothing, since what it
///writes is read for the things it names rather than asked about line by line.
///
///<strong>Values as they mean, not as Excel dresses them.</strong> A date reads as a date, a time as a time and a
///percentage as one; every other number reads as the number it is, without the decimals, separators or currency its
///format adds. A formula reads as what Excel last worked it out to, which the file keeps beside it, or as the formula
///itself when the file has none, as a workbook a script wrote often has not. The numbers Word puts in front of a list
///item or a heading are drawn by Word and are not in the file's text, so they are not in this either.
///
///The older binary .xls and .doc are another format altogether, and are not read.
///</summary>
public static class OfficeText
{
    ///<summary>
    ///The most one part of a file is unpacked to. A zip says how big its parts are, and can be lying: a few megabytes can
    ///unpack to gigabytes, in a page that has to keep running.
    ///</summary>
    public const long MostPartBytes = 100L * 1024 * 1024;

    ///<summary>Excel's main namespace, as it writes it and as its Strict format does.</summary>
    private static readonly string[] SheetSpaces = { "http://schemas.openxmlformats.org/spreadsheetml/2006/main", "http://purl.oclc.org/ooxml/spreadsheetml/main" };

    private static readonly string[] WordSpaces = { "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "http://purl.oclc.org/ooxml/wordprocessingml/main" };

    private static readonly string[] RelationSpaces = { "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "http://purl.oclc.org/ooxml/officeDocument/relationships" };

    ///<summary>Markup compatibility: a part written two ways, for an Office that knows the newer and one that does not.</summary>
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, CloseInput = true };

    ///<summary>What a number's format makes of it: all that a cell's style is read for.</summary>
    private enum Look
    {
        Plain,
        Date,
        Time,
        DateTime,

        ///<summary>Hours past a day, as [h]:mm does: a duration rather than a time of day.</summary>
        Elapsed,

        Percent
    }

    private sealed record Relation(string Id, string Type, string Target);

    #region A workbook

    ///<summary>
    ///Every sheet, a row to a line: its number, then its cells between tabs. The number is there so an answer can say "row
    ///214" and be found again; counting rows is not something a model does well. A row with nothing in it is left out, and
    ///so are the empty cells at the end of one.
    ///
    ///<strong>Read no further than <paramref name="most"/> characters</strong>, which is all of it that would be sent: a
    ///sheet of a million rows stops being read there, and Cut says so.
    ///</summary>
    public static (string Text, bool Cut) Workbook(byte[] data, int most)
    {
        using var package = new Package(data);

        var book = package.Main("xl/workbook.xml");
        var root = package.Load(book);
        var relations = package.Relations(book);
        var date1904 = IsTrue(ChildOf(root, "workbookPr")?.Attribute("date1904"));
        var strings = new List<string>();
        var looks = new List<Look>();

        if (relations.FirstOrDefault(each => each.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)) is Relation shared && package.Has(shared.Target))
            strings = SharedStrings(package, shared.Target);

        if (relations.FirstOrDefault(each => each.Type.EndsWith("/styles", StringComparison.Ordinal)) is Relation styles && package.Has(styles.Target))
            looks = CellLooks(package.Load(styles.Target));

        var text = new StringBuilder();

        foreach (var sheet in ChildOf(root, "sheets")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var name = (string)sheet.Attribute("name") ?? "";
            var id = RelationId(sheet);
            var part = relations.FirstOrDefault(each => each.Id == id);

            if (part == null || !package.Has(part.Target))
                continue;

            var hidden = "";

            if ((string)sheet.Attribute("state") is "hidden" or "veryHidden")
                hidden = " (hidden)";

            if (!part.Type.EndsWith("/worksheet", StringComparison.Ordinal))
            {
                text.Append($"Sheet \"{name}\"{hidden} is a chart, with no cells to read.\n\n");

                continue;
            }

            text.Append($"Sheet \"{name}\"{hidden} - each line is a row: its number, then its cells, separated by tabs.\n");

            if (Rows(package, part.Target, strings, looks, date1904, text, most))
                return (text.ToString(0, most), true);

            text.Append('\n');
        }

        return (text.ToString(), false);
    }

    ///<summary>The workbook's strings, which its cells name by number: each one's runs together, less the phonetic guide Japanese text carries above itself.</summary>
    private static List<string> SharedStrings(Package package, string path)
    {
        var strings = new List<string>();

        using var reader = XmlReader.Create(package.Open(path), Settings);

        reader.MoveToContent();

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                strings.Add(Runs((XElement)XNode.ReadFrom(reader)));
            else
                reader.Read();
        }

        return strings;
    }

    ///<summary>The words of a string with runs in it, or of one without: every t under it, outside a phonetic run.</summary>
    private static string Runs(XElement holder)
    {
        var text = new StringBuilder();

        if (holder == null)
            return "";

        foreach (var element in holder.Descendants())
            if (element.Name.LocalName == "t" && !element.Ancestors().Any(above => above.Name.LocalName == "rPh"))
                text.Append(element.Value);

        return text.ToString();
    }

    ///<summary>What each cell style does to a number, by the style's number, which is what a cell's s says.</summary>
    private static List<Look> CellLooks(XElement styles)
    {
        var codes = new Dictionary<int, string>();
        var looks = new List<Look>();

        foreach (var format in ChildOf(styles, "numFmts")?.Elements() ?? Enumerable.Empty<XElement>())
            if (int.TryParse((string)format.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                codes[id] = (string)format.Attribute("formatCode") ?? "";

        foreach (var style in ChildOf(styles, "cellXfs")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            int.TryParse((string)style.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);

            looks.Add(LookOf(id, codes.GetValueOrDefault(id)));
        }

        return looks;
    }

    ///<summary>
    ///What a number format shows a number as. Its own code when the file gives one; otherwise one of the formats Excel has
    ///built in, which a file names by number alone and which differ by country only in their order.
    ///</summary>
    private static Look LookOf(int id, string code)
    {
        if (code != null)
            return LookOfCode(code);

        if (id == 9 || id == 10)
            return Look.Percent;
        else if (id == 22)
            return Look.DateTime;
        else if (id == 46)
            return Look.Elapsed;
        else if ((id >= 18 && id <= 21) || id == 45 || id == 47)
            return Look.Time;
        else if ((id >= 14 && id <= 17) || (id >= 27 && id <= 36) || (id >= 50 && id <= 58))
            return Look.Date;

        return Look.Plain;
    }

    ///<summary>
    ///A format code read for what it shows, after what is only decoration is taken out: quoted words, an escaped
    ///character, the spacing and fill marks, and anything in brackets (a color, a currency, a condition) except a [h], [m]
    ///or [s], which counts time past a day. Only the first of its sections, the one for a number above zero.
    ///</summary>
    private static Look LookOfCode(string code)
    {
        var bare = new StringBuilder();
        var elapsed = false;

        for (int i = 0; i < code.Length; i++)
        {
            var c = code[i];

            if (c == ';')
                break;
            else if (c == '"')
            {
                var end = code.IndexOf('"', i + 1);

                if (end < 0)
                    break;

                i = end;
            }
            else if (c == '\\' || c == '_' || c == '*')
                i++;
            else if (c == '[')
            {
                var end = code.IndexOf(']', i + 1);

                if (end < 0)
                    break;

                var inside = code.Substring(i + 1, end - i - 1).ToLowerInvariant();

                if (inside.Length > 0 && inside.All(letter => letter == 'h' || letter == 'm' || letter == 's'))
                    elapsed = true;

                i = end;
            }
            else
                bare.Append(char.ToLowerInvariant(c));
        }

        var shown = bare.ToString();

        if (shown.Contains('%'))
            return Look.Percent;

        if (elapsed)
            return Look.Elapsed;

        var time = shown.Contains('h') || shown.Contains('s') || shown.Contains("am/pm") || shown.Contains("a/p");
        var date = shown.Contains('y') || shown.Contains('d') || (shown.Contains('m') && !time);

        if (date && time)
            return Look.DateTime;
        else if (date)
            return Look.Date;
        else if (time)
            return Look.Time;

        return Look.Plain;
    }

    ///<summary>One sheet's rows into the text, until it passes <paramref name="most"/>: true when it did, and the rest was not read.</summary>
    private static bool Rows(Package package, string path, List<string> strings, List<Look> looks, bool date1904, StringBuilder text, int most)
    {
        using var reader = XmlReader.Create(package.Open(path), Settings);

        var number = 0;

        reader.MoveToContent();

        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                reader.Read();

                continue;
            }

            var row = (XElement)XNode.ReadFrom(reader);
            var cells = new List<string>();
            var column = -1;

            //Both are optional, and mean the next one when left out.
            number = Whole(row.Attribute("r"), number + 1);

            foreach (var cell in row.Elements().Where(each => each.Name.LocalName == "c"))
            {
                column = ColumnOf((string)cell.Attribute("r"), column + 1);

                while (cells.Count < column)
                    cells.Add("");

                var value = CellText(cell, strings, looks, date1904);

                if (column < cells.Count)
                    cells[column] = value;
                else
                    cells.Add(value);
            }

            var line = string.Join('\t', cells).TrimEnd('\t');

            if (line.Length == 0)
                continue;

            text.Append(number.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(line).Append('\n');

            if (text.Length > most)
                return true;
        }

        return false;
    }

    ///<summary>What a cell reads as, on one line: a tab or a line break inside it would read as the end of it, or of the row.</summary>
    private static string CellText(XElement cell, List<string> strings, List<Look> looks, bool date1904)
    {
        var type = (string)cell.Attribute("t") ?? "n";
        var value = (string)ChildOf(cell, "v");
        string text;

        if (type == "inlineStr")
            text = Runs(ChildOf(cell, "is"));
        else if (value == null)
            text = Formula(cell);
        else if (type == "s")
        {
            text = "";

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < strings.Count)
                text = strings[index];
        }
        else if (type == "b")
        {
            text = "FALSE";

            if (value == "1")
                text = "TRUE";
        }
        else if (type == "e" || type == "str" || type == "d")
            text = value;
        else
        {
            var style = Whole(cell.Attribute("s"), 0);
            var look = Look.Plain;

            if (style >= 0 && style < looks.Count)
                look = looks[style];

            text = Number(value, look, date1904);
        }

        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
    }

    ///<summary>A formula the file keeps no result for, as Excel's box shows it, or nothing, for one that only shares another cell's.</summary>
    private static string Formula(XElement cell)
    {
        var written = ((string)ChildOf(cell, "f") ?? "").Trim();

        if (written.Length == 0)
            return "";

        return "=" + written;
    }

    ///<summary>
    ///A number as its format means it. A date or a time from Excel's count of days, since the last day of 1899, or since
    ///1904 in a workbook that says so, to the nearest second; a percentage times a hundred; anything else as the number it
    ///is, to the fifteen digits Excel keeps, so 0.1 + 0.2 is 0.3 here as it is there.
    ///</summary>
    private static string Number(string written, Look look, bool date1904)
    {
        if (!double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return written;

        if (look == Look.Percent)
            return Plain(value * 100) + "%";

        if (look == Look.Plain || value < 0 || value > 2_958_465)
            return Plain(value);

        var seconds = (long)Math.Round(value * 86_400);

        if (look == Look.Elapsed)
            return FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");

        //Excel counts a 29 February 1900 that never was, so its days before it start a day later.
        var start = new DateTime(1899, 12, 30);

        if (date1904)
            start = new DateTime(1904, 1, 1);
        else if (value < 60)
            start = new DateTime(1899, 12, 31);

        var when = start.AddSeconds(seconds);
        var clock = "HH:mm:ss";

        if (when.Second == 0)
            clock = "HH:mm";

        if (look == Look.Date)
            return when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (look == Look.Time)
            return when.ToString(clock, CultureInfo.InvariantCulture);

        return when.ToString("yyyy-MM-dd " + clock, CultureInfo.InvariantCulture);
    }

    private static string Plain(double value)
    {
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    ///<summary>A cell reference's column, from 0 (B7 is 1), or <paramref name="otherwise"/> when it has none.</summary>
    private static int ColumnOf(string reference, int otherwise)
    {
        var column = 0;
        var letters = 0;

        foreach (var c in reference ?? "")
        {
            var upper = char.ToUpperInvariant(c);

            if (upper < 'A' || upper > 'Z')
                break;

            column = column * 26 + (upper - 'A' + 1);
            letters++;
        }

        //Excel's last column is XFD.
        if (letters == 0 || letters > 3 || column > 16_384)
            return otherwise;

        return column - 1;
    }

    #endregion

    #region A document

    ///<summary>
    ///A Word document's words: its paragraphs a line each, and its tables a row to a line with the cells between tabs, as a
    ///workbook's are. What is in a text box follows the paragraph it is anchored to. The page header and footer come first
    ///and last, said once each however many sections repeat them.
    ///
    ///<strong>Only what the document says now</strong>: words deleted with changes tracked are left out and words inserted
    ///are in, and a field (a page number, a date, a cross-reference) reads as what Word last showed, not its code.
    ///</summary>
    public static string Document(byte[] data)
    {
        using var package = new Package(data);

        var main = package.Main("word/document.xml");
        var root = package.Load(main);
        var relations = package.Relations(main);
        var text = new StringBuilder();

        text.Append(Margins(package, root, relations, "headerReference", "[Page header] "));
        text.Append('\n');

        if (WordChild(root, "body") is XElement body)
            Blocks(body, text);

        text.Append('\n');
        text.Append(Margins(package, root, relations, "footerReference", "[Page footer] "));

        return Tidy(text.ToString());
    }

    ///<summary>The headers, or the footers, each a line after <paramref name="label"/>: one a section names, read once however many name it, and one that says what another already said left out.</summary>
    private static string Margins(Package package, XElement root, List<Relation> relations, string reference, string label)
    {
        var text = new StringBuilder();
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var said = new HashSet<string>(StringComparer.Ordinal);

        foreach (var named in root.Descendants().Where(each => IsWord(each, reference)))
        {
            var id = RelationId(named);
            var part = relations.FirstOrDefault(each => each.Id == id);

            if (part == null || !read.Add(part.Target) || !package.Has(part.Target))
                continue;

            var words = new StringBuilder();

            Blocks(package.Load(part.Target), words);

            var line = OneLine(words.ToString());

            if (line.Length > 0 && said.Add(line))
                text.Append(label).Append(line).Append('\n');
        }

        return text.ToString();
    }

    ///<summary>What a body, a cell, a text box or a header holds, in order: paragraphs and tables, and whatever wraps them.</summary>
    private static void Blocks(XElement holder, StringBuilder text)
    {
        foreach (var child in holder.Elements())
        {
            if (IsWord(child, "p"))
                Paragraph(child, text);
            else if (IsWord(child, "tbl"))
                Table(child, text);
            else if (IsCompatibility(child, "AlternateContent"))
                Blocks(Chosen(child), text);
            else
                Blocks(child, text);
        }
    }

    ///<summary>A paragraph on a line of its own, and then what any text box anchored in it says.</summary>
    private static void Paragraph(XElement holder, StringBuilder text)
    {
        var line = new StringBuilder();
        var boxes = new List<XElement>();

        Inline(holder, line, boxes);

        text.Append(line).Append('\n');

        foreach (var box in boxes)
            Blocks(box, text);
    }

    ///<summary>
    ///The words inside a paragraph, through whatever holds its runs: a link, a field, a tracked insertion, a content
    ///control, a drawing. Not a paragraph's or a run's own settings, where a tab means a tab stop; not what a tracked change
    ///deleted or moved away, tabs and breaks included. A field's code is an element of its own, never read.
    ///</summary>
    private static void Inline(XElement holder, StringBuilder line, List<XElement> boxes)
    {
        foreach (var child in holder.Elements())
        {
            var name = child.Name.LocalName;

            if (IsCompatibility(child, "AlternateContent"))
                Inline(Chosen(child), line, boxes);
            else if (!WordSpaces.Contains(child.Name.NamespaceName))
                Inline(child, line, boxes);
            else if (name == "t")
                line.Append(child.Value);
            else if (name == "tab" || name == "ptab")
                line.Append('\t');
            else if (name == "br" || name == "cr")
                line.Append('\n');
            else if (name == "noBreakHyphen")
                line.Append('-');
            else if (name == "txbxContent")
                boxes.Add(child);
            else if (name != "pPr" && name != "rPr" && name != "del" && name != "moveFrom")
                Inline(child, line, boxes);
        }
    }

    ///<summary>A table a row to a line, its cells between tabs, each cell's paragraphs run together on that line.</summary>
    private static void Table(XElement holder, StringBuilder text)
    {
        foreach (var row in Within(holder, "tr"))
        {
            var cells = new List<string>();

            foreach (var cell in Within(row, "tc"))
            {
                var words = new StringBuilder();

                Blocks(cell, words);
                cells.Add(OneLine(words.ToString()));
            }

            var line = string.Join('\t', cells).TrimEnd('\t');

            if (line.Length > 0)
                text.Append(line).Append('\n');
        }
    }

    ///<summary>The rows of a table, or the cells of a row, including those a content control or custom markup wraps.</summary>
    private static IEnumerable<XElement> Within(XElement holder, string name)
    {
        foreach (var child in holder.Elements())
        {
            if (IsWord(child, name))
                yield return child;
            else if (IsWord(child, "sdt") || IsWord(child, "sdtContent") || IsWord(child, "customXml"))
                foreach (var found in Within(child, name))
                    yield return found;
        }
    }

    ///<summary>The way of writing a part that an Office that knows it reads, the first choice, or the fallback when there is no choice.</summary>
    private static XElement Chosen(XElement alternatives)
    {
        foreach (var child in alternatives.Elements())
            if (IsCompatibility(child, "Choice"))
                return child;

        return alternatives.Elements().FirstOrDefault(each => IsCompatibility(each, "Fallback")) ?? new XElement("none");
    }

    ///<summary>Text on one line, its breaks and tabs spaces and its runs of spaces one.</summary>
    private static string OneLine(string text)
    {
        return string.Join(' ', text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    ///<summary>The text with no spaces at the ends of its lines, no more than one empty line in a row, and none at either end.</summary>
    private static string Tidy(string text)
    {
        var kept = new StringBuilder();
        var empty = false;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0)
            {
                empty = kept.Length > 0;

                continue;
            }

            if (empty)
                kept.Append('\n');

            kept.Append(trimmed).Append('\n');
            empty = false;
        }

        return kept.ToString().TrimEnd('\n');
    }

    private static bool IsWord(XElement element, string name)
    {
        return element.Name.LocalName == name && WordSpaces.Contains(element.Name.NamespaceName);
    }

    private static XElement WordChild(XElement element, string name)
    {
        return element.Elements().FirstOrDefault(child => IsWord(child, name));
    }

    private static bool IsCompatibility(XElement element, string name)
    {
        return element.Name.LocalName == name && element.Name.NamespaceName == Compatibility;
    }

    #endregion

    #region What the bytes are

    ///<summary>
    ///The name of the root of a package's main part, "workbook" for a workbook and "document" for a Word document, or null
    ///when the bytes are not an Office package: what a file is turned away by when it is not what its name says.
    ///</summary>
    public static string MainElement(byte[] data)
    {
        try
        {
            using var package = new Package(data);

            var main = package.Main("");

            if (!package.Has(main))
                return null;

            using var reader = XmlReader.Create(package.Open(main), Settings);

            reader.MoveToContent();

            return reader.LocalName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region Reading the package

    ///<summary>The child of a spreadsheet element with a name, whichever namespace it was written in.</summary>
    private static XElement ChildOf(XElement element, string name)
    {
        return element?.Elements().FirstOrDefault(child => child.Name.LocalName == name && SheetSpaces.Contains(child.Name.NamespaceName));
    }

    ///<summary>The r:id an element names another part by, or "" when it names none.</summary>
    private static string RelationId(XElement element)
    {
        foreach (var attribute in element.Attributes())
            if (attribute.Name.LocalName == "id" && RelationSpaces.Contains(attribute.Name.NamespaceName))
                return attribute.Value;

        return "";
    }

    private static bool IsTrue(XAttribute attribute)
    {
        return (string)attribute is "1" or "true";
    }

    private static int Whole(XAttribute attribute, int otherwise)
    {
        if (int.TryParse((string)attribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;

        return otherwise;
    }

    ///<summary>
    ///An Office file as the zip it is: its parts by name, the case a writer used ignored, as the format says, and what each
    ///part's relationships name, which is how one part finds another.
    ///</summary>
    private sealed class Package : IDisposable
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _parts = new(StringComparer.OrdinalIgnoreCase);

        public Package(byte[] data)
        {
            _zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);

            foreach (var entry in _zip.Entries)
                _parts[entry.FullName.Replace('\\', '/').TrimStart('/')] = entry;
        }

        public bool Has(string path)
        {
            return _parts.ContainsKey(path);
        }

        ///<summary>The part the package says is its main one, or <paramref name="otherwise"/> when it does not say.</summary>
        public string Main(string otherwise)
        {
            foreach (var relation in Relations(""))
                if (relation.Type.EndsWith("/officeDocument", StringComparison.Ordinal))
                    return relation.Target;

            return otherwise;
        }

        ///<summary>A part, unpacked no further than <see cref="MostPartBytes"/>.</summary>
        public Stream Open(string path)
        {
            if (!_parts.TryGetValue(path, out var entry))
                throw new InvalidDataException($"it has no {path}");

            if (entry.Length > MostPartBytes)
                throw new InvalidDataException($"its {path} unpacks to {AiAttachment.Size(entry.Length)}, and a part can be read at {AiAttachment.Size(MostPartBytes)} at most");

            return new Bounded(entry.Open(), path);
        }

        public XElement Load(string path)
        {
            using var reader = XmlReader.Create(Open(path), Settings);

            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

            return document.Root ?? throw new InvalidDataException($"its {path} is empty");
        }

        ///<summary>What a part's relationships name, or the package's own for "", with each target made a path from the root.</summary>
        public List<Relation> Relations(string part)
        {
            var folder = "";
            var file = part;
            var slash = part.LastIndexOf('/');

            if (slash >= 0)
            {
                folder = part.Substring(0, slash + 1);
                file = part.Substring(slash + 1);
            }

            var path = folder + "_rels/" + file + ".rels";
            var relations = new List<Relation>();

            if (!Has(path))
                return relations;

            foreach (var relation in Load(path).Elements().Where(each => each.Name.LocalName == "Relationship"))
            {
                if ((string)relation.Attribute("TargetMode") == "External")
                    continue;

                var target = (string)relation.Attribute("Target") ?? "";

                relations.Add(new Relation((string)relation.Attribute("Id") ?? "", (string)relation.Attribute("Type") ?? "", Resolved(folder, target)));
            }

            return relations;
        }

        public void Dispose()
        {
            _zip.Dispose();
        }

        ///<summary>A target from the folder of the part that names it, or from the root when it starts with a slash.</summary>
        private static string Resolved(string folder, string target)
        {
            var path = new List<string>();
            var whole = folder + target;

            if (target.StartsWith('/'))
                whole = target;

            foreach (var step in Uri.UnescapeDataString(whole).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (step == "..")
                {
                    if (path.Count > 0)
                        path.RemoveAt(path.Count - 1);
                }
                else if (step != ".")
                    path.Add(step);
            }

            return string.Join('/', path);
        }
    }

    ///<summary>A part read no further than <see cref="MostPartBytes"/> unpacked, whatever its zip entry claimed.</summary>
    private sealed class Bounded : Stream
    {
        private readonly Stream _inner;
        private readonly string _path;
        private long _read;

        public Bounded(Stream inner, string path)
        {
            _inner = inner;
            _path = path;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var got = _inner.Read(buffer, offset, count);

            _read += got;

            if (_read > MostPartBytes)
                throw new InvalidDataException($"its {_path} unpacks to more than {AiAttachment.Size(MostPartBytes)}, the most a part can be read at");

            return got;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }

    #endregion
}
