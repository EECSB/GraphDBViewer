using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///The installed-plugin list, the disabled bundled plugins and each plugin's identity marker, changed by
///the host one change at a time rather than written whole by the Plugins page.
///
///<strong>Why the page stopped writing them (2026-09-23).</strong> The page read the list when it opened,
///changed its own copy and wrote all of it back, so of two administrators installing at the same moment
///the later write undid the earlier, and both were told it had worked: sixteen installs at once kept
///one. No gate on the server could help while the read happened in a browser. So the page still
///<em>proves</em> a plugin, which has to happen in the runtime that will run it, and the host applies
///what the page decided to the list as it stands, under a gate held from that read to its write.
///
///The generic instance-state surface still serves these keys to anybody signed in, because the boot
///reads them before anything else exists, but it no longer writes them (<see cref="OwnsKey"/>). The
///web-only edition has no server and never calls any of this, the same as
///<see cref="InstanceStateContract"/>.
///</summary>
public static class PluginInstallsContract
{
    ///<summary>POST a <see cref="PluginInstallChange"/> here to install. Every answer is the list as it now stands.</summary>
    public const string InstalledPath = "api/plugins/installed";

    ///<summary>Where the bundled plugins are removed and restored; <see cref="BundledPathFor"/> names one.</summary>
    public const string BundledPath = "api/plugins/bundled";

    ///<summary>One installed plugin: PUT a <see cref="PluginInstallChange"/> to update it. It is uninstalled at <see cref="UninstallPathFor"/>.</summary>
    public static string InstalledPathFor(string id)
    {
        return $"{InstalledPath}/{Uri.EscapeDataString(id)}";
    }

    ///<summary>
    ///DELETE here to uninstall one plugin, the id in the query string, where it is decoded once and
    ///exactly. In a path segment an id's own "%2F" and an escaped "/" arrive as the same text, so a record
    ///whose id holds a "%2F", listed before the host refused a "%" in an id, could not be told apart
    ///there from a plugin with a "/", and no request could take it out of the list (a review of the
    ///2026-09-24 audit's fix). Uninstalling is the one change every listed record has to stay open to.
    ///</summary>
    public static string UninstallPathFor(string id)
    {
        return $"{InstalledPath}?id={Uri.EscapeDataString(id)}";
    }

    ///<summary>POST a <see cref="PluginEnabledChange"/>: the state it should be in rather than a flip, so a repeat changes nothing.</summary>
    public static string EnabledPathFor(string id)
    {
        return InstalledPathFor(id) + "/enabled";
    }

    ///<summary>POST a <see cref="BundledPluginChange"/>: a bundled plugin removed, or restored. The answer is the ids removed as they now stand.</summary>
    public static string BundledPathFor(string id)
    {
        return $"{BundledPath}/{Uri.EscapeDataString(id)}";
    }

    ///<summary>
    ///Whether a key is one of the three these endpoints own, which the generic surface serves but will
    ///not write: a whole value written there is exactly the lost update they exist to prevent.
    ///</summary>
    public static bool OwnsKey(string key)
    {
        if (key == InstanceStateContract.InstalledPluginsKey || key == InstanceStateContract.DisabledBundledKey)
            return true;

        return InstanceStateContract.IsIdentityKey(key);
    }

    ///<summary>What the generic surface answers when somebody writes one of them there anyway.</summary>
    public const string OwnedRefusal = "This key is changed through api/plugins/installed and api/plugins/bundled, one change at a time. A whole value written here would undo whatever was changed beside it.";
}

///<summary>An install, or an update: the record the page proved, and whether to record where it came from.</summary>
public sealed class PluginInstallChange
{
    [JsonPropertyName("record")]
    public InstalledPluginRecord Record { get; set; }

    ///<summary>
    ///Whether the identity marker (ADR 0012) is written with the record, inside the same gate. An install
    ///records it, and so does a store update, which may carry a repository the install did not; an update
    ///by URL leaves it as it was, because nothing about where the plugin comes from changed. Since
    ///2026-09-26 the host writes it all the same when the marker it has does not name where the record
    ///comes from, or there is none, so a change that says it records nothing cannot leave a marker
    ///naming somewhere other than the plugin in the list; false only keeps a marker that already does.
    ///</summary>
    [JsonPropertyName("recordIdentity")]
    public bool RecordIdentity { get; set; }
}

///<summary>Enabled or disabled: kept in the list either way, loaded or not from the next reload.</summary>
public sealed class PluginEnabledChange
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

///<summary>A bundled plugin removed, which the boot reads as "skip its module", or restored.</summary>
public sealed class BundledPluginChange
{
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }
}
