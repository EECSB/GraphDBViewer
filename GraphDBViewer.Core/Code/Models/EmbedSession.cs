using Microsoft.AspNetCore.Components;

namespace GraphDBViewerWeb.Code;

///<summary>
///Whether this page is the embedded viewer, and the token the page that embeds it passed (2026-10-21, DESIGN.md's
///embeddable viewer, the user's call: any site may embed it, in an iframe, and it reads with a read-only token the
///embedding page passes). The viewer is embedded at <c>/embed</c>, the query string saying what it shows
///(<see cref="EmbedSettings"/>); the token comes in the fragment, <c>/embed?instance&amp;q=…#token=trl_…</c>, which a
///browser sends to no server and puts in no Referer header, and the page takes the fragment off its address once it
///has been read (<c>Embed.razor</c>).
///
///<strong>Why a token at all:</strong> a browser keeps a site's cookies out of another site's frame, so the viewer's
///own sign-in never reaches an embedded page, and a sign-in form inside the frame could not set one either.
///</summary>
public sealed class EmbedSession
{
    ///<summary>The embedded viewer's route, below the app's base, without a slash.</summary>
    public const string Path = "embed";

    ///<summary>What the fragment calls the token.</summary>
    public const string TokenKey = "token";

    public EmbedSession(NavigationManager nav)
    {
        IsEmbedded = IsEmbedRoute(nav.ToBaseRelativePath(nav.Uri));

        if (IsEmbedded)
            Token = TokenIn(new Uri(nav.Uri).Fragment);
    }

    ///<summary>Whether this page is the embedded viewer: what it keeps it keeps in memory, and what it asks the host carries <see cref="Token"/>.</summary>
    public bool IsEmbedded { get; }

    ///<summary>The token the embedding page passed, or null when it passed none.</summary>
    public string Token { get; }

    ///<summary>Whether an address below the app's base, its query and fragment included, is the embedded viewer's.</summary>
    public static bool IsEmbedRoute(string relative)
    {
        var route = (relative ?? "").Split('?', '#')[0].Trim('/');

        return string.Equals(route, Path, StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The token a fragment names, <c>#token=…</c> among any others joined by <c>&amp;</c>, or null when it names none.</summary>
    public static string TokenIn(string fragment)
    {
        foreach (var part in (fragment ?? "").TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');

            if (at <= 0 || !string.Equals(part.Substring(0, at), TokenKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Uri.UnescapeDataString(part.Substring(at + 1)).Trim();

            if (value.Length > 0)
                return value;
        }

        return null;
    }
}
