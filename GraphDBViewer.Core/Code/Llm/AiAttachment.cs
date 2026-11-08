using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GraphDBViewerWeb.Code;

///<summary>What a file is to Ask AI, which decides how it reaches the model.</summary>
public enum AttachmentKind
{
    ///<summary>Nothing the assistant can read.</summary>
    Unsupported,

    ///<summary>PNG, JPEG, GIF or WebP, sent as it is.</summary>
    Image,

    ///<summary>Sent as it is to a model that reads one: it reads the text and sees each page.</summary>
    Pdf,

    ///<summary>Anything that is text already, a CSV, a GraphML export, a query, sent as its words.</summary>
    Text,

    ///<summary>An Excel workbook, sent as its words: every sheet, a row to a line (<see cref="OfficeText.Workbook"/>).</summary>
    Spreadsheet,

    ///<summary>A Word document, sent as its words: its paragraphs and tables (<see cref="OfficeText.Document"/>).</summary>
    Document
}

///<summary>
///One piece of what a question's files become in a request: words, or a picture or a PDF as base64. Each provider writes
///these in its own shape (<see cref="AiAttachment.Parts"/>).
///</summary>
public sealed record AttachmentPart(AttachmentKind Kind, string Text, string MediaType = "", string Base64 = "", string Name = "");

///<summary>
///A file sent with a question: a screenshot pasted into the box, a spreadsheet of parts, a GraphML export to compare
///against the graph.
///
///<strong>Ported from GDS Viewer's AiAttachment</strong> (2026-11-02), itself ConfigurationRuleEditor's: its kinds, its
///limits and its reasons for turning a file away. An Excel workbook and a Word document are read in the page
///(<see cref="OfficeText"/>) into the words that are sent; the older binary .xls and .doc are not.
///
///<strong>Every question sends the whole conversation again</strong>, its files included: no provider keeps anything
///between requests. That is what the limits on a conversation are sized against, and why a file can be unticked in the
///files panel (<see cref="Excluded"/>): it stays on screen, and stops being sent.
///</summary>
public sealed class AiAttachment
{
    ///<summary>The most files one question can carry.</summary>
    public const int MostPerQuestion = 10;

    ///<summary>Anthropic's ceiling for one picture.</summary>
    public const int MostImageBytes = 5 * 1024 * 1024;

    ///<summary>For a PDF or a text file.</summary>
    public const int MostFileBytes = 10 * 1024 * 1024;

    ///<summary>
    ///Every file one conversation sends, together.
    ///
    ///They go again with every question, as base64, a third bigger, straight from this page to the provider: 12 MB is 16
    ///as base64, under Anthropic's 32 MB request and OpenAI's 50 MB of files a request. GDS Viewer's figure, set when
    ///Google took 20 MB in one request; its OpenAI-compatible endpoint, which this app reaches Gemini through, documents no
    ///limit of its own (read 2026-10-29), so 12 stays until one is tried there.
    ///</summary>
    public const int MostConversationBytes = 12 * 1024 * 1024;

    ///<summary>
    ///How much of one file's text goes into the prompt: about 57,000 tokens. Every later question sends it again, so an
    ///export of a million lines would go on costing that for the rest of the conversation. Past it the model is told
    ///where the file was cut.
    ///</summary>
    public const int MostTextCharacters = 200_000;

    ///<summary>About what a picture costs: the most Anthropic charges for one it has scaled to fit.</summary>
    public const int ImageTokens = 1600;

    ///<summary>About what a page of a PDF costs: its text, and a picture of the page.</summary>
    public const int PdfPageTokens = 2000;

    ///<summary>About what a page of a PDF costs as its words alone, until they are read (<see cref="AsText"/>).</summary>
    public const int PdfPageTextTokens = 500;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    private static readonly string[] ImageTypes = { "image/png", "image/jpeg", "image/gif", "image/webp" };

    ///<summary>Text anybody sends, which a browser mostly has a type for.</summary>
    private static readonly string[] CommonText = { ".txt", ".csv", ".tsv", ".json", ".xml", ".md", ".log", ".yaml", ".yml", ".ini", ".htm", ".html" };

    ///<summary>A graph written out, which few browsers have a type for: the formats this viewer imports and exports, and RDF's.</summary>
    private static readonly string[] GraphText = { ".graphml", ".gexf", ".gml", ".dot", ".gv", ".jsonl", ".ndjson", ".jsonld", ".ttl", ".nt", ".nq", ".trig", ".rdf", ".owl" };

    ///<summary>Queries, in the languages the editor writes, and SQL beside them.</summary>
    private static readonly string[] QueryText = { ".gremlin", ".groovy", ".cypher", ".cql", ".aql", ".dql", ".sparql", ".rq", ".sql" };

    ///<summary>An Excel workbook, with or without macros, which are never run, only the cells read.</summary>
    private static readonly string[] SpreadsheetFiles = { ".xlsx", ".xlsm" };

    private static readonly string[] DocumentFiles = { ".docx" };

    ///<summary>Office files this page does not read: the binary formats from before 2007, and a PowerPoint deck.</summary>
    private static readonly string[] OfficeFiles = { ".xls", ".doc", ".pptx", ".ppt" };

    private static readonly string[] SpreadsheetTypes = { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.ms-excel.sheet.macroenabled.12" };

    private const string DocumentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    ///<summary>What an Office file from before 2007 starts with, and one locked with a password, which Office wraps the same way.</summary>
    private static readonly byte[] CompoundFile = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private static int _lastId;

    private string _text;
    private string _unreadable;
    private bool _goesOn;
    private int _pages = -1;
    private string _pdfText;
    private string _pdfUnreadable;

    public AiAttachment(string name, string mediaType, byte[] data)
    {
        Name = name;
        MediaType = mediaType;
        Data = data;
        Id = Interlocked.Increment(ref _lastId);
    }

    ///<summary>A number of its own, for the page to keep a chip and a tick to one file however many share a name.</summary>
    public int Id { get; }

    ///<summary>As the person's machine named it. Shown on the chip, and given to the model so a question can say which file it means.</summary>
    public string Name { get; }

    ///<summary>What the browser said the file is. Often nothing, as for a GraphML export, so the name decides first.</summary>
    public string MediaType { get; }

    public byte[] Data { get; }

    ///<summary>
    ///Unticked in the files panel: on screen still, so it can be ticked again, and not sent. The model is told in one line
    ///that it was there (<see cref="Parts"/>) rather than finding a question about a file it never saw.
    ///</summary>
    public bool Excluded { get; set; }

    ///<summary>
    ///The same file again, unticked: what a compaction keeps on its briefing (<see cref="AiChat.CompactAsync"/>). Another
    ///of it rather than this one, so the question it went with goes on showing it as it was sent; the bytes are shared,
    ///not copied.
    ///</summary>
    public AiAttachment Unticked()
    {
        return new AiAttachment(Name, MediaType, Data) { Excluded = true };
    }

    public AttachmentKind Kind => KindOf(Name, MediaType);

    ///<summary>
    ///A PDF sent as the words read out of it here (<see cref="ReadPdf"/>) rather than as the file, as the model picked says
    ///(<see cref="LlmConnection.PdfAsText"/>). Set by the window at every question, as <see cref="Excluded"/> is by the files
    ///panel, so another model picked sends the same file its own way.
    ///</summary>
    public bool AsText { get; set; }

    ///<summary>What the file goes to the model as: a PDF sent as its words is text.</summary>
    public AttachmentKind SentKind
    {
        get
        {
            if (Kind == AttachmentKind.Pdf && AsText)
                return AttachmentKind.Text;

            return Kind;
        }
    }

    ///<summary>Whether a PDF's words have been read out of it (<see cref="ReadPdf"/>), whether or not there were any.</summary>
    public bool PdfRead => _pdfText != null;

    ///<summary>
    ///Reads a PDF's words out of it, once, with PdfPig (<see cref="SourceTextExtractor"/>), which the caller has loaded
    ///(<see cref="IPdfReaderLoader"/>). Afterwards they are its <see cref="Text"/>, and why there were none, a scan's pictures
    ///of pages or a file PdfPig cannot open, its <see cref="Unreadable"/>.
    ///</summary>
    public void ReadPdf()
    {
        if (Kind != AttachmentKind.Pdf || PdfRead)
            return;

        var (text, error) = SourceTextExtractor.Extract(Name, new MemoryStream(Data), MediaType);

        _pdfText = text ?? "";

        if (error != null)
            _pdfUnreadable = $"{error} A scanned PDF has pictures of its pages and no words: send it as the file, to a model that reads one.";
    }

    ///<summary>The words a file is sent as: a workbook's or a document's read out of it, and any other file's decoded.</summary>
    private string Read()
    {
        if (Kind != AttachmentKind.Spreadsheet && Kind != AttachmentKind.Document)
            return Decode(Data);

        try
        {
            if (Kind == AttachmentKind.Document)
                return OfficeText.Document(Data);

            var workbook = OfficeText.Workbook(Data, MostTextCharacters);

            _goesOn = workbook.Cut;

            return workbook.Text;
        }
        catch (Exception failed)
        {
            var what = "a Word document";

            if (Kind == AttachmentKind.Spreadsheet)
                what = "an Excel workbook";

            _unreadable = $"{Name} could not be read as {what}: {failed.Message}";

            return "";
        }
    }

    ///<summary>A text file's words, or a workbook's or a document's read out of it, once: what it is sent as, and what it is weighed by.</summary>
    public string Text
    {
        get
        {
            if (Kind == AttachmentKind.Pdf)
                return _pdfText ?? "";

            if (_text == null)
                _text = Read();

            return _text;
        }
    }

    ///<summary>
    ///Why a workbook or a document could not be read, said so the person can do something about it, or null when it was,
    ///as it always is for any other kind. Reading it is what finds out, so the file is read here, once, when it is
    ///attached rather than when the question goes.
    ///</summary>
    public string Unreadable
    {
        get
        {
            if (Kind == AttachmentKind.Pdf)
                return _pdfUnreadable;

            if (_text == null)
                _text = Read();

            return _unreadable;
        }
    }

    ///<summary>A PDF's pages, counted once (<see cref="PageCount"/>).</summary>
    public int Pages
    {
        get
        {
            if (_pages < 0)
                _pages = PageCount(Data);

            return _pages;
        }
    }

    ///<summary>About what the file adds to a request it goes with (<see cref="AiEstimate"/>).</summary>
    public long Tokens
    {
        get
        {
            if (Kind == AttachmentKind.Image)
                return ImageTokens;
            else if (Kind == AttachmentKind.Pdf && !AsText)
                return (long)Pages * PdfPageTokens;
            else if (Kind == AttachmentKind.Pdf && !PdfRead)
                return (long)Pages * PdfPageTextTokens;

            return AiEstimate.TokensIn(Math.Min(Text.Length, MostTextCharacters));
        }
    }

    ///<summary>For the file picker, so it opens on what can be attached. Anything else can still be picked, and is turned away with a reason.</summary>
    public static string Accept
    {
        get
        {
            return string.Join(",", ImageExtensions.Concat(new[] { ".pdf" }).Concat(SpreadsheetFiles).Concat(DocumentFiles).Concat(CommonText).Concat(GraphText).Concat(QueryText));
        }
    }

    ///<summary>The name decides before the type does: a browser calls a CSV something different on every system, and has no word at all for most graph formats.</summary>
    public static AttachmentKind KindOf(string name, string mediaType)
    {
        var extension = Path.GetExtension(name ?? "").ToLowerInvariant();
        var type = (mediaType ?? "").Trim().ToLowerInvariant();

        if (ImageExtensions.Contains(extension))
            return AttachmentKind.Image;
        else if (extension == ".pdf")
            return AttachmentKind.Pdf;
        else if (SpreadsheetFiles.Contains(extension))
            return AttachmentKind.Spreadsheet;
        else if (DocumentFiles.Contains(extension))
            return AttachmentKind.Document;
        else if (CommonText.Contains(extension) || GraphText.Contains(extension) || QueryText.Contains(extension))
            return AttachmentKind.Text;
        else if (OfficeFiles.Contains(extension))
            return AttachmentKind.Unsupported;

        //No extension it knows, as with a picture dragged out of another page: the type is all there is.
        if (ImageTypes.Contains(type))
            return AttachmentKind.Image;
        else if (type == "application/pdf")
            return AttachmentKind.Pdf;
        else if (SpreadsheetTypes.Contains(type))
            return AttachmentKind.Spreadsheet;
        else if (type == DocumentType)
            return AttachmentKind.Document;
        else if (type.StartsWith("text/", StringComparison.Ordinal) || type == "application/json" || type == "application/xml")
            return AttachmentKind.Text;

        return AttachmentKind.Unsupported;
    }

    ///<summary>The most a file of a kind can be.</summary>
    public static int MostBytes(AttachmentKind kind)
    {
        if (kind == AttachmentKind.Image)
            return MostImageBytes;

        return MostFileBytes;
    }

    ///<summary>
    ///Why a file cannot go with a question, from its name, its type and its size alone, so a file far too big is turned
    ///away before the page reads a byte of it. Null when nothing so far says no.
    ///</summary>
    public static string Refusal(string name, string mediaType, long size)
    {
        var extension = Path.GetExtension(name ?? "").ToLowerInvariant();
        var kind = KindOf(name, mediaType);

        if (extension == ".xls")
            return $"{name} is an Excel workbook from before 2007, which this page cannot read. Save it as an .xlsx, or a sheet as a CSV, and attach that.";
        else if (extension == ".doc")
            return $"{name} is a Word document from before 2007, which this page cannot read. Save it as a .docx or a PDF, and attach that.";
        else if (OfficeFiles.Contains(extension))
            return $"{name} is a PowerPoint deck, which this page cannot read. Save it as a PDF, and attach that.";

        if (kind == AttachmentKind.Unsupported)
            return $"{name} is not a file the assistant can read. It takes pictures (PNG, JPEG, GIF, WebP), PDFs, Excel workbooks, Word documents and text files: CSV, JSON, GraphML, queries.";

        if (size == 0)
            return $"{name} is empty.";

        var most = MostBytes(kind);

        if (size > most)
            return $"{name} is {Size(size)}, and a file of its kind can be {Size(most)} at most.";

        return null;
    }

    ///<summary>Why a file cannot go with a question, said so the person can do something about it, or null when it can.</summary>
    public static string Problem(string name, string mediaType, byte[] data)
    {
        if (Refusal(name, mediaType, data.Length) is string refused)
            return refused;

        var kind = KindOf(name, mediaType);

        if (kind == AttachmentKind.Image && ImageType(data) == null)
            return $"{name} is not a PNG, JPEG, GIF or WebP picture, whatever its name says.";
        else if (kind == AttachmentKind.Pdf && !IsPdf(data))
            return $"{name} is not a PDF, whatever its name says.";
        else if (kind == AttachmentKind.Text && !LooksLikeText(data))
            return $"{name} is not a text file, whatever its name says.";
        else if (kind == AttachmentKind.Spreadsheet)
            return OfficeProblem(name, data, "workbook", "an Excel workbook", ".xlsx");
        else if (kind == AttachmentKind.Document)
            return OfficeProblem(name, data, "document", "a Word document", ".docx");

        return null;
    }

    ///<summary>
    ///Why bytes called a workbook or a document are not one. A file locked with a password is wrapped the way an Office
    ///file from before 2007 is, so either is said to be one of the two, with how to get one this page reads.
    ///</summary>
    private static string OfficeProblem(string name, byte[] data, string root, string what, string extension)
    {
        if (Opens(data, 0, CompoundFile))
            return $"{name} is locked with a password, or is {what} from before 2007 with a new name. Save it unlocked as an {extension}, and attach that.";

        if (OfficeText.MainElement(data) != root)
            return $"{name} is not {what}, whatever its name says.";

        return null;
    }

    ///<summary>
    ///Whether a provider's models are sent a kind of file at all.
    ///
    ///<strong>What is known to be read on the other end</strong>, not what could be put on the wire. Pictures and words go
    ///to every model, and a workbook or a document goes as its words. A PDF goes to Anthropic, as a document, and to
    ///OpenAI, as a file part, both of which say they read one. Google's OpenAI-compatible endpoint, which is how this app
    ///reaches Gemini, documents pictures and audio and says nothing of a PDF, and a server of one's own may be anything,
    ///so neither is sent one.
    ///</summary>
    public static bool SentTo(string providerType, AttachmentKind kind)
    {
        if (kind == AttachmentKind.Image || kind == AttachmentKind.Text || kind == AttachmentKind.Spreadsheet || kind == AttachmentKind.Document)
            return true;
        else if (kind == AttachmentKind.Pdf)
            return providerType == LlmConnection.Anthropic || providerType == LlmConnection.OpenAI;

        return false;
    }

    ///<summary>Why a file waiting in the box cannot go to the model picked, for the line that says why Send waits, or null when it can.</summary>
    public static string NotSentTo(string providerType, AiAttachment file, bool pdfAsText = false)
    {
        if (SentTo(providerType, file.Kind) || (file.Kind == AttachmentKind.Pdf && pdfAsText))
            return null;

        if (providerType == LlmConnection.Gemini)
            return $"{file.Name} cannot go to a Gemini model: Google's OpenAI-compatible endpoint, which this app reaches it through, takes pictures and text but not PDFs. Tick Send PDFs as their text on this model under AI settings, take it off, or pick an Anthropic or OpenAI model.";

        return $"{file.Name} cannot go to a server of your own, which may not read PDFs. Tick Send PDFs as their text on this model under AI settings, take it off, or pick an Anthropic or OpenAI model.";
    }

    ///<summary>
    ///What a question's files become in a request, ahead of the question's own words.
    ///
    ///<strong>Ahead of the question</strong>, so the question reads them before it is asked, and so that everything up to
    ///the last file is what was sent the time before, byte for byte, which is what a provider's cache holds on to.
    ///<strong>Each one named</strong>: a picture or a PDF just after a line saying which it is, and a text file inside tags
    ///carrying its name, so "the second screenshot" or "row 40 of the sheet" can be matched to the right one.
    ///
    ///A file the model cannot be sent, or one unticked in the files panel, is still said in a line: left out without a
    ///word, it would read as a question about a file the person never sent.
    ///</summary>
    public static List<AttachmentPart> Parts(IReadOnlyList<AiAttachment> files, string providerType)
    {
        var parts = new List<AttachmentPart>();

        foreach (var file in files)
        {
            if (file.Excluded)
            {
                parts.Add(new AttachmentPart(AttachmentKind.Text, $"[The person attached {file.Name} here, and has since left it out of the conversation.]"));

                continue;
            }

            if (!SentTo(providerType, file.SentKind))
            {
                parts.Add(new AttachmentPart(AttachmentKind.Text, $"[The person attached {file.Name}, which this model cannot be sent. If the question needs it, say so.]"));

                continue;
            }

            if (file.Kind == AttachmentKind.Image)
            {
                //The type the bytes say rather than the one the name does: Anthropic refuses a JPEG sent as a PNG.
                var type = ImageType(file.Data) ?? "image/png";

                parts.Add(new AttachmentPart(AttachmentKind.Text, $"Attached file: {file.Name}"));
                parts.Add(new AttachmentPart(AttachmentKind.Image, "", type, Convert.ToBase64String(file.Data), file.Name));
            }
            else if (file.SentKind == AttachmentKind.Pdf)
            {
                parts.Add(new AttachmentPart(AttachmentKind.Text, $"Attached file: {file.Name}"));
                parts.Add(new AttachmentPart(AttachmentKind.Pdf, "", "application/pdf", Convert.ToBase64String(file.Data), file.Name));
            }
            else if (file.Unreadable is string why)
                //Turned away when it was attached, so only a file attached some other way gets here.
                parts.Add(new AttachmentPart(AttachmentKind.Text, $"[The person attached {file.Name}. {why}]"));
            else
                parts.Add(new AttachmentPart(AttachmentKind.Text, Wrapped(file.Name, file.Text, file._goesOn)));
        }

        return parts;
    }

    ///<summary>
    ///A text file between tags carrying its name, so the model can tell where it stops and the question starts, cut at
    ///<see cref="MostTextCharacters"/>, saying how much more there was. A workbook is read no further than that, so how
    ///much more is not known: <paramref name="workbookGoesOn"/> says there was more, without a number.
    ///</summary>
    public static string Wrapped(string name, string text, bool workbookGoesOn = false)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"<attachment name=\"{name.Replace('"', '\'')}\">");

        if (text.Length <= MostTextCharacters)
        {
            builder.AppendLine(text.TrimEnd());

            if (workbookGoesOn)
                builder.AppendLine("[Cut short here. The workbook goes on past this point, and the rest was left out.]");
        }
        else
        {
            var rest = text.Length - MostTextCharacters;

            builder.AppendLine(text.Substring(0, MostTextCharacters).TrimEnd());
            builder.AppendLine($"[Cut short here. The file goes on for another {rest.ToString("N0", CultureInfo.InvariantCulture)} characters, which were left out.]");
        }

        builder.Append("</attachment>");

        return builder.ToString();
    }

    ///<summary>
    ///A picture's type read off its first bytes, or null for anything else. What the model is told has to be what the
    ///bytes are, since Anthropic refuses a JPEG sent as a PNG, and a file's name is only a claim.
    ///</summary>
    public static string ImageType(byte[] data)
    {
        if (Opens(data, 0, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        else if (Opens(data, 0, new byte[] { 0xFF, 0xD8, 0xFF }))
            return "image/jpeg";
        else if (Opens(data, 0, "GIF87a"u8) || Opens(data, 0, "GIF89a"u8))
            return "image/gif";
        else if (Opens(data, 0, "RIFF"u8) && Opens(data, 8, "WEBP"u8))
            return "image/webp";

        return null;
    }

    ///<summary>"%PDF-" somewhere in the first kilobyte, which is as far in as the format lets it start.</summary>
    public static bool IsPdf(byte[] data)
    {
        var length = Math.Min(data.Length, 1024);

        return data.AsSpan(0, length).IndexOf("%PDF-"u8) >= 0;
    }

    ///<summary>Text has no NUL in it, unless it is UTF-16, which says so in its first two bytes. Looked for in the first few kilobytes, where a binary file gives itself away.</summary>
    public static bool LooksLikeText(byte[] data)
    {
        if (Opens(data, 0, new byte[] { 0xFF, 0xFE }) || Opens(data, 0, new byte[] { 0xFE, 0xFF }))
            return true;

        var length = Math.Min(data.Length, 8192);

        return data.AsSpan(0, length).IndexOf((byte)0) < 0;
    }

    ///<summary>
    ///A text file's words: UTF-8 unless a byte order mark says otherwise, and Latin-1 when it is not UTF-8.
    ///
    ///A file that is not UTF-8 is most likely in a Windows code page from before it, as an old CSV export is; Latin-1 reads
    ///every byte as some character, so the numbers that make up most of such a file come through whole.
    ///</summary>
    public static string Decode(byte[] data)
    {
        if (Opens(data, 0, new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        else if (Opens(data, 0, new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);

        var start = 0;

        if (Opens(data, 0, new byte[] { 0xEF, 0xBB, 0xBF }))
            start = 3;

        try
        {
            return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    ///<summary>
    ///About how many pages a PDF has, for the estimate, not for anything that has to be right.
    ///
    ///<strong>Its page objects, counted where they can be seen</strong>: each page is an object that says /Type /Page,
    ///which a file written plainly shows. A newer one packs its objects into compressed streams, where they cannot be seen
    ///without inflating them, so then the page tree's /Count, the largest one there is, which is often still in the open;
    ///and with neither, one page for every 50 KB, which is what text and line drawings tend to come to.
    ///</summary>
    public static int PageCount(byte[] data)
    {
        var span = data.AsSpan();
        var pages = 0;
        var at = 0;

        while (true)
        {
            var found = span.Slice(at).IndexOf("/Type"u8);

            if (found < 0)
                break;

            var next = SkipSpaces(span, at + found + 5);

            if (StartsAt(span, next, "/Page"u8) && !IsLetter(span, next + 5))
                pages++;

            at += found + 5;
        }

        if (pages > 0)
            return pages;

        var counted = MostCount(span);

        if (counted > 0)
            return counted;

        return Math.Max(1, data.Length / 50_000);
    }

    ///<summary>"512 bytes", "40 KB", "1.4 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} bytes";
        else if (bytes < 1024 * 1024)
            return $"{Math.Ceiling(bytes / 1024.0).ToString(CultureInfo.InvariantCulture)} KB";

        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }

    ///<summary>The largest number after a /Count, or 0 when there is none to see.</summary>
    private static int MostCount(ReadOnlySpan<byte> span)
    {
        var most = 0;
        var at = 0;

        while (true)
        {
            var found = span.Slice(at).IndexOf("/Count"u8);

            if (found < 0)
                break;

            var digit = SkipSpaces(span, at + found + 6);
            var value = 0;

            while (digit < span.Length && span[digit] >= (byte)'0' && span[digit] <= (byte)'9' && value < 100_000)
            {
                value = value * 10 + (span[digit] - (byte)'0');
                digit++;
            }

            most = Math.Max(most, value);
            at += found + 6;
        }

        return most;
    }

    private static int SkipSpaces(ReadOnlySpan<byte> span, int at)
    {
        while (at < span.Length && (span[at] == (byte)' ' || span[at] == (byte)'\r' || span[at] == (byte)'\n' || span[at] == (byte)'\t'))
            at++;

        return at;
    }

    private static bool StartsAt(ReadOnlySpan<byte> span, int at, ReadOnlySpan<byte> word)
    {
        return at >= 0 && at + word.Length <= span.Length && span.Slice(at, word.Length).SequenceEqual(word);
    }

    private static bool IsLetter(ReadOnlySpan<byte> span, int at)
    {
        if (at >= span.Length)
            return false;

        var b = span[at];

        return (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');
    }

    private static bool Opens(byte[] data, int offset, ReadOnlySpan<byte> signature)
    {
        return data.Length >= offset + signature.Length && data.AsSpan(offset, signature.Length).SequenceEqual(signature);
    }
}
