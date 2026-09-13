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

    ///<summary>Serializes the list the way <see cref="Parse"/> reads it — what the install flow PUTs.</summary>
    public static string Serialize(IReadOnlyList<InstalledPluginRecord> records)
    {
        return JsonSerializer.Serialize(records);
    }

    ///<summary>
    ///Which bundled plugins an administrator removed — read at boot the way the installed list is,
    ///with the same manners: every failure answers "none disabled", and the fetch is best-effort
    ///because a bundled plugin wrongly present beats a boot that broke.
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

            var ids = JsonSerializer.Deserialize<string[]>(await response.Content.ReadAsStringAsync(cts.Token));

            return ids ?? [];
        }
        catch
        {
            return [];
        }
    }

    ///<summary>
    ///The stored value is a JSON array of <see cref="InstalledPluginRecord"/>. Each record's manifest
    ///is re-checked against the manifest rules on the way in — the namespace refusal included — so a
    ///value edited behind the API's back gets no further than a refused install would have. Public
    ///because the server reads the same value through its own store — the role editor listing a
    ///plugin's declared permissions, the settings writes resolving their gate.
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
            try
            {
                var record = element.Deserialize<InstalledPluginRecord>();

                if (record == null || record.Manifest == null || string.IsNullOrWhiteSpace(record.AssemblyUrl) || string.IsNullOrWhiteSpace(record.Hash))
                    continue;

                //The same gate an install passes through, applied again at read time.
                PluginManifest.Parse(JsonSerializer.Serialize(record.Manifest));

                records.Add(record);
            }
            catch (PluginManifestException)
            {
                //One bad entry must not cost the rest — the quarantine story is where a bad plugin
                //gets surfaced and removed; the list reader just declines to trip over it.
            }
            catch (JsonException)
            {
            }
        }

        return records;
    }
}
