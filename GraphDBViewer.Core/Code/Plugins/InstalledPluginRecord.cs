using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///One installed plugin, as the instance list stores it: the manifest it was installed from, where its
///assembly lives, the hash that pins which bytes those are, and whether it is currently enabled. The
///whole install flow writes these; the boot loader reads them. Bundled plugins never appear here —
///they are build-time references and do not pass through the install path.
///</summary>
public sealed class InstalledPluginRecord
{
    [JsonPropertyName("manifest")]
    public PluginManifest Manifest { get; set; }

    ///<summary>
    ///The manifest URL it was installed from — where an update would be looked for. <strong>Null for a
    ///plugin installed from an uploaded zip</strong>, which came from a file and has nowhere to look:
    ///its update is another upload, and <see cref="UploadedFile"/> is what it is shown by instead.
    ///</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; }

    ///<summary>
    ///Where the assembly is fetched from on a cache miss: the entry's file beside the manifest, or —
    ///for an upload — this instance's own <see cref="PluginUploads.Path"/> under the assembly's hash.
    ///</summary>
    [JsonPropertyName("assemblyUrl")]
    public string AssemblyUrl { get; set; }

    ///<summary>
    ///The name of the zip an administrator uploaded, kept so the list can say where the plugin came
    ///from. Display only: nothing is fetched by it and nothing is compared against it — a file name is
    ///not identity, which is why an upload's identity marker is the assembly's hash instead.
    ///</summary>
    [JsonPropertyName("uploadedFile")]
    public string UploadedFile { get; set; }

    ///<summary>
    ///SHA-256 of the assembly bytes, lowercase hex. The byte cache is keyed by this, and a fresh
    ///fetch is verified against it — the URL is allowed to change what it serves; the install is not.
    ///</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    ///<summary>Disabled means kept but not loaded — flipped from the Plugins page, applied on reload.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("installedUtc")]
    public DateTimeOffset InstalledUtc { get; set; }

    ///<summary>When it was last updated in place, or null if never. The install date stays the install date.</summary>
    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset? UpdatedUtc { get; set; }

    ///<summary>
    ///The store entry's repository, for anything installed from a store — <strong>the identity marker's
    ///new shape</strong>. ADR 0012's trust-on-first-use marker is the install URL's ORIGIN, which
    ///distinguishes nothing on a store where every release is on github.com; the repository does.
    ///Null for a manual-URL install, where the origin is still the marker.
    ///</summary>
    [JsonPropertyName("repository")]
    public string Repository { get; set; }

    ///<summary>The index URL the entry came from, so the page knows which records that store may update. Null for a manual-URL install.</summary>
    [JsonPropertyName("storeSource")]
    public string StoreSource { get; set; }

    ///<summary>The one hash algorithm the platform uses for plugin bytes.</summary>
    public static string HashOf(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
