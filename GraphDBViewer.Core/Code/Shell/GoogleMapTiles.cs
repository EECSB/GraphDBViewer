using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Google's Map Tiles API as the earth of the Map and the Globe (2026-11-07, the user's go on what was written up on
///2026-11-07): not a URL template, as every other tile server here is, but a session made with the person's key for one
///kind of map, tiles asked for under that session, and a copyright line that follows what is on screen, shown beside
///Google's logo. Read from Google's pages that day: the 2D tiles overview, session tokens, policies, and usage and billing.
///Its endpoints answer a page on another site (CORS), checked that day with requests that carried no key.
///</summary>
public static class GoogleMapTiles
{
    ///<summary>Where Google's tiles, sessions and viewports are asked for.</summary>
    public const string Host = "https://tile.googleapis.com";

    ///<summary>The kinds of map Google's 2D tiles come in, as its createSession names them, with what each is called here.</summary>
    public static readonly IReadOnlyList<(string Type, string Name)> MapTypes =
    [
        ("roadmap", "Roadmap"),
        ("satellite", "Satellite"),
        ("terrain", "Terrain")
    ];

    ///<summary>The kind a new choice of Google starts at.</summary>
    public const string DefaultMapType = "roadmap";

    ///<summary>
    ///How long before it runs out a session is made again. Google says a session lasts two weeks "but this might change", and
    ///says nothing of renewing one, so a session is made again a day ahead, which no view held open meets in the middle.
    ///</summary>
    public static readonly TimeSpan RenewAhead = TimeSpan.FromDays(1);

    public static bool IsMapType(string type)
    {
        return MapTypes.Any(t => t.Type == type);
    }

    ///<summary>What a kind of map is called here, or the kind itself when it is none of Google's.</summary>
    public static string NameOf(string type)
    {
        foreach (var (kind, name) in MapTypes)
            if (kind == type)
                return name;

        return type ?? "";
    }

    ///<summary>
    ///The language the map's labels are written in and the region whose borders it draws, which a session has to name: the
    ///page's own culture, as the browser gives it, and en-US and US where there is none.
    ///</summary>
    public static (string Language, string Region) Locale(CultureInfo culture)
    {
        var name = culture?.Name ?? "";

        if (name.Length == 0)
            return ("en-US", "US");

        var region = "US";

        try
        {
            var found = new RegionInfo(name).TwoLetterISORegionName;

            if (found.Length == 2 && found.All(char.IsAsciiLetterUpper))
                region = found;
        }
        catch (ArgumentException) { }

        return (name, region);
    }

    ///<summary>What a session was made for: the kind of map, the language and the region, all of which it is bound to.</summary>
    public static string SessionFor(string mapType, string language, string region)
    {
        return $"{mapType} {language} {region}";
    }

    ///<summary>
    ///Whether a setting wants a session made before it is drawn: it has none, or one made for another kind of map, language or
    ///region, or one within <see cref="RenewAhead"/> of running out.
    ///</summary>
    public static bool NeedsSession(MapTileSettings settings, string language, string region, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(settings.Session))
            return true;

        if (settings.SessionFor != SessionFor(settings.MapType, language, region))
            return true;

        return DateTimeOffset.FromUnixTimeSeconds(settings.SessionExpiry) - now < RenewAhead;
    }

    ///<summary>What a session is asked for with: the kind of map, the language and the region, and for terrain the roadmap layer Google requires with it.</summary>
    public static string SessionBody(string mapType, string language, string region)
    {
        var body = new JsonObject
        {
            ["mapType"] = mapType,
            ["language"] = language,
            ["region"] = region
        };

        if (mapType == "terrain")
            body["layerTypes"] = new JsonArray("layerRoadmap");

        return body.ToJsonString();
    }

    ///<summary>
    ///The tiles under a session, as the views take a template: the session written in, and the key left for them to put in,
    ///as they put in any server's.
    ///</summary>
    public static string TilesTemplate(string session)
    {
        return $"{Host}/v1/2dtiles/{{z}}/{{x}}/{{y}}?session={Uri.EscapeDataString(session ?? "")}&key={{key}}";
    }

    ///<summary>
    ///Where the copyright for a view is asked: Google's viewport, with the session and the key, the zoom the tiles are drawn at,
    ///and the bounds on screen. Latitudes are held inside the tiles' square and longitudes just inside the world, Google
    ///taking neither pole nor ±180; a view over the antimeridian keeps its west east of its east, as Google's pages allow.
    ///</summary>
    public static string ViewportUrl(string session, string key, GoogleTileView view)
    {
        var zoom = Math.Clamp(view.Zoom, 0, 22);
        var bounds = $"north={Latitude(view.North)}&south={Latitude(view.South)}&east={Longitude(view.East)}&west={Longitude(view.West)}";

        return $"{Host}/tile/v1/viewport?session={Uri.EscapeDataString(session ?? "")}&key={Uri.EscapeDataString(key ?? "")}&zoom={zoom}&{bounds}";
    }

    private const double MaxLatitude = 85.0511;
    private const double MaxLongitude = 179.9999;

    private static string Latitude(double degrees)
    {
        return Math.Clamp(degrees, -MaxLatitude, MaxLatitude).ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static string Longitude(double degrees)
    {
        return Math.Clamp(degrees, -MaxLongitude, MaxLongitude).ToString("0.#####", CultureInfo.InvariantCulture);
    }

    ///<summary>
    ///The copyright for what is on screen, as Google's viewport answers it, which its policies ask to be shown with its tiles
    ///(beside its logo, the corner's credit); or why there is none.
    ///</summary>
    public static async Task<(string Copyright, string Problem)> ViewportAsync(
        HttpClient http,
        string session,
        string key,
        GoogleTileView view,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ViewportUrl(session, key, view));

        var (body, problem) = await SendAsync(http, request, "the map's copyright", cancellationToken);

        if (problem != null)
            return (null, problem);

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("copyright", out var copyright)
                && copyright.ValueKind == JsonValueKind.String)
                return (copyright.GetString(), null);
        }
        catch (JsonException) { }

        return (null, "Google answered with no copyright for the map.");
    }

    ///<summary>Makes a session for a kind of map with the key: the session, or why there is none, in a sentence.</summary>
    public static async Task<(GoogleMapSession Session, string Problem)> CreateSessionAsync(
        HttpClient http,
        string key,
        string mapType,
        string language,
        string region,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Host}/v1/createSession?key={Uri.EscapeDataString(key ?? "")}")
        {
            Content = new StringContent(SessionBody(mapType, language, region), Encoding.UTF8, "application/json")
        };

        var (body, problem) = await SendAsync(http, request, "a map session", cancellationToken);

        if (problem != null)
            return (null, problem);

        try
        {
            using var document = JsonDocument.Parse(body);

            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("session", out var session)
                && session.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(session.GetString())
                && TryExpiry(root, out var expiry))
                return (new GoogleMapSession(session.GetString(), expiry, SessionFor(mapType, language, region)), null);
        }
        catch (JsonException) { }

        return (null, "Google answered with no map session in its answer.");
    }

    //When the session runs out, in seconds since 1970: a string in Google's answer, read as a number too.
    private static bool TryExpiry(JsonElement root, out long expiry)
    {
        expiry = 0;

        if (!root.TryGetProperty("expiry", out var value))
            return false;

        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry);
        else if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out expiry);

        return false;
    }

    //A request to Google: its answer's body, or why there is none, naming what was asked for.
    private static async Task<(string Body, string Problem)> SendAsync(HttpClient http, HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException failed)
        {
            return (null, $"Google could not be reached for {what}: {failed.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"Google did not answer when asked for {what}.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return (body, null);

            return (null, $"Google refused {what}: {ErrorOf(body, response)}");
        }
    }

    ///<summary>What Google said was wrong, its error's message, or the status when it said nothing readable.</summary>
    public static string ErrorOf(string body, HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? "");

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
                return message.GetString();
        }
        catch (JsonException) { }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }
}

///<summary>A session Google made: its token, when it runs out (seconds since 1970), and what it was made for (<see cref="GoogleMapTiles.SessionFor"/>).</summary>
public sealed record GoogleMapSession(string Token, long Expiry, string For);

///<summary>What a view shows of Google's tiles: the zoom they are drawn at, and the bounds on screen in degrees.</summary>
public sealed record GoogleTileView(int Zoom, double West, double East, double North, double South);
