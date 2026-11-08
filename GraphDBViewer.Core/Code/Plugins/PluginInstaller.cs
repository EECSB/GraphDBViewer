using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///The install flow's IO half (plugin-system task 14), driven by the Plugins page: fetch a manifest by
///URL, refuse or prove it, and hand the host the one change to make to the list (since 2026-09-23; the
///page used to save its whole copy). Install is by URL, from the plugin store's index, or from a zip.
///Everything here answers with an error string for the page to show rather than throwing, because every
///failure is an administrator standing there wanting to know why. A plugin whose manifest or code would
///come over plain http with no sha256 to check it by is installed all the same, once whoever installs it
///has been told so (<see cref="PlainWarning(string, string, string, string)"/>, 2026-10-04).
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
    ///<param name="goOnOverPlainHttp">
    ///Asked, with <see cref="PlainWarning(string, string, string, string)"/>'s sentence, before code that would come over
    ///plain http with nothing to check it by is fetched, since proving it runs it (2026-10-04); false installs nothing,
    ///and answers <see cref="PlainDeclined"/>. The page asks the person, unless it said the same before they pressed the
    ///button. Null goes on without asking.
    ///</param>
    public async Task<(InstalledPluginRecord Record, string Error)> FetchAndProveAsync(
        string manifestUrl,
        IReadOnlyCollection<string> takenIds,
        string expectedHash = null,
        StoreEntry entry = null,
        Func<string, Task<bool>> goOnOverPlainHttp = null)
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

        //Before the code is fetched, since proving it runs it.
        if (!PluginManifest.RunsHere(manifest.HostApiVersion))
            return (null, PluginManifest.HostApiRefusal(manifest));

        //Before "taken", so a bundled id pasted from a URL gets the reason that matters: the prefix is
        //reserved, not merely occupied.
        if (PluginIds.IsReserved(manifest.Id))
            return (null, PluginIds.ReservedRefusal(manifest.Id));

        if (takenIds.Contains(manifest.Id))
            return (null, PluginIds.AlreadyHereRefusal(manifest.Id));

        string assemblyUrl;
        byte[] bytes;

        try
        {
            var (assemblyFile, _) = DotNetAssemblyLoader.ParseEntry(manifest.Entry);

            //The entry's file, beside the manifest — a plugin is a folder with a manifest in it.
            assemblyUrl = new Uri(source, assemblyFile).ToString();
        }
        catch (PluginLoadException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"The assembly could not be fetched: {ex.Message}");
        }

        //Before the code is fetched, since proving it runs it: whoever installs is told first when it would come over
        //plain http with nothing to check it by (2026-10-04).
        var plain = PlainWarning(source.ToString(), assemblyUrl, expectedHash, entry?.Source);

        if (plain != null && goOnOverPlainHttp != null && !await goOnOverPlainHttp(plain))
            return (null, PlainDeclined);

        try
        {
            bytes = await _http.GetByteArrayAsync(assemblyUrl);
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

        //The store's hash these bytes were just checked against, kept so the host can tell whether code that came over
        //plain http was checked by anything.
        string checkedAgainst = null;

        if (!string.IsNullOrWhiteSpace(expectedHash))
            checkedAgainst = hash;

        return (new InstalledPluginRecord
        {
            Manifest = manifest,
            Source = source.ToString(),
            AssemblyUrl = assemblyUrl,
            Hash = hash,
            Enabled = true,
            InstalledUtc = DateTimeOffset.UtcNow,
            Repository = entry?.Repository,
            StoreSource = entry?.Source,
            Sha256 = checkedAgainst
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
        StoreEntry entry = null,
        Func<string, Task<bool>> goOnOverPlainHttp = null)
    {
        if (installed?.Manifest?.Id == null)
            return (null, "There is nothing here to update.");

        var (record, error) = await FetchAndProveAsync(manifestUrl, takenIds, expectedHash, entry, goOnOverPlainHttp);

        if (error != null)
            return (null, error);

        //An update that arrived under a different id is not an update. The host refuses it too, but this
        //is where a person sees why.
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
            return (null, null, $"That file is {ByteSizes.Human(zip.LongLength)}, past the {ByteSizes.Human(PluginUploads.MaxBytes)} a plugin package may be.");

        var (package, error) = PluginPackage.Read(zip);

        if (error != null)
            return (null, null, error);

        //The same two refusals an install by URL makes, in the same order and for the same reasons:
        //reserved before taken, so a bundled id gets told the prefix is reserved rather than occupied.
        if (PluginIds.IsReserved(package.Manifest.Id))
            return (null, null, PluginIds.ReservedRefusal(package.Manifest.Id));

        if (takenIds.Contains(package.Manifest.Id))
            return (null, null, PluginIds.AlreadyHereRefusal(package.Manifest.Id));

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
            return $"That assembly is {ByteSizes.Human(assembly.LongLength)}, past the {ByteSizes.Human(PluginUploads.MaxBytes)} an upload may be.";

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

    //── Over plain http ────────────────────────────────────────────────────────────────────────

    ///<summary>What an install answers when the person chose not to go on over plain http: a notice rather than an error.</summary>
    public const string PlainDeclined = "Nothing was installed, and its code was not fetched.";

    ///<summary>
    ///What whoever installs is told before the install is made, when the plugin's manifest or code would come over plain
    ///http with no sha256 to check the code by; null when nothing would (decided with the user on 2026-10-04). It is said
    ///and never refused, since plain stays a choice: under the address in the install form, on a store card, before the
    ///code is fetched when only the manifest names it, and in the host's log once it is installed (the host's
    ///PluginInstallsApi).
    ///
    ///<strong>Over http, anyone on the network between can hand this instance other code</strong>, which it would install
    ///and run as this plugin: an install by URL is checked against no hash. A store entry's sha256 checks the code, but
    ///only one from an index that came over https; from an index over http, whoever could change the code could change
    ///the hash beside it, and the manifest's address too.
    ///</summary>
    ///<param name="manifest">The manifest's address: the one typed, or a store version's.</param>
    ///<param name="assembly">The code's address once the manifest has named it, or null before.</param>
    ///<param name="sha256">The sha256 a store version gives for the code, or null.</param>
    ///<param name="index">The store index the version is in, or null for an install by URL.</param>
    public static string PlainWarning(string manifest, string assembly, string sha256, string index)
    {
        if (IsPlain(index))
            return $"This plugin's store index, {index}, comes over plain http, so the sha256 it gives checks nothing: anyone on the network between here and there could change the code and its hash together, and this instance would install their code as this plugin. An https address for the index takes that away.";

        if (!string.IsNullOrWhiteSpace(sha256))
            return null;

        if (IsPlain(manifest) || IsPlain(assembly))
            return "This plugin comes over plain http with no sha256 to check its code by: anyone on the network between here and its server could hand this instance code of their own, and it would be installed as this plugin. An https address takes that away, as does a store entry that gives its code's sha256.";

        return null;
    }

    ///<summary>
    ///The same, of an installed record, which is how the host reads it: the store's sha256 counts only when it is the
    ///hash of the code the record pins.
    ///</summary>
    public static string PlainWarning(InstalledPluginRecord record)
    {
        if (record == null)
            return null;

        var sha256 = record.Sha256;

        if (!string.Equals((sha256 ?? "").Trim(), record.Hash, StringComparison.OrdinalIgnoreCase))
            sha256 = null;

        return PlainWarning(record.Source, record.AssemblyUrl, sha256, record.StoreSource);
    }

    ///<summary>
    ///Where a manifest's code is fetched from: its entry's file resolved against the manifest's own address, which may
    ///name another server altogether. Null when the entry is not one.
    ///</summary>
    public static string AssemblyUrlOf(string manifestUrl, PluginManifest manifest)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var source) || manifest == null)
            return null;

        try
        {
            var (assemblyFile, _) = DotNetAssemblyLoader.ParseEntry(manifest.Entry);

            return new Uri(source, assemblyFile).ToString();
        }
        catch (Exception ex) when (ex is PluginLoadException || ex is UriFormatException)
        {
            return null;
        }
    }

    private static bool IsPlain(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp;
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

    //── Changing the list ─────────────────────────────────────────────────────────────────────

    //Each of these is one change, made by the host to the list as it stands and answered with the list
    //as it now is, other administrators' changes included; that is what the page shows next. The page
    //used to write its whole copy back, and of two administrators the later undid the earlier
    //(PluginInstallsContract).

    ///<summary>
    ///Installs a proved record. The host refuses it if the id got there first, and writes the identity
    ///marker with it when asked.
    ///</summary>
    public Task<(IReadOnlyList<InstalledPluginRecord> Records, string Error)> InstallAsync(InstalledPluginRecord record, bool recordIdentity)
    {
        return ChangeAsync(HttpMethod.Post, PluginInstallsContract.InstalledPath, new PluginInstallChange { Record = record, RecordIdentity = recordIdentity });
    }

    ///<summary>
    ///Puts a proved newer version where the old one stands. What the id owns (enabled, the install date)
    ///is carried over by the host from the record as it is now, not from the page's copy.
    ///</summary>
    public Task<(IReadOnlyList<InstalledPluginRecord> Records, string Error)> UpdateAsync(InstalledPluginRecord record, bool recordIdentity)
    {
        return ChangeAsync(HttpMethod.Put, PluginInstallsContract.InstalledPathFor(record.Manifest.Id), new PluginInstallChange { Record = record, RecordIdentity = recordIdentity });
    }

    ///<summary>The state it should be in, not a flip: two administrators disabling it at once both get a disabled plugin.</summary>
    public Task<(IReadOnlyList<InstalledPluginRecord> Records, string Error)> SetEnabledAsync(string id, bool enabled)
    {
        return ChangeAsync(HttpMethod.Post, PluginInstallsContract.EnabledPathFor(id), new PluginEnabledChange { Enabled = enabled });
    }

    ///<summary>Out of the list. Already gone answers the list too, so the cleanup after it runs either way.</summary>
    public Task<(IReadOnlyList<InstalledPluginRecord> Records, string Error)> UninstallAsync(string id)
    {
        return ChangeAsync(HttpMethod.Delete, PluginInstallsContract.UninstallPathFor(id), null);
    }

    ///<summary>A bundled plugin removed or restored, answered with the ids removed as they now stand.</summary>
    public async Task<(IReadOnlyList<string> Disabled, string Error)> SetBundledDisabledAsync(string id, bool disabled)
    {
        var (answer, error) = await SendAsync(HttpMethod.Post, PluginInstallsContract.BundledPathFor(id), new BundledPluginChange { Disabled = disabled });

        if (error != null)
            return (null, error);

        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<string[]>(answer) ?? [], null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "The server answered something other than the plugins it has removed.");
        }
    }

    private async Task<(IReadOnlyList<InstalledPluginRecord> Records, string Error)> ChangeAsync(HttpMethod method, string path, object change)
    {
        var (answer, error) = await SendAsync(method, path, change);

        if (error != null)
            return (null, error);

        return (InstalledPlugins.Parse(answer), null);
    }

    ///<summary>
    ///The body goes as <see cref="System.Text.Json.JsonSerializer"/> writes it by default, which is how the
    ///host reads it. A refusal comes back as the host's own sentence, which is what the page shows.
    ///</summary>
    private async Task<(string Answer, string Error)> SendAsync(HttpMethod method, string path, object change)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);

            if (change != null)
                request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(change, change.GetType()), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
                return (text, null);

            if (response.Content.Headers.ContentType?.MediaType == "text/plain" && !string.IsNullOrWhiteSpace(text))
                return (null, text.Trim());

            return (null, $"The server refused the change ({(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (null, $"The change did not reach the server: {ex.Message}");
        }
    }
}
