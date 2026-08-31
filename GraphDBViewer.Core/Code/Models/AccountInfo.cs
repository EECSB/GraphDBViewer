namespace GraphDBViewerWeb.Code;

///<summary>
///Who the browser is signed in as, and whether this deployment has accounts at all.
///
///The viewer runs in WebAssembly and the sign-in is a cookie the *server* reads, so the client cannot
///work any of this out for itself — it has to ask. One request on startup, and the answer decides whether
///the top bar shows an account menu at all.
///</summary>
public class AccountInfo
{
    ///<summary>Path the client asks. Named once so the host builds its route from the same constant.</summary>
    public const string Path = "api/auth/me";

    ///<summary>
    ///False when the deployment runs open. There is no account to manage then — everyone is one built-in
    ///user — so the menu is not shown rather than shown empty.
    ///</summary>
    public bool HasAccounts { get; set; }

    ///<summary>Whether someone is signed in. Always true when the app is reachable at all.</summary>
    public bool SignedIn { get; set; }

    ///<summary>The address they signed in with, shown in the menu so it is obvious *which* account this is.</summary>
    public string Email { get; set; }

    ///<summary>
    ///An antiforgery token for the sign-out form. Handed over here because a form rendered by WebAssembly
    ///has no server-side render to get one from, and signing out is a POST — a link would let any page on
    ///the internet sign you out of this one.
    ///</summary>
    public string AntiforgeryToken { get; set; }

    ///<summary>What to show when there is no name to show.</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Email))
                return "Account";

            return Email;
        }
    }

    ///<summary>The first letter of the address, for the avatar circle.</summary>
    public string Initial
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Email))
                return "?";

            return Email.Substring(0, 1).ToUpperInvariant();
        }
    }
}
