using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Fetches a Wikipedia article's plain text for knowledge-graph generation, via the MediaWiki extracts
///API — the endpoint that serves article prose (SPARQL endpoints like Wikidata serve structured
///triples, not text). Browser-direct like everything else: origin=* makes the API CORS-open. The
///request building and response parsing are pure and unit-tested; the send is thin — the same split as
///the LLM providers.
///</summary>
public static class WikipediaSource
{
    public const string ApiBase = "https://en.wikipedia.org/w/api.php";

    ///<summary>The extracts query for an article title: plain text, redirects followed, CORS-open.</summary>
    public static string BuildExtractUrl(string title)
    {
        var source = ParseSource(title);

        return BuildExtractUrl(source.Title, source.ApiBase);
    }

    ///<summary>The same query against a particular Wikipedia's API, for a link to one that is not English.</summary>
    public static string BuildExtractUrl(string title, string apiBase)
    {
        return $"{apiBase}?action=query&prop=extracts&explaintext=1&redirects=1&format=json&origin=*&titles={Uri.EscapeDataString(title.Trim())}";
    }

    ///<summary>
    ///What was typed, read as an article title — which is what the API wants — and the Wikipedia to ask.
    ///
    ///A pasted URL is accepted as well, because somebody with the article open in front of them will
    ///reach for the address bar before they retype the heading. The title is taken out of the link, and
    ///so is the Wikipedia it came from: a German link asks the German Wikipedia rather than missing in
    ///English. Anything that is not a Wikipedia URL is left exactly as typed, so a title that happens to
    ///contain a slash still works.
    ///</summary>
    public static (string Title, string ApiBase) ParseSource(string input)
    {
        var text = (input ?? "").Trim();

        if (text.Length == 0)
            return (text, ApiBase);

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (text, ApiBase);

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return (text, ApiBase);

        if (!uri.Host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase))
            return (text, ApiBase);

        var apiBase = $"https://{uri.Host}/w/api.php";
        var title = TitleFromUrl(uri);

        if (string.IsNullOrWhiteSpace(title))
            return (text, apiBase);

        return (title, apiBase);
    }

    //Both shapes a Wikipedia link comes in: /wiki/Title, and /w/index.php?title=Title.
    private static string TitleFromUrl(Uri uri)
    {
        const string wikiPath = "/wiki/";

        if (uri.AbsolutePath.StartsWith(wikiPath, StringComparison.OrdinalIgnoreCase))
        {
            var slug = uri.AbsolutePath.Substring(wikiPath.Length);

            return Uri.UnescapeDataString(slug).Replace('_', ' ').Trim();
        }

        var query = uri.Query;
        const string titleParam = "title=";
        int at = query.IndexOf(titleParam, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
            return null;

        var value = query.Substring(at + titleParam.Length);
        int end = value.IndexOf('&');

        if (end >= 0)
            value = value.Substring(0, end);

        return Uri.UnescapeDataString(value).Replace('_', ' ').Trim();
    }

    ///<summary>The article's extract, or null when the page is missing or empty.</summary>
    public static string ParseExtract(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
                return null;

            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("missing", out _))
                    return null;

                if (page.Value.TryGetProperty("extract", out var extract))
                    return extract.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    ///<summary>Fetches and parses in one step, returning the text or a clear error. Never both.</summary>
    public static async Task<(string Text, string Error)> FetchExtractAsync(HttpClient http, string title, CancellationToken ct)
    {
        try
        {
            var source = ParseSource(title);
            var json = await http.GetStringAsync(BuildExtractUrl(source.Title, source.ApiBase), ct);
            var text = ParseExtract(json);

            if (string.IsNullOrWhiteSpace(text))
                return (null, $"No Wikipedia article found for \"{source.Title}\".");

            return (text.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Wikipedia fetch failed: {ex.Message}");
        }
    }
}
