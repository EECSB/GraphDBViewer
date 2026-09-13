using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///Where an uploaded plugin's bytes live once the browser has opened its zip and proved it.
///
///<strong>A plugin installed from a file still has to be fetchable at every boot</strong>, in every
///browser, by everyone — the assembly is loaded before <c>App</c> from a URL, and a zip on one
///administrator's laptop is not a URL. So the host keeps the bytes and serves them, and the record's
///<c>assemblyUrl</c> points here instead of at somebody else's server.
///
///<strong>The key is the content's own hash</strong>, which is the whole of the endpoint's safety: a
///caller cannot name a path, only bytes that already hash to what they claim, and the boot's existing
///hash check then verifies the answer exactly as it verifies a stranger's server.
///</summary>
public static class PluginUploads
{
    ///<summary>Where the host takes and serves them: <c>POST api/plugins/files</c>, <c>GET api/plugins/files/{hash}</c>.</summary>
    public const string Path = "api/plugins/files";

    ///<summary>The URL a record carries for uploaded bytes — relative, because it is this instance's own.</summary>
    public static string UrlOf(string hash)
    {
        return $"{Path}/{hash}";
    }

    ///<summary>Whether a record's assembly URL is one of ours, which is what "installed from a file" means downstream.</summary>
    public static bool IsUploaded(InstalledPluginRecord record)
    {
        var url = record?.AssemblyUrl;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.StartsWith(Path + "/", StringComparison.Ordinal);
    }

    ///<summary>
    ///The most one upload may be. Far above any plugin anybody has built here and far below anything
    ///that would trouble the host — the cap exists so a mistyped file is refused before it is read,
    ///not to ration disk.
    ///</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    ///<summary>What the browser sends: the assembly, as it came out of the zip.</summary>
    public const string ContentType = "application/octet-stream";

    ///<summary>
    ///What this instance is holding, and which of it no installed plugin claims:
    ///<c>GET api/plugins/files</c>.
    ///
    ///<strong>The file-system sibling of the graph's orphan audit.</strong> Uploaded code is removed
    ///when the last record naming it goes, but that delete is quiet on failure by design — and an
    ///instance restored from a backup has records and no files, or files and no records, because a
    ///backup carries neither half with the other. So there has to be a way to look.
    ///</summary>
    public const string SweepPath = Path;
}

///<summary>One assembly this instance is holding, and whether anything installed runs on it.</summary>
public sealed class HeldPlugin
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    ///<summary>The plugin running on it, or null when nothing is — which is what makes it an orphan.</summary>
    [JsonPropertyName("plugin")]
    public string Plugin { get; set; }

    ///<summary>The zip it arrived as, when the record that named it says so.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; }

    public bool IsOrphan
    {
        get
        {
            return string.IsNullOrWhiteSpace(Plugin);
        }
    }
}

///<summary>
///What the sweep answers: everything held, and the records that name a file this instance does not have.
///
///<strong>The second half is the one nobody expects.</strong> A record pointing at bytes that are gone
///is a plugin that will fail at everyone's next boot with a 404 in the quarantine mark — and it is what
///a restored backup looks like, because the database is carried and the folder beside it is not.
///</summary>
public sealed class PluginFileSweep
{
    [JsonPropertyName("held")]
    public List<HeldPlugin> Held { get; set; } = new();

    ///<summary>Installed plugins whose code this instance does not have. By plugin id.</summary>
    [JsonPropertyName("missing")]
    public List<string> Missing { get; set; } = new();
}

///<summary>What the host kept, and where the boot will find it again.</summary>
public sealed class UploadedPlugin
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    ///<summary>The relative URL to fetch these bytes from — what goes on the record.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    ///<summary>Whether these exact bytes were already here — a reinstall of the same build, which costs nothing.</summary>
    [JsonPropertyName("existed")]
    public bool Existed { get; set; }
}
