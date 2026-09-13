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
    ///whether the two are compatible is the loader's question (phase 3), not the parser's.
    ///</summary>
    public const string CurrentHostApiVersion = "1.0";

    ///<summary>
    ///Every host API version this build can run, newest first. One today, and a list because a build
    ///that kept an older surface working would need to say so somewhere — <strong>and because the store
    ///has to answer "would this plugin run here" for entries compiled against versions this build never
    ///was</strong> (plugin-store criterion 12). An entry with nothing compatible is shown with what it
    ///needs rather than hidden, so an administrator learns that upgrading unlocks it.
    ///</summary>
    public static readonly IReadOnlyList<string> SupportedHostApiVersions = [CurrentHostApiVersion];

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
        {
            if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/'))
                throw new PluginManifestException($@"The route ""{route}"" does not start with ""/"". Routes are absolute paths.");
        }

        //The namespace rule (ADR 0008). Refused rather than trimmed: an install that silently dropped
        //a declared permission would leave the plugin broken in a way nobody chose.
        foreach (var permission in manifest.Permissions ?? [])
        {
            if (string.IsNullOrWhiteSpace(permission) || !permission.StartsWith(manifest.Id + ".", StringComparison.Ordinal))
                throw new PluginManifestException(
                    $@"The manifest for ""{manifest.Id}"" declares the permission ""{permission}"", which is outside its own namespace. " +
                    $@"A plugin may only declare permissions under its own id — ""{manifest.Id}.something"" — and one that reaches further is refused whole.");
        }

        return manifest;
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
