namespace GraphDBViewerWeb.Code;

///<summary>
///The wire shape of the instance-scoped store — state that belongs to the deployment, not to a
///person: the installed-plugin list, a plugin's admin-set settings (ADR 0006). Named once so the two
///halves cannot drift, exactly as <see cref="AppStorageContract"/> does for the per-user store.
///
///In the core rather than the host because the client has to know the path <em>before</em> the app
///exists: the installed-plugin list is fetched by a bare HttpClient ahead of <c>Build()</c>, plugins
///having to register their services before the container seals. The web-only edition has no server
///and never calls any of this; the constants sit dormant there.
///</summary>
public static class InstanceStateContract
{
    ///<summary>
    ///Base path for a single key's value. The key is appended as a catch-all segment, so it may carry
    ///its colon without any escaping games.
    ///</summary>
    public const string BasePath = "api/instance";

    ///<summary>The key the installed-plugin list lives under.</summary>
    public const string InstalledPluginsKey = "plugins:installed";

    ///<summary>
    ///The bundled plugins an administrator removed, as a JSON array of ids. Bundled plugins ship in
    ///the image, so "uninstalling" one means the boot skips its module — this list is what the boot
    ///consults, and restoring is removing the id again.
    ///</summary>
    public const string DisabledBundledKey = "plugins:bundled-disabled";

    ///<summary>
    ///One plugin's settings: a single opaque blob under one key (ADR 0006's open question 2, answered
    ///the simple way — a settings page is one read and one write, and per-setting permissions have no
    ///use case to pay for). Written by whoever holds the <c>&lt;id&gt;.configure</c> permission the
    ///plugin declares, or <c>plugins.install</c> — which always suffices: whoever may remove the
    ///plugin outright may configure it.
    ///</summary>
    public static string SettingsKeyFor(string pluginId)
    {
        return $"plugins:{pluginId}:settings";
    }

    ///<summary>
    ///The last rewrite of a plugin's typed properties (ADR 0014, question 1) — its report, with the
    ///worklist of values that would not convert. Reserved on the generic surface: the worklist quotes
    ///raw values, so it is read through the retype endpoint, behind the install permission, only.
    ///</summary>
    public static string RetypeKeyFor(string pluginId)
    {
        return $"plugins:{pluginId}:retype";
    }

    public static bool IsRetypeKey(string key)
    {
        return key != null && key.StartsWith("plugins:", StringComparison.Ordinal) && key.EndsWith(":retype", StringComparison.Ordinal);
    }

    ///<summary>
    ///The identity marker trust-on-first-use records at install (ADR 0012): which origin this id was
    ///first installed from. Deliberately not deleted by uninstall — surviving is its whole job, so a
    ///later install over leftovers can be recognized as the same plugin or challenged.
    ///</summary>
    public static string IdentityKeyFor(string pluginId)
    {
        return $"plugins:{pluginId}:identity";
    }

    ///<summary>Whether a key is some plugin's settings blob, and whose.</summary>
    public static bool TryParseSettingsKey(string key, out string pluginId)
    {
        pluginId = null;

        if (key == null || !key.StartsWith("plugins:", StringComparison.Ordinal) || !key.EndsWith(":settings", StringComparison.Ordinal))
            return false;

        var id = key.Substring("plugins:".Length, key.Length - "plugins:".Length - ":settings".Length);

        if (id.Length == 0)
            return false;

        pluginId = id;

        return true;
    }

    ///<summary>The full relative path for one key.</summary>
    public static string PathFor(string key)
    {
        return $"{BasePath}/{Uri.EscapeDataString(key)}";
    }

    ///<summary>Values are opaque strings here too — the server stores what it is handed.</summary>
    public const string ContentType = "text/plain";

    ///<summary>
    ///What the endpoint accepts for one value. Far below the workspace cap on purpose: what lives
    ///here is a plugin list and per-plugin settings, not query results.
    ///</summary>
    public const long MaxValueBytes = 1L * 1024 * 1024;
}
