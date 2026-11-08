using System;
using System.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///Which addresses a link drawn from typed Markdown keeps (2026-10-03). Markdig's DisableHtml keeps any HTML typed as
///text, but a Markdown link is still a link, and until then its address went into the page whatever its scheme: a
///<c>[x](javascript:...)</c> in a part's description, a customer's notes or an operation's instruction drew a link
///that ran script for whoever clicked it. So every renderer hands each address to <see cref="Rewrite"/>, a whitelist
///in the spirit of <see cref="AttachmentLines.IsWebLink"/>: a web page's scheme, a mail's, or none at all. In the core,
///which has no Markdig, so that each plugin's renderer and the CRM's mail keep to one rule.
///</summary>
public static class MarkdownLinks
{
    ///<summary>Where a link whose address is refused points instead: nowhere, on the page it is on.</summary>
    public const string Nowhere = "#";

    private static readonly string[] Schemes = ["http", "https", "mailto"];

    ///<summary>The address as a link may be drawn with: itself when <see cref="IsSafe"/> says so, else <see cref="Nowhere"/>.</summary>
    public static string Rewrite(string url)
    {
        if (IsSafe(url))
            return url;

        return Nowhere;
    }

    ///<summary>
    ///Whether an address may be drawn as a link: one whose scheme is http, https or mailto, or one with no scheme,
    ///which a browser reads against the page it is on. Never one holding a space or a control character, which a
    ///browser drops before it reads the scheme, so a <c>java&amp;#9;script:</c> cannot pass for one without a scheme.
    ///</summary>
    public static bool IsSafe(string url)
    {
        var link = url ?? "";

        if (link.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        var colon = link.IndexOf(':');

        if (colon < 0)
            return true;

        //A colon after the path, the query or the fragment has begun is no scheme's: "notes/a:b" is a path on the site.
        var begun = link.IndexOfAny(['/', '?', '#']);

        if (begun >= 0 && begun < colon)
            return true;

        return Schemes.Contains(link.Substring(0, colon), StringComparer.OrdinalIgnoreCase);
    }
}
