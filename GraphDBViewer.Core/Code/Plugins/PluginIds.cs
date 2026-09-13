using System;

namespace GraphDBViewerWeb.Code;

///<summary>
///The one rule about plugin ids that is policy rather than syntax: <c>treeality.</c> is reserved for
///the plugins that ship with Treeality (the plugin store's question 1, the user, 2026-09-06). The
///registry's submission Action refuses the prefix from anyone else; this is the app's half, for the
///install-by-URL path that exists today. A manifest's id is also the namespace of a third-party
///plugin's labels and permissions, so a plugin under the prefix would look official in every list.
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
}
