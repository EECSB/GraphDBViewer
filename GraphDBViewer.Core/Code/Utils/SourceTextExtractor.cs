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
///Turns an uploaded file into plain text: since 2026-11-02, a PDF Ask AI sends as its words to a model set to take
///them (<see cref="AiAttachment.ReadPdf"/>), as knowledge-graph generation's window once did every file. Office formats that are
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
    ///<summary>
    ///The file as text, or a clear reason it can't be. Never both. What the file is read as agrees with
    ///<see cref="AiAttachment.KindOf"/> (2026-11-07): a PDF is one by its type, <c>application/pdf</c>, since a browser
    ///may name a file dragged out of another page by its type alone, or by its extension; the Office formats by theirs.
    ///</summary>
    public static (string Text, string Error) Extract(string fileName, MemoryStream content, string mediaType = null)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var type = (mediaType ?? "").Trim().ToLowerInvariant();

        if (type == "application/pdf" || extension == ".pdf")
            return ExtractVia(fileName, content, "PDF", FromPdf);
        else if (extension == ".docx")
            return ExtractVia(fileName, content, "Word", FromDocx);
        else if (extension == ".xlsx")
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

        var styles = XlsxTable.DateStyles(zip);
        var epoch = XlsxTable.Epoch(zip);

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
    ///the stored value untouched otherwise (<see cref="XlsxTable.TryDate"/>).
    ///</summary>
    private static string Cell(string value, string type, string style, IReadOnlyDictionary<int, bool> styles, DateTime epoch)
    {
        if (XlsxTable.TryDate(value, type, style, styles, epoch, out var date))
            return date;

        return value;
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
            if (text[i] == '\0' || text[i] == '�')
                suspicious++;

        return suspicious > sample / 100;
    }
}
