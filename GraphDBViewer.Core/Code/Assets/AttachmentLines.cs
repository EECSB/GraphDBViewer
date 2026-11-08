namespace GraphDBViewerWeb.Code;

///<summary>One file a text field lists: the name it had when it was attached, and its URL.</summary>
public sealed record AttachedFile(string Name, string Url);

///<summary>
///Files listed in a text field, one Markdown link per line: how the CRM has kept an interaction's attachments
///since its phase 2, and every <c>attachments</c> field since 2026-09-30, when Organization's documents became
///a list. In the core so every plugin reads and writes the same lines, and the DAM's usage check, which looks
///inside every <c>attachments</c> field, finds each link wherever it is kept.
///
///<strong>Only a web link is a file here</strong> (2026-10-03): every list draws its files as links, so a link of
///any other scheme, <c>javascript:</c> above all, would run when clicked. The rule is a whitelist,
///<see cref="IsWebLink"/>, and both ways go through it: a line that is not one is not read, and is not written.
///</summary>
public static class AttachmentLines
{
    ///<summary>
    ///The files a text lists. A line that is a bare URL is read as one named by its last segment, and a line
    ///that is neither a link nor a URL is skipped, as is a link that is not a web link.
    ///</summary>
    public static List<AttachedFile> Parse(string text)
    {
        var found = new List<AttachedFile>();

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(')'))
            {
                var middle = line.LastIndexOf("](", StringComparison.Ordinal);

                if (middle > 0)
                {
                    var url = line.Substring(middle + 2, line.Length - middle - 3).Trim();

                    if (IsWebLink(url))
                        found.Add(new AttachedFile(line.Substring(1, middle - 1).Trim(), url));
                }

                continue;
            }

            if (IsWebLink(line))
                found.Add(new AttachedFile(LastSegment(line), line));
        }

        return found;
    }

    ///<summary>
    ///Whether a link may be kept in a list and drawn as one: an http or https address, or one on this site, which
    ///begins with a slash. Nothing else, so no scheme that runs anything gets in by a spelling a browser forgives:
    ///the text must begin with one of the three, and holds no space or control character, which a browser drops
    ///before it reads the scheme.
    ///</summary>
    public static bool IsWebLink(string url)
    {
        var link = url ?? "";

        if (link.Length == 0 || link.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        if (link.StartsWith('/'))
            return true;

        return link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>
    ///The lines a list of files is kept in, each a Markdown link. A name loses the brackets and line breaks that
    ///would end its link early, and one with nothing left is named by its URL; a file whose URL is not a web link
    ///is left out.
    ///</summary>
    public static string Format(IEnumerable<AttachedFile> files)
    {
        var lines = new List<string>();

        foreach (var file in files ?? [])
        {
            var url = (file?.Url ?? "").Trim();

            if (!IsWebLink(url))
                continue;

            var name = new string((file.Name ?? "").Where(c => c != '[' && c != ']' && c != '\n' && c != '\r').ToArray()).Trim();

            if (name.Length == 0)
                name = LastSegment(url);

            lines.Add($"[{name}]({url})");
        }

        return string.Join("\n", lines);
    }

    ///<summary>A URL's last segment: what a file with no name of its own is called.</summary>
    public static string LastSegment(string url)
    {
        var trimmed = (url ?? "").TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');

        if (slash >= 0 && slash < trimmed.Length - 1)
            return trimmed.Substring(slash + 1);

        return trimmed;
    }
}
