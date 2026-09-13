using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>What the host answers about where the store reads from.</summary>
public sealed class StoreSourcesAnswer
{
    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = new();
}

///<summary>
///Where the Store tab reads from: the official registry, plus whatever this deployment was configured
///with.
///
///<strong>The configuration stays on the server</strong> and the browser learns only the URLs. A
///deployment that points at a private registry is telling the host something, not the page, and reading
///<c>Plugins__Sources</c> in the browser would mean shipping the whole configuration section to it.
///
///<strong>The official source is first, and first wins.</strong> Merging is by id and the earlier
///source keeps the entry (<see cref="PluginStore.Merge"/>), so a mirror listing <c>treeality.*</c> or
///re-listing an official id cannot displace the official entry by being configured second.
///</summary>
public static class PluginStoreSources
{
    ///<summary>
    ///The official registry's index. <strong>Not the GitHub API</strong> — sixty unauthenticated calls
    ///an hour is a limit the page would hit on its first busy afternoon, so the registry publishes a
    ///static index to Pages and the app reads a file.
    ///</summary>
    public const string Official = "https://eecsb.github.io/treeality-plugins/index.json";

    ///<summary>Where the host answers the configured extras.</summary>
    public const string Path = "api/plugins/sources";

    ///<summary>The configuration section a deployment adds sources under.</summary>
    public const string ConfigurationKey = "Plugins:Sources";

    ///<summary>
    ///The official source and the host's, in that order, with duplicates and blanks dropped. The
    ///official one is not repeated if a deployment also configured it.
    ///</summary>
    public static IReadOnlyList<string> All(IEnumerable<string> configured)
    {
        var sources = new List<string> { Official };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Official };

        foreach (var source in configured ?? [])
        {
            var url = (source ?? "").Trim();

            if (url.Length == 0)
                continue;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                continue;

            if (seen.Add(url))
                sources.Add(url);
        }

        return sources;
    }

    ///<summary>
    ///The sources this instance uses, asked of the host. A host that will not answer is not a failure:
    ///the official source stands on its own, and the panel says nothing about a configuration nobody
    ///set.
    ///</summary>
    public static async Task<IReadOnlyList<string>> LoadAsync(HttpClient http)
    {
        try
        {
            var answer = await http.GetFromJsonAsync<StoreSourcesAnswer>(Path);

            return All(answer?.Sources);
        }
        catch
        {
            return All(null);
        }
    }
}
