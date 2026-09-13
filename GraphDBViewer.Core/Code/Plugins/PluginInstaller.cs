using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///The install flow's IO half (plugin-system task 14), driven by the Plugins page: fetch a manifest by
///URL, refuse or prove it, and save the rewritten list. Install is by URL — there is no catalog and
///no marketplace, deliberately. Everything here answers with an error string for the page to show
///rather than throwing, because every failure is an administrator standing there wanting to know why.
///</summary>
public sealed class PluginInstaller
{
    private readonly HttpClient _http;

    public PluginInstaller(HttpClient http)
    {
        _http = http;
    }

    ///<summary>
    ///The manifest alone — no assembly fetched, nothing proved, nothing loaded.
    ///
    ///<strong>What "check for updates" needs, and the most it should take.</strong> Asking whether a
    ///newer version exists must not download and hash a stranger's assembly: that is the expensive half
    ///and the dangerous half, and it belongs to deciding to install rather than to looking.
    ///</summary>
    public async Task<(PluginManifest Manifest, string Error)> FetchManifestAsync(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var source) || (source.Scheme != "https" && source.Scheme != "http"))
            return (null, "That is not an absolute http(s) URL. Point it at the plugin's manifest.json.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return (null, $"The manifest could not be fetched: the server answered {(int)response.StatusCode}.");

            return (PluginManifest.Parse(await response.Content.ReadAsStringAsync()), null);
        }
        catch (PluginManifestException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"The manifest could not be fetched: {ex.Message}");
        }
    }

    ///<summary>
    ///Fetches, parses, refuses or proves one plugin, and answers with the record to add — not yet
    ///saved, not yet active. The assembly is fetched from beside the manifest (the entry's file
    ///resolved against the manifest URL), hashed to pin which bytes were approved, and loaded once
    ///against throwaway registries: finding a missing method now is recoverable; finding it at the
    ///next boot is the quarantine doing damage control (task 13).
    ///</summary>
    ///<param name="expectedHash">
    ///The SHA-256 the registry reviewed, when this install came from the store — so the bytes fetched
    ///are the bytes somebody looked at (plugin-store criterion 13). Null on a manual-URL install, where
    ///there is nothing to compare against and the URL is the whole of the trust.
    ///</param>
    ///<param name="entry">
    ///The store entry, when there is one: its repository becomes the identity marker (ADR 0012 as the
    ///store reshapes it — an origin distinguishes nothing when every release is on github.com), and its
    ///source is remembered so the page knows which records the store may update.
    ///</param>
    public async Task<(InstalledPluginRecord Record, string Error)> FetchAndProveAsync(
        string manifestUrl,
        IReadOnlyCollection<string> takenIds,
        string expectedHash = null,
        StoreEntry entry = null)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var source) || (source.Scheme != "https" && source.Scheme != "http"))
            return (null, "That is not an absolute http(s) URL. Point it at the plugin's manifest.json.");

        string manifestJson;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return (null, $"The manifest could not be fetched: the server answered {(int)response.StatusCode}.");

            manifestJson = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return (null, $"The manifest could not be fetched: {ex.Message}");
        }

        PluginManifest manifest;

        try
        {
            manifest = PluginManifest.Parse(manifestJson);
        }
        catch (PluginManifestException ex)
        {
            return (null, ex.Message);
        }

        //Before "taken", so a bundled id pasted from a URL gets the reason that matters: the prefix is
        //reserved, not merely occupied.
        if (PluginIds.IsReserved(manifest.Id))
            return (null, PluginIds.ReservedRefusal(manifest.Id));

        if (takenIds.Contains(manifest.Id))
            return (null, $@"""{manifest.Id}"" is already here. Uninstall it first — installing over a plugin is not yet a thing.");

        string assemblyUrl;
        byte[] bytes;

        try
        {
            var (assemblyFile, _) = DotNetAssemblyLoader.ParseEntry(manifest.Entry);

            //The entry's file, beside the manifest — a plugin is a folder with a manifest in it.
            assemblyUrl = new Uri(source, assemblyFile).ToString();

            bytes = await _http.GetByteArrayAsync(assemblyUrl);
        }
        catch (PluginLoadException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"The assembly could not be fetched: {ex.Message}");
        }

        var hash = InstalledPluginRecord.HashOf(bytes);

        //Before proving: what the registry approved is not these bytes, so nothing is learned by
        //loading them. Both hashes go in the message, because "the hash did not match" is not an
        //answer anybody can act on.
        var mismatch = HashRefusal(expectedHash, hash);

        if (mismatch != null)
            return (null, mismatch);

        var refusal = DotNetAssemblyLoader.Prove(bytes, manifest);

        if (refusal != null)
            return (null, refusal);

        return (new InstalledPluginRecord
        {
            Manifest = manifest,
            Source = source.ToString(),
            AssemblyUrl = assemblyUrl,
            Hash = hash,
            Enabled = true,
            InstalledUtc = DateTimeOffset.UtcNow,
            Repository = entry?.Repository,
            StoreSource = entry?.Source
        }, null);
    }

    ///<summary>
    ///A newer version of something already installed, proved the same way and answered as a REPLACEMENT
    ///record rather than a second one (plugin-store, update).
    ///
    ///<strong>What carries over is what the id owns</strong>: whether it is enabled, and when it was
    ///first installed. Nothing about its data, its settings blob or its permission grants is touched,
    ///because none of them is keyed by anything that changed — the id is the same id. A permission the
    ///new manifest declares that the old one did not is registered at the next boot and granted to
    ///nobody, which is the platform's posture everywhere.
    ///
    ///The id is excluded from <paramref name="takenIds"/> by the caller, and only that one: an update
    ///that collided with a DIFFERENT installed id would still be refused, which is right.
    ///</summary>
    public async Task<(InstalledPluginRecord Record, string Error)> FetchUpdateAsync(
        InstalledPluginRecord installed,
        string manifestUrl,
        IReadOnlyCollection<string> takenIds,
        string expectedHash = null,
        StoreEntry entry = null)
    {
        if (installed?.Manifest?.Id == null)
            return (null, "There is nothing here to update.");

        var (record, error) = await FetchAndProveAsync(manifestUrl, takenIds, expectedHash, entry);

        if (error != null)
            return (null, error);

        //An update that arrived under a different id is not an update. Nothing downstream would catch
        //it: the page swaps by position, and the record would take the old id's settings and grants.
        if (!string.Equals(record.Manifest.Id, installed.Manifest.Id, StringComparison.Ordinal))
            return (null, $@"That manifest is ""{record.Manifest.Id}"", not ""{installed.Manifest.Id}"". An update has to be the same plugin.");

        record.Enabled = installed.Enabled;
        record.InstalledUtc = installed.InstalledUtc;
        record.UpdatedUtc = DateTimeOffset.UtcNow;

        //A store update carries the entry's repository; an update by URL keeps whatever marker the
        //install had, because nothing about it changed.
        record.Repository ??= installed.Repository;
        record.StoreSource ??= installed.StoreSource;

        return (record, null);
    }

    //── Installing from a file ─────────────────────────────────────────────────────────────────

    ///<summary>
    ///A plugin out of a zip an administrator picked: opened, refused or proved, and answered as the
    ///record to add — <em>with the assembly bytes beside it</em>, because they are not on the server
    ///yet and the caller is what puts them there.
    ///
    ///<strong>Nothing is uploaded here, deliberately.</strong> The trust-on-first-use prompt sits
    ///between proving and committing, and an install somebody walks away from at that prompt must
    ///leave no code on the instance. So the record carries the URL the bytes <em>will</em> have — it
    ///is the hash, so it is knowable now — and <see cref="UploadAsync"/> is what makes it true.
    ///
    ///Synchronous because none of it is IO: the file is already in memory, and the zip, the manifest
    ///and the load-to-prove all happen in the browser.
    ///</summary>
    public (InstalledPluginRecord Record, byte[] Assembly, string Error) OpenAndProve(string fileName, byte[] zip, IReadOnlyCollection<string> takenIds)
    {
        if (zip != null && zip.LongLength > PluginUploads.MaxBytes)
            return (null, null, $"That file is {Megabytes(zip.LongLength)}, past the {Megabytes(PluginUploads.MaxBytes)} a plugin package may be.");

        var (package, error) = PluginPackage.Read(zip);

        if (error != null)
            return (null, null, error);

        //The same two refusals an install by URL makes, in the same order and for the same reasons:
        //reserved before taken, so a bundled id gets told the prefix is reserved rather than occupied.
        if (PluginIds.IsReserved(package.Manifest.Id))
            return (null, null, PluginIds.ReservedRefusal(package.Manifest.Id));

        if (takenIds.Contains(package.Manifest.Id))
            return (null, null, $@"""{package.Manifest.Id}"" is already here. Uninstall it first — installing over a plugin is not yet a thing.");

        var refusal = DotNetAssemblyLoader.Prove(package.Assembly, package.Manifest);

        if (refusal != null)
            return (null, null, refusal);

        var hash = InstalledPluginRecord.HashOf(package.Assembly);

        return (new InstalledPluginRecord
        {
            Manifest = package.Manifest,
            //No source: a file is not a URL, and leaving this null is what stops "check for updates"
            //offering to look somewhere that was never looked at.
            Source = null,
            AssemblyUrl = PluginUploads.UrlOf(hash),
            Hash = hash,
            UploadedFile = NameOf(fileName),
            Enabled = true,
            InstalledUtc = DateTimeOffset.UtcNow
        }, package.Assembly, null);
    }

    ///<summary>
    ///Hands the proved assembly to the instance, which keeps it under its own hash and serves it back
    ///at every boot. Null on success, else the reason — and a mismatch between what was sent and what
    ///the instance says it kept is one of them, because a record pointing at bytes nobody checked is
    ///exactly what the boot's hash check exists to catch, one boot too late.
    ///</summary>
    public async Task<string> UploadAsync(byte[] assembly)
    {
        if (assembly == null || assembly.Length == 0)
            return "There is no assembly to keep — nothing was uploaded.";

        if (assembly.LongLength > PluginUploads.MaxBytes)
            return $"That assembly is {Megabytes(assembly.LongLength)}, past the {Megabytes(PluginUploads.MaxBytes)} an upload may be.";

        UploadedPlugin kept;

        try
        {
            using var content = new ByteArrayContent(assembly);

            content.Headers.ContentType = new MediaTypeHeaderValue(PluginUploads.ContentType);

            var response = await _http.PostAsync(PluginUploads.Path, content);

            if (!response.IsSuccessStatusCode)
                return $"The plugin's code could not be kept on this instance: the server answered {(int)response.StatusCode}. {await response.Content.ReadAsStringAsync()}".TrimEnd();

            kept = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<UploadedPlugin>(response.Content);
        }
        catch (Exception ex)
        {
            return $"The plugin's code did not reach the server: {ex.Message}";
        }

        var sent = InstalledPluginRecord.HashOf(assembly);

        if (kept == null || !string.Equals(kept.Hash, sent, StringComparison.OrdinalIgnoreCase))
            return $"The instance kept something other than what was sent — {sent} went up and it answered {kept?.Hash ?? "nothing"}. Nothing was installed.";

        return null;
    }

    ///<summary>A picked file's own name, with any path a browser might have put in front of it dropped. Display only.</summary>
    private static string NameOf(string fileName)
    {
        var name = (fileName ?? "").Trim();

        var cut = name.LastIndexOfAny(['/', '\\']);

        if (cut >= 0)
            name = name.Substring(cut + 1);

        if (name.Length > 120)
            name = name.Substring(0, 120);

        return name;
    }

    private static string Megabytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    ///<summary>Why these bytes are not the reviewed ones, or null. Both hashes, because one is not something anybody can act on.</summary>
    public static string HashRefusal(string expected, string actual)
    {
        var wanted = (expected ?? "").Trim();

        if (wanted.Length == 0)
            return null;

        if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase))
            return null;

        return $"The assembly is not the one the registry reviewed. The entry says {wanted}; what was fetched hashes to {actual}. Nothing was installed.";
    }

    ///<summary>Writes the whole list back — install, uninstall, enable and disable are all this one
    ///PUT, gated server-side on the install permission. Null on success, else the reason.</summary>
    public async Task<string> SaveAsync(IReadOnlyList<InstalledPluginRecord> records)
    {
        try
        {
            var response = await _http.PutAsync(
                InstanceStateContract.PathFor(InstanceStateContract.InstalledPluginsKey),
                new StringContent(InstalledPlugins.Serialize(records), Encoding.UTF8, InstanceStateContract.ContentType));

            if (!response.IsSuccessStatusCode)
                return $"The server refused the save ({(int)response.StatusCode}).";

            return null;
        }
        catch (Exception ex)
        {
            return $"The save did not reach the server: {ex.Message}";
        }
    }
}
