using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>What a GUN write does. Three shapes cover everything the viewer can ask for.</summary>
public enum GunWriteKind
{
    ///<summary><c>put</c> of a set of keys onto a node. GUN merges, so this both creates and updates.</summary>
    Put,

    ///<summary><c>put</c> of another node onto a key — which is what an edge is in GUN.</summary>
    Link,

    ///<summary><c>put(null)</c> — GUN's delete, of a whole node or of the one key holding a link.</summary>
    Clear
}

///<summary>
///One GUN write, as the statement the user reviews and as the operation the driver applies —
///<see cref="GunQuery"/>'s counterpart for the other direction.
///
///GUN has no query language, so a staged edit cannot be a query. What it is instead is the <b>real GUN
///JavaScript</b> that will run — <c>gun.get('alice').put({"name":"Alice"})</c> — one statement per line, in
///the same Generated tab every other engine stages its queries in. That keeps the flow the whole app is
///built on (compose, review, edit, Commit, discard) while never showing a language GUN does not have.
///
///The statement is the record; the driver <b>parses it back</b> into the operation it applies, rather than
///evaluating the text. Nothing the user can type in that box becomes JavaScript the page runs.
///</summary>
public sealed class GunWrite
{
    public GunWriteKind Kind { get; init; }

    ///<summary>The node written to.</summary>
    public string Soul { get; init; } = "";

    ///<summary>For a link or an unlink, the key holding it — which is the edge's label.</summary>
    public string Edge { get; init; }

    ///<summary>For a link, the node linked to.</summary>
    public string Target { get; init; }

    ///<summary>
    ///For a put, the keys written. A null value is GUN's way of deleting a key, so it survives the round
    ///trip rather than being dropped as "no value".
    ///</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    ///<summary>Writes a set of keys onto a node, creating it if no peer has it yet.</summary>
    public static GunWrite Put(string soul, IReadOnlyDictionary<string, string> values)
    {
        return new GunWrite { Kind = GunWriteKind.Put, Soul = soul ?? "", Values = values ?? new Dictionary<string, string>() };
    }

    ///<summary>Writes one key — the same as a <see cref="Put"/> of one, and what a property edit is.</summary>
    public static GunWrite PutValue(string soul, string key, string value)
    {
        return Put(soul, new Dictionary<string, string> { [key] = value });
    }

    ///<summary>Links one node to another under a key. That key is the edge's label; GUN has no others.</summary>
    public static GunWrite Link(string soul, string edge, string target)
    {
        return new GunWrite { Kind = GunWriteKind.Link, Soul = soul ?? "", Edge = edge ?? "", Target = target ?? "" };
    }

    ///<summary>
    ///Nulls a whole node, or — with an edge — just the key holding that link. <paramref name="target"/> is
    ///what that link currently points at: GUN's own operation does not need it (a key holds one link, so
    ///nulling the key is the whole of it), but the viewer does, to know which edge vanished from the canvas.
    ///It rides along as a comment, which is what it is.
    ///</summary>
    public static GunWrite Clear(string soul, string edge = null, string target = null)
    {
        return new GunWrite { Kind = GunWriteKind.Clear, Soul = soul ?? "", Edge = edge, Target = target };
    }

    ///<summary>
    ///The GUN JavaScript this write is, on one line. Single-line is not a style choice: the Generated
    ///buffer is committed by splitting on newlines, so a statement that wrapped would run as two halves.
    ///</summary>
    public string ToStatement()
    {
        var sb = new StringBuilder("gun.get('").Append(GunQuery.Escape(Soul)).Append("')");

        if (!string.IsNullOrEmpty(Edge))
            sb.Append(".get('").Append(GunQuery.Escape(Edge)).Append("')");

        if (Kind == GunWriteKind.Clear)
        {
            sb.Append(".put(null)");

            if (!string.IsNullOrEmpty(Target))
                sb.Append(TargetComment).Append(GunQuery.Escape(Target));

            return sb.ToString();
        }

        if (Kind == GunWriteKind.Link)
            return sb.Append(".put(gun.get('").Append(GunQuery.Escape(Target)).Append("'))").ToString();

        return sb.Append(".put(").Append(ValuesJson()).Append(")").ToString();
    }

    ///<summary>The put's keys as the JSON object the statement carries — and valid JavaScript, being JSON.</summary>
    public string ValuesJson()
    {
        var values = new Dictionary<string, string>();

        foreach (var kv in Values)
            values[kv.Key] = kv.Value;

        return JsonSerializer.Serialize(values);
    }

    ///<summary>Marks the comment naming what a cleared link pointed at.</summary>
    public const string TargetComment = "//-> ";

    //gun.get('soul')[.get('edge')].put( … )[//-> target]
    private static readonly Regex StatementRegex = new(
        @"^gun\.get\('(?<soul>(?:[^'\\]|\\.)*)'\)(?:\.get\('(?<edge>(?:[^'\\]|\\.)*)'\))?\.put\((?<body>.*?)\)\s*(?://->\s*(?<was>.*))?$",
        RegexOptions.Compiled);

    private static readonly Regex LinkBodyRegex = new(
        @"^gun\.get\('(?<target>(?:[^'\\]|\\.)*)'\)$",
        RegexOptions.Compiled);

    ///<summary>True when the line is a write rather than a read. A read is a key path; a write is code.</summary>
    public static bool IsWrite(string statement)
    {
        return StatementRegex.IsMatch((statement ?? "").Trim());
    }

    ///<summary>
    ///Reads a statement back into the write it describes, or null when the line is not one this wrote.
    ///Best-effort by design: an unrecognized line stays staged and fails loudly at commit rather than being
    ///guessed at.
    ///</summary>
    public static GunWrite Parse(string statement)
    {
        var match = StatementRegex.Match((statement ?? "").Trim());

        if (!match.Success)
            return null;

        var soul = Unescape(match.Groups["soul"].Value);

        string edge = null;
        if (match.Groups["edge"].Success)
            edge = Unescape(match.Groups["edge"].Value);

        var body = match.Groups["body"].Value.Trim();

        if (body == "null")
        {
            string was = null;
            if (match.Groups["was"].Success)
                was = Unescape(match.Groups["was"].Value.Trim());

            return Clear(soul, edge, was);
        }

        var link = LinkBodyRegex.Match(body);

        if (link.Success)
            return Link(soul, edge, Unescape(link.Groups["target"].Value));

        var values = ReadValues(body);

        if (values == null)
            return null;

        //A put under a key is still a put — of that one key's object — and the viewer never writes one.
        if (edge != null)
            return null;

        return Put(soul, values);
    }

    //The object literal a put carries. It is written as JSON, so it reads back as JSON.
    private static Dictionary<string, string> ReadValues(string body)
    {
        if (!body.StartsWith("{", StringComparison.Ordinal))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var values = new Dictionary<string, string>();

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                    values[property.Name] = null;
                else if (property.Value.ValueKind == JsonValueKind.String)
                    values[property.Name] = property.Value.GetString();
                else
                    values[property.Name] = property.Value.GetRawText();
            }

            return values;
        }
        catch
        {
            return null;
        }
    }

    private static string Unescape(string value)
    {
        return (value ?? "").Replace("\\'", "'").Replace("\\\\", "\\");
    }
}
