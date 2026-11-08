using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;

namespace GraphDBViewerWeb.Code;

///<summary>
///What the boot-time read of the installed-plugin list found — kept as a singleton because the read
///can never be repeated by a registered service: it has to happen <em>before</em> <c>Build()</c>, a
///plugin's <c>ConfigureServices</c> having to run before the container seals (ADR 0006's boot-order
///constraint). The loader consumes it at that same moment; the Plugins page reads it after.
///</summary>
public sealed class InstalledPlugins
{
    public InstalledPlugins(IReadOnlyList<InstalledPluginRecord> records)
    {
        Records = records;
    }

    ///<summary>
    ///True when the list could not be fetched at all — a timeout or an unreachable host — as opposed to
    ///an answer of none (a fresh instance) or a refusal (signed out). The records are empty either way;
    ///what differs is whether that emptiness is known to be true.
    ///</summary>
    public bool Unavailable { get; init; }

    ///<summary>What is installed — empty on a fresh instance, and empty whenever the list could not
    ///be read (see <see cref="LoadAsync"/> for why that is a normal answer).</summary>
    public IReadOnlyList<InstalledPluginRecord> Records { get; }

    ///<summary>
    ///Reads and parses the list by bare HttpClient, the way DevSecrets is fetched — nothing else
    ///exists yet. Every failure answers an empty list, because boot must always get far enough to
    ///draw the sign-in page: a 401 is simply a signed-out visitor, a 204 a fresh instance — and one
    ///unparsable entry skips that entry, not the boot. A timeout or an unreachable host answers the
    ///same empty list marked <see cref="Unavailable"/>: nothing is known, and the Plugins and
    ///not-found pages say so instead of calling every record "waiting for a reload". Fetched
    ///<c>no-store</c>: only the hash-named assemblies are cacheable, never the list itself.
    ///</summary>
    public static async Task<InstalledPlugins> LoadAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Get, InstanceStateContract.PathFor(InstanceStateContract.InstalledPluginsKey));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return new InstalledPlugins([]);

            var json = await response.Content.ReadAsStringAsync(cts.Token);

            return new InstalledPlugins(Parse(json));
        }
        catch
        {
            //The host answered nothing at all — timed out or unreachable. The same empty list, marked,
            //because an answer of none and no answer are different facts.
            return new InstalledPlugins([]) { Unavailable = true };
        }
    }

    ///<summary>Serializes the list the way <see cref="Parse"/> reads it: what the host stores, and answers every change to the list with.</summary>
    public static string Serialize(IReadOnlyList<InstalledPluginRecord> records)
    {
        return JsonSerializer.Serialize(records);
    }

    ///<summary>
    ///Which bundled plugins an administrator removed — read at boot the way the installed list is,
    ///with the same manners: every failure answers "none disabled", and the fetch is best-effort
    ///because a bundled plugin wrongly present beats a boot that broke. What it answers is read by
    ///<see cref="ParseDisabledBundled"/>, as the host reads it.
    ///</summary>
    public static async Task<IReadOnlyList<string>> DisabledBundledAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var request = new HttpRequestMessage(HttpMethod.Get, InstanceStateContract.PathFor(InstanceStateContract.DisabledBundledKey));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return [];

            return ParseDisabledBundled(await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch
        {
            return [];
        }
    }

    ///<summary>
    ///The removed bundled ids in the stored value, a JSON array of strings, blanks and repeats left out.
    ///<strong>Anything else reads as none removed</strong>, deliberately: the set is written by the host,
    ///and a garbled one is a fault in the host, so the safe reading is the one that leaves a working
    ///plugin working rather than silently retiring every bundled plugin at once.
    ///
    ///The boot (<see cref="DisabledBundledAsync"/>), the server asking whether a bundled plugin is gone,
    ///and the host changing the set all read it here. Until 2026-09-26 each of the three parsed it
    ///itself (the 2026-09-24 audit), and the server's parse had once been written five times, one copy
    ///for each perspective.
    ///</summary>
    public static IReadOnlyList<string> ParseDisabledBundled(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    ///<summary>
    ///The stored value is a JSON array of <see cref="InstalledPluginRecord"/>. Each record is checked on
    ///the way in by <see cref="Refusal"/>, the check the host makes before it stores one — the manifest's
    ///rules, the namespace refusal and the reserved prefix among them — so a value edited behind the
    ///host's back gets no further than a refused install would have. Public because the server reads the
    ///same value through its own store — its changes to the list, the role editor listing a plugin's
    ///declared permissions, the settings writes resolving their gate.
    ///</summary>
    public static IReadOnlyList<InstalledPluginRecord> Parse(string json)
    {
        var records = new List<InstalledPluginRecord>();

        if (string.IsNullOrWhiteSpace(json))
            return records;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            //A value that is not even JSON is a value nobody wrote through the install flow.
            return records;
        }

        using var _ = document;

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return records;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            InstalledPluginRecord record;

            try
            {
                record = element.Deserialize<InstalledPluginRecord>();
            }
            catch (JsonException)
            {
                continue;
            }

            //One bad entry must not cost the rest — the quarantine story is where a bad plugin gets
            //surfaced and removed; the list reader just declines to trip over it.
            if (Refusal(record) == null)
                records.Add(record);
        }

        return records;
    }

    ///<summary>
    ///Why this record cannot be in the list, or null: there is no plugin in it, it does not say where its
    ///code is or what that code hashes to, its manifest breaks the manifest's rules, or its id is under
    ///the reserved prefix, which belongs to the plugins that ship with Treeality (ADR 0012), and those
    ///are build-time references that never pass through an install.
    ///
    ///<strong>One check, made at both ends of the list.</strong> The host refuses to store a record this
    ///refuses, and <see cref="Parse"/> skips one it finds there anyway. Until 2026-09-26 the host made a
    ///copy of these checks, and the two disagreed (the 2026-09-24 audit): the host refused a reserved id
    ///and the reader took it, so a plugin under the prefix that reached the list another way loaded at
    ///every boot, looking official in every list, where the host's own reads hold that no installed id is
    ///reserved. The host's refusal of a "%" in an id is its own and not here: an id listed with one
    ///before that refusal has to stay readable, so that it can still be uninstalled.
    ///</summary>
    public static string Refusal(InstalledPluginRecord record)
    {
        if (record?.Manifest == null || string.IsNullOrWhiteSpace(record.Manifest.Id))
            return "There is no plugin in that. An install carries the record the page proved, manifest and all.";

        if (string.IsNullOrWhiteSpace(record.AssemblyUrl) || string.IsNullOrWhiteSpace(record.Hash))
            return "That record does not say where its code is, or what its code hashes to.";

        try
        {
            PluginManifest.Parse(JsonSerializer.Serialize(record.Manifest));
        }
        catch (PluginManifestException ex)
        {
            return ex.Message;
        }

        if (PluginIds.IsReserved(record.Manifest.Id))
            return PluginIds.ReservedRefusal(record.Manifest.Id);

        if (!PluginManifest.RunsHere(record.Manifest.HostApiVersion))
            return PluginManifest.HostApiRefusal(record.Manifest);

        return null;
    }
}
