using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a plugin declares about itself, parsed and checked before any of its code is considered
///(plugin-system design). Reverse-DNS ids make accidental collision essentially impossible;
///deliberate collision is impersonation, which is ADR 0012's problem, not this parser's.
///
///The one rule enforced here rather than anywhere later: <strong>a manifest may declare permissions
///under its own id only, and one that does not is refused, never trimmed</strong> (ADR 0008). A
///plugin that could name <c>plugins.install</c> could have it granted along with the rest and then
///install further plugins — and the manifest is the last point at which that is cheap to stop.
///</summary>
public sealed class PluginManifest
{
    ///<summary>A .NET assembly plugin — the kind that matters; pages and domain modules are this.</summary>
    public const string DotNetKind = "dotnet";

    ///<summary>A JavaScript module plugin. Reserved: nothing loads one yet.</summary>
    public const string JsKind = "js";

    ///<summary>
    ///The host API surface this build offers. A manifest names the version it compiled against;
    ///whether the two are compatible is the loader's question (<see cref="RunsHere"/>), not the parser's. 1.1 since
    ///2026-10-20, which added <see cref="Notifications"/> and took nothing away.
    ///</summary>
    public const string CurrentHostApiVersion = "1.1";

    ///<summary>The first host API version whose manifest may declare <see cref="Notifications"/> (the notifications spec, question 9).</summary>
    public const string NotificationsHostApiVersion = "1.1";

    ///<summary>How many kinds of notification one manifest may declare: a settings page lists every one of them for each person who may hear it.</summary>
    public const int MostNotificationKinds = 50;

    ///<summary>
    ///Every host API version this build can run, newest first. A list because a build that kept an
    ///older surface working needs to say so somewhere, as this one does for 1.0, which 1.1 only added
    ///to — <strong>and because the store has to answer "would this plugin run here" for entries
    ///compiled against versions this build never was</strong> (plugin-store criterion 12). An entry
    ///with nothing compatible is shown with what it needs rather than hidden, so an administrator
    ///learns that upgrading unlocks it.
    ///</summary>
    public static readonly IReadOnlyList<string> SupportedHostApiVersions = [CurrentHostApiVersion, "1.0"];

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("hostApiVersion")]
    public string HostApiVersion { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; }

    ///<summary>Where the code starts: <c>Example.Erp.dll!Example.Erp.Module</c> for a .NET plugin.</summary>
    [JsonPropertyName("entry")]
    public string Entry { get; set; }

    [JsonPropertyName("routes")]
    public string[] Routes { get; set; } = [];

    [JsonPropertyName("permissions")]
    public string[] Permissions { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public string[] Dependencies { get; set; } = [];

    ///<summary>
    ///The kinds of notification the plugin raises from its own container, through the API with the token
    ///issued to it (the notifications spec, question 9). Read from host API 1.1; a 1.0 manifest has none.
    ///</summary>
    [JsonPropertyName("notifications")]
    public PluginNotificationKind[] Notifications { get; set; } = [];

    ///<summary>
    ///Parses one manifest and refuses anything malformed or out of bounds, with a message written for
    ///the administrator who just tried to install the thing. Refusal is all-or-nothing on purpose — a
    ///manifest is a claim of identity, and half a claim is not a smaller claim.
    ///</summary>
    public static PluginManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PluginManifestException("The manifest is empty.");

        PluginManifest manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(json);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"The manifest is not valid JSON: {ex.Message}");
        }

        if (manifest == null)
            throw new PluginManifestException("The manifest is empty.");

        Require(manifest.Id, "id");
        Require(manifest.Name, "name");
        Require(manifest.Version, "version");
        Require(manifest.HostApiVersion, "hostApiVersion");
        Require(manifest.Kind, "kind");
        Require(manifest.Entry, "entry");

        if (manifest.Id.Any(char.IsWhiteSpace))
            throw new PluginManifestException($@"The id ""{manifest.Id}"" contains whitespace. An id is an identity and appears in storage keys and permission names — use reverse-DNS, like com.example.erp.");

        if (manifest.Kind != DotNetKind && manifest.Kind != JsKind)
            throw new PluginManifestException($@"The kind ""{manifest.Kind}"" is not one this host loads — it knows ""{DotNetKind}"" and ""{JsKind}"".");

        foreach (var route in manifest.Routes ?? [])
            if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/'))
                throw new PluginManifestException($@"The route ""{route}"" does not start with ""/"". Routes are absolute paths.");

        //The namespace rule (ADR 0008). Refused rather than trimmed: an install that silently dropped
        //a declared permission would leave the plugin broken in a way nobody chose.
        foreach (var permission in manifest.Permissions ?? [])
            if (string.IsNullOrWhiteSpace(permission) || !permission.StartsWith(manifest.Id + ".", StringComparison.Ordinal))
                throw new PluginManifestException(
                    $@"The manifest for ""{manifest.Id}"" declares the permission ""{permission}"", which is outside its own namespace. " +
                    $@"A plugin may only declare permissions under its own id — ""{manifest.Id}.something"" — and one that reaches further is refused whole.");

        RefuseNotifications(manifest);

        return manifest;
    }

    ///<summary>
    ///The kinds of notification it declares, held to the rules the permissions are: each under the plugin's own id, as
    ///a permission is, so it cannot pass for the host's or another plugin's, and each gated by one of the permissions it
    ///declares, so nobody hears of it who could not open what it is about (criterion 9). Refused whole, as a permission
    ///outside the namespace is.
    ///</summary>
    private static void RefuseNotifications(PluginManifest manifest)
    {
        var kinds = manifest.Notifications ?? [];

        if (kinds.Length == 0)
            return;

        if (!ReadsNotifications(manifest.HostApiVersion))
            throw new PluginManifestException($@"The manifest for ""{manifest.Id}"" declares notifications, which host API {NotificationsHostApiVersion} brought, and says it was built for {manifest.HostApiVersion}. Declare hostApiVersion {NotificationsHostApiVersion} to raise them.");

        if (kinds.Length > MostNotificationKinds)
            throw new PluginManifestException($@"The manifest for ""{manifest.Id}"" declares {kinds.Length} kinds of notification. {MostNotificationKinds} is the most one plugin may.");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kind in kinds)
        {
            var id = kind?.Id ?? "";

            if (id.Length == 0 || id.Length > 200 || id.Any(char.IsWhiteSpace) || !id.StartsWith(manifest.Id + ".", StringComparison.Ordinal))
                throw new PluginManifestException($@"The manifest for ""{manifest.Id}"" declares the notification kind ""{id}"", which is outside its own namespace. A kind's id is the plugin's id, a dot and a word, like ""{manifest.Id}.overdue"", as a permission's is.");

            if (!seen.Add(id))
                throw new PluginManifestException($@"The manifest for ""{manifest.Id}"" declares the notification kind ""{id}"" twice.");

            if (string.IsNullOrWhiteSpace(kind.Name) || kind.Name.Length > 200)
                throw new PluginManifestException($@"The notification kind ""{id}"" needs a name a person reads in their settings, 200 characters at most, like ""An invoice that is overdue"".");

            if (string.IsNullOrWhiteSpace(kind.Gate) || !(manifest.Permissions ?? []).Contains(kind.Gate, StringComparer.Ordinal))
                throw new PluginManifestException($@"The notification kind ""{id}"" is gated by ""{kind.Gate}"", which is not one of the permissions ""{manifest.Id}"" declares. Only somebody holding one of the plugin's own permissions hears of what it raises.");
        }
    }

    ///<summary>
    ///Whether this build runs a plugin built for this host API version: one of <see cref="SupportedHostApiVersions"/>.
    ///The loader's question, which the parser leaves to it: asked before an install by URL or zip proves the code, and of
    ///every record the installed list holds (InstalledPlugins.Refusal), since 2026-10-31. Until then only the store and
    ///an update by URL asked it.
    ///</summary>
    public static bool RunsHere(string hostApiVersion)
    {
        var api = (hostApiVersion ?? "").Trim();

        return api.Length > 0 && SupportedHostApiVersions.Any(one => string.Equals(one, api, StringComparison.OrdinalIgnoreCase));
    }

    ///<summary>What an install or the installed list says of a plugin built for a host API this build does not run.</summary>
    public static string HostApiRefusal(PluginManifest manifest)
    {
        return $@"""{manifest.Name}"" {manifest.Version} was built for host API {manifest.HostApiVersion}, which this build does not run: it runs {string.Join(" and ", SupportedHostApiVersions)}.";
    }

    ///<summary>Whether a manifest built for this host API version may declare notifications: 1.1 or later.</summary>
    public static bool ReadsNotifications(string hostApiVersion)
    {
        if (!System.Version.TryParse((hostApiVersion ?? "").Trim(), out var version))
            return false;

        return version >= System.Version.Parse(NotificationsHostApiVersion);
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PluginManifestException($@"The manifest is missing ""{field}"", which is required.");
    }
}

///<summary>Why a manifest was refused, worded for the person who tried to install it.</summary>
public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string message) : base(message)
    {
    }
}

///<summary>
///One kind of notification a plugin declares (host API 1.1): its id under the plugin's own, the name a person reads in
///their notification settings, and the one of the plugin's own permissions somebody needs to hear of one.
///</summary>
public sealed class PluginNotificationKind
{
    ///<summary>The plugin's id, a dot and a word: <c>com.example.erp.overdue</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    ///<summary>What a person reads in their settings and above each one: <em>An invoice that is overdue</em>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    ///<summary>One of the permissions the manifest declares: who may hear of one, checked again whenever it is read.</summary>
    [JsonPropertyName("gate")]
    public string Gate { get; set; }
}
