using System;

namespace GraphDBViewerWeb.Code;

///<summary>
///The one rule about plugin ids that is policy rather than syntax: <c>treeality.</c> is reserved for
///the plugins that ship with Treeality (the plugin store's question 1, the user, 2026-09-06). The
///registry's submission Action refuses the prefix from anyone else; this is the app's half, for the
///install-by-URL path that exists today. A manifest's id is also the namespace of a third-party
///plugin's labels and permissions, so a plugin under the prefix would look official in every list.
///The sentences the Plugins page shows for an id it cannot install are here too, so the page and the
///host say each of them one way.
///</summary>
public static class PluginIds
{
    public const string ReservedPrefix = "treeality.";

    ///<summary>A prefix test that ignores case, because "Treeality.parts" would look no less official.</summary>
    public static bool IsReserved(string id)
    {
        return id != null && id.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>What the Plugins page shows for a reserved id, with the way out in the sentence.</summary>
    public static string ReservedRefusal(string id)
    {
        var rest = id.Substring(ReservedPrefix.Length);

        if (rest.Length == 0)
            rest = "gauges";

        return $@"""{id}"" uses the reserved prefix ""{ReservedPrefix}"", which belongs to the plugins that ship with Treeality. Publish it under your own name — ""acme.{rest}"" — and install that.";
    }

    ///<summary>
    ///What the Plugins page shows for an id that is in the list already. The page's installer says it
    ///against the list the page read, before it fetches or proves any code, and the host says it against
    ///the list as it stands, under the list's gate, where an install another administrator made a moment
    ///ago is seen. Written out at all three until 2026-09-26 (the 2026-09-24 audit).
    ///</summary>
    public static string AlreadyHereRefusal(string id)
    {
        return $@"""{id}"" is already here. Uninstall it first — installing over a plugin is not yet a thing.";
    }
}
