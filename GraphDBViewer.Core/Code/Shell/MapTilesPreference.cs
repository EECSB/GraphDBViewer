using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Where the Map and the Globe take their earth from (2026-11-05, the user): a tile server's URL template, set in Settings
///under Map tiles. Empty, the default, keeps the earth images that ship with the viewer, so the views still work
///air-gapped; with a URL, both views draw the earth from that server's tiles instead: the person's own (a tile server in a
///container), a free one such as OpenStreetMap's, or a commercial one with their key. A setting and never a default, as
///the map-view spec has said since 2026-09-06: OpenStreetMap's servers forbid an app that uses them as its backend,
///commercial ones want a key and their terms, and the map has to keep working with no network.
///
///Held by <see cref="ShellState.MapTiles"/> rather than registered on its own, so neither host has a service to add, and
///kept behind <see cref="IAppStorage"/>, where the AI keys are: the browser in the web edition, the workspace in Treeality.
///</summary>
public sealed class MapTilesPreference
{
    public const string StorageKey = "graphdbviewer:mapTiles";

    ///<summary>A tile server named by its URL template: one's own or a free one, and what every setting was until 2026-11-07.</summary>
    public const string TileUrl = "";

    ///<summary>Google's Map Tiles API (2026-11-07): a key and a kind of map rather than a URL (<see cref="GoogleMapTiles"/>).</summary>
    public const string Google = "google";

    ///<summary>The subdomains <c>{s}</c> takes in turn, as Leaflet's default has them.</summary>
    public const string Subdomains = "abc";

    //The placeholders a template may hold: the tile's zoom, column and row ({-y} counts rows from the south, as TMS
    //servers do), a subdomain, and where the key goes.
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal) { "z", "x", "y", "-y", "s", "key" };
    private static readonly Regex Placeholder = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);

    //Providers whose attribution is known, for a template that names none: their text and where it links.
    private static readonly (string Host, string Text, string Link)[] KnownAttributions =
    [
        ("tile.openstreetmap.org", "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright")
    ];

    ///<summary>
    ///Free tile servers that need no key, offered in the dialog and in the map's layers menu to choose with a click
    ///(2026-11-05, the user asked for some to use from the start, then for satellite and topographic ones among them). Each
    ///allows other sites to read its tiles, checked that day, and asks for the credit given. Offered, not set: the built-in
    ///earth stays the default, needing no network and telling no server where anyone looks. Grouped by kind, in this order.
    ///</summary>
    public static readonly IReadOnlyList<MapTilePreset> FreeServers =
    [
        new("OpenStreetMap", "Street", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors", "OpenStreetMap's own map. For light use: its servers ask that nobody leans on them hard."),
        new("CARTO Voyager", "Street", "https://{s}.basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png", "© OpenStreetMap contributors © CARTO", "A colored street map. Free for non-commercial use."),
        new("OpenTopoMap", "Topographic", "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors, SRTM · © OpenTopoMap (CC-BY-SA)", "A topographic map, with relief and contour lines, from OpenStreetMap's data."),
        new("Esri World Imagery", "Satellite", "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", "Tiles © Esri — Source: Esri, Maxar, Earthstar Geographics, and the GIS User Community", "Aerial and satellite photographs, sharp down to streets. Esri's terms ask for an ArcGIS account for use beyond trying it out."),
        new("Sentinel-2 cloudless", "Satellite", "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2020_3857/default/g/{z}/{y}/{x}.jpg", "Sentinel-2 cloudless 2020 by EOX IT Services GmbH (contains modified Copernicus Sentinel data 2020) · CC BY-NC-SA 4.0", "The whole earth from the Sentinel-2 satellites without clouds, sharp to about a town. Free for non-commercial use."),
        new("CARTO Light", "Light", "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png", "© OpenStreetMap contributors © CARTO", "A pale, quiet map that lets the graph stand out. Free for non-commercial use."),
        new("CARTO Dark", "Dark", "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png", "© OpenStreetMap contributors © CARTO", "The same map in dark gray, for the dark theme. Free for non-commercial use.")
    ];

    ///<summary>The free server a setting draws from, or null for the built-in earth or a server of one's own.</summary>
    public static MapTilePreset FreeServerOf(MapTileSettings settings)
    {
        if (settings == null || !settings.IsOn)
            return null;

        return FreeServers.FirstOrDefault(p => p.Url == settings.Url);
    }

    private readonly IAppStorage _storage;
    private Task _loading;
    private MapTileSettings _settings = new();

    public MapTilesPreference(IAppStorage storage)
    {
        _storage = storage;
    }

    ///<summary>Raised after the setting changes, so a page showing the Map or the Globe draws its earth again.</summary>
    public event Func<Task> Changed;

    ///<summary>
    ///The setting, read once from where it is kept. Empty until a URL was saved. Everyone asking while the read is on its
    ///way waits for that one read, a save included: a save that went ahead of it was undone when the read came back with
    ///what had been kept before (the workspace's storage is a server, slow enough on the first read for a person to type
    ///a URL and press Save in the meantime).
    ///</summary>
    public async Task<MapTileSettings> GetAsync()
    {
        _loading ??= LoadAsync();
        await _loading;

        return _settings;
    }

    private async Task LoadAsync()
    {
        _settings = Read(await _storage.GetStringAsync(StorageKey));
    }

    ///<summary>
    ///Keeps a new setting, or says why it cannot: the problem with the URL or the key, or null once kept. An empty URL puts
    ///the built-in earth back and forgets the key with it. <paramref name="key"/> is what was typed in
    ///the key box, null when nothing was, which keeps the key there was while the URL has a {key} for it; but a URL that
    ///goes from https to plain http drops a kept key unless this save types one, so a key is never sent in the clear
    ///without being given again (the rule every connection here follows). Nor is Google's key sent to a server of one's
    ///own: going from Google to a URL drops it unless this save types one (2026-11-07).
    ///</summary>
    public async Task<string> SetAsync(string url, string key)
    {
        var current = await GetAsync();
        url = (url ?? "").Trim();

        MapTileSettings next;

        if (url.Length == 0)
            next = new MapTileSettings { KeepInMemory = current.KeepInMemory };
        else
        {
            //Nothing typed keeps the key there was, while the URL still has a place for it, does not drop to http, and the
            //key was not Google's.
            var keep = key;

            if (keep == null)
            {
                if (current.IsGoogle)
                    keep = "";
                else if (!url.Contains("{key}", StringComparison.Ordinal))
                    keep = "";
                else if (IsPlainHttp(url) && !IsPlainHttp(current.Url ?? ""))
                    keep = "";
                else
                    keep = current.Key ?? "";
            }

            //The built-in earth's switch stays as it was: a server edited while it is on is kept for when it is turned off.
            next = new MapTileSettings { Provider = TileUrl, Url = url, Key = keep.Trim(), BuiltIn = current.BuiltIn, KeepInMemory = current.KeepInMemory };

            var problem = Problem(next);

            if (problem != null)
                return problem;
        }

        if (next.Provider == current.Provider && next.Url == current.Url && next.Key == current.Key && next.BuiltIn == current.BuiltIn)
            return null;

        await KeepAsync(next);

        return null;
    }

    ///<summary>
    ///Keeps Google's Map Tiles API as the earth (2026-11-07): its kind of map, and the key typed, null when nothing was. Nothing
    ///typed keeps the key Google had, but never one typed for a server of one's own, which is not sent to Google. A session
    ///made for the same key is kept with it; one made for another kind of map is made again when drawn
    ///(<see cref="GoogleMapTiles"/>). The built-in earth's switch stays as it was, as a URL's save leaves it.
    ///</summary>
    public async Task<string> SetGoogleAsync(string mapType, string key)
    {
        var current = await GetAsync();
        var keep = key;

        if (keep == null)
        {
            if (current.IsGoogle)
                keep = current.Key ?? "";
            else
                keep = "";
        }

        var next = new MapTileSettings
        {
            Provider = Google,
            MapType = mapType ?? "",
            Key = keep.Trim(),
            BuiltIn = current.BuiltIn,
            KeepInMemory = current.KeepInMemory
        };

        if (current.IsGoogle && current.Key == next.Key)
        {
            next.Session = current.Session;
            next.SessionExpiry = current.SessionExpiry;
            next.SessionFor = current.SessionFor;
        }

        var problem = Problem(next);

        if (problem != null)
            return problem;

        if (current.IsGoogle && next.MapType == current.MapType && next.Key == current.Key)
            return null;

        await KeepAsync(next);

        return null;
    }

    ///<summary>
    ///Turns the built-in earth on or off while a server stays kept (2026-11-05, the user's switch at the dialog's top), so
    ///going back to the server needs nothing typed again. With no server kept there is nothing to turn it off for.
    ///</summary>
    public async Task SetBuiltInAsync(bool builtIn)
    {
        var current = await GetAsync();

        if (!current.HasServer || current.BuiltIn == builtIn)
            return;

        var next = current.Copy();

        next.BuiltIn = builtIn;

        await KeepAsync(next);
    }

    ///<summary>How long a refused session is not asked for again with the same key, kind of map, language and region.</summary>
    public static readonly TimeSpan RetryRefusedAfter = TimeSpan.FromMinutes(1);

    private Task<(MapTileSettings Settings, string Problem)> _sessioning;
    private string _refusedFor;
    private string _refusal;
    private DateTimeOffset _refusedAt;

    ///<summary>
    ///The setting ready to draw from, and why it cannot be, or null (2026-11-07): for Google, with a session made, or made
    ///again when the one kept was for another kind of map, language or region or runs out within a day
    ///(<see cref="GoogleMapTiles.NeedsSession"/>). The session is kept with the setting, so a reload draws under it rather
    ///than asking for another. One request at a time however many ask, and a refusal is not asked again with the same key and
    ///kind for <see cref="RetryRefusedAfter"/>, so a wrong key is not put to Google at every draw.
    ///</summary>
    public async Task<(MapTileSettings Settings, string Problem)> ReadyAsync(HttpClient http, CultureInfo culture, DateTimeOffset now)
    {
        var settings = await GetAsync();

        if (!settings.IsOn || !settings.IsGoogle)
            return (settings, null);

        var (language, region) = GoogleMapTiles.Locale(culture);

        if (!GoogleMapTiles.NeedsSession(settings, language, region, now))
            return (settings, null);

        var asking = settings.Key + "\n" + GoogleMapTiles.SessionFor(settings.MapType, language, region);

        if (asking == _refusedFor && now - _refusedAt < RetryRefusedAfter)
            return (settings, _refusal);

        _sessioning ??= SessionAsync(http, settings, language, region, asking, now);

        var made = _sessioning;

        try
        {
            return await made;
        }
        finally
        {
            if (_sessioning == made)
                _sessioning = null;
        }
    }

    private async Task<(MapTileSettings Settings, string Problem)> SessionAsync(
        HttpClient http,
        MapTileSettings asked,
        string language,
        string region,
        string asking,
        DateTimeOffset now)
    {
        var (session, problem) = await GoogleMapTiles.CreateSessionAsync(http, asked.Key, asked.MapType, language, region, CancellationToken.None);

        if (problem != null)
        {
            _refusedFor = asking;
            _refusal = problem;
            _refusedAt = now;

            return (asked, problem);
        }

        //Kept on the setting it was made for only: one changed meanwhile is asked for again when it is drawn.
        var current = await GetAsync();

        if (!current.IsGoogle || current.Key != asked.Key || current.MapType != asked.MapType)
            return (current, null);

        var next = current.Copy();

        next.Session = session.Token;
        next.SessionExpiry = session.Expiry;
        next.SessionFor = session.For;

        //Kept without telling the views it changed: whoever asked draws with it.
        _settings = next;
        await _storage.SetStringAsync(StorageKey, JsonSerializer.Serialize(next));

        return (next, null);
    }

    ///<summary>
    ///Whether the Map and the Globe keep the tiles they have drawn, up to 600, so going back over the earth asks for none
    ///again (2026-11-07, the user: on by default, off for whoever wants them let go of). Off, they keep only the tiles on
    ///screen and let go of the rest at once. Kept whatever the earth is drawn from, so it holds for the next server too.
    ///</summary>
    public async Task SetKeepInMemoryAsync(bool keep)
    {
        var current = await GetAsync();

        if (current.KeepInMemory == keep)
            return;

        var next = current.Copy();

        next.KeepInMemory = keep;

        await KeepAsync(next);
    }

    private async Task KeepAsync(MapTileSettings next)
    {
        _settings = next;
        _refusedFor = null;

        if (next.HasServer || !next.KeepInMemory)
            await _storage.SetStringAsync(StorageKey, JsonSerializer.Serialize(next));
        else
            await _storage.RemoveAsync(StorageKey);

        var handler = Changed;

        if (handler == null)
            return;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();
    }

    ///<summary>A kept setting out of its text: anything unreadable, or a URL that would not be kept now, is no setting.</summary>
    public static MapTileSettings Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new MapTileSettings();

        MapTileSettings kept;

        try
        {
            kept = JsonSerializer.Deserialize<MapTileSettings>(text);
        }
        catch (JsonException)
        {
            return new MapTileSettings();
        }

        if (kept == null)
            return new MapTileSettings();

        kept.Provider ??= "";
        kept.Url ??= "";
        kept.Key ??= "";
        kept.MapType ??= "";
        kept.Session ??= "";
        kept.SessionFor ??= "";

        //No server to draw from, only the memory switch is kept.
        if (!kept.HasServer || Problem(kept) != null)
            return new MapTileSettings { KeepInMemory = kept.KeepInMemory };

        return kept;
    }

    ///<summary>What is wrong with a setting, in a sentence for the dialog, or null when it can be drawn from.</summary>
    public static string Problem(MapTileSettings settings)
    {
        if (settings.IsGoogle)
            return GoogleProblem(settings);
        else if (!string.IsNullOrEmpty(settings.Provider))
            return "That is not a kind of tile server this knows.";

        var url = settings.Url ?? "";
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Placeholder.Matches(url))
        {
            var name = m.Groups[1].Value;

            if (!Placeholders.Contains(name))
                return $"{{{name}}} is not a placeholder this understands. It takes {{z}}, {{x}}, {{y}} (or {{-y}}), {{s}} and {{key}}.";

            named.Add(name);
        }

        if (!named.Contains("z") || !named.Contains("x") || (!named.Contains("y") && !named.Contains("-y")))
            return "The URL needs {z}, {x} and {y} where the tile's zoom, column and row go, as in https://tile.example.org/{z}/{x}/{y}.png.";

        var sample = Placeholder.Replace(url, "0");

        if (!Uri.TryCreate(sample, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "The URL has to start with https:// (or http://) and name a server.";

        if (named.Contains("key") && string.IsNullOrEmpty(settings.Key))
            return "The URL has {key} in it: type the key under Key.";

        if (!named.Contains("key") && !string.IsNullOrEmpty(settings.Key))
            return "Put {key} in the URL where the provider wants the key, as in ?key={key} or ?apikey={key}.";

        return null;
    }

    //Google's tiles want a kind of map and the key the person's Google Cloud project gave them.
    private static string GoogleProblem(MapTileSettings settings)
    {
        if (!GoogleMapTiles.IsMapType(settings.MapType))
            return "Choose Google's kind of map: Roadmap, Satellite or Terrain.";

        if (string.IsNullOrEmpty(settings.Key))
            return "Google's map tiles need your API key: type it under Key.";

        return null;
    }

    ///<summary>
    ///What the layers menu calls the earth a setting draws from when it is none of the free servers: Google's map by its kind,
    ///or a server of one's own by its address.
    ///</summary>
    public static string Name(MapTileSettings settings)
    {
        if (settings.IsGoogle)
            return "Google Maps · " + GoogleMapTiles.NameOf(settings.MapType);

        return Host(settings.Url);
    }

    ///<summary>Whether a URL is plain http, which sends the tiles, and any key in it, unencrypted.</summary>
    public static bool IsPlainHttp(string url)
    {
        return (url ?? "").TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The server a template draws from, for saying where the tiles come from.</summary>
    public static string Host(string url)
    {
        var sample = Placeholder.Replace(url ?? "", "a");

        if (Uri.TryCreate(sample, UriKind.Absolute, out var uri))
            return uri.Host;

        return "";
    }

    ///<summary>
    ///What the views show in their corner for a setting: the credit a free server asks for, else the one a known provider
    ///asks for, else where the tiles come from, so the source is never unsaid. The link is the known provider's, if any.
    ///Nothing is typed for it: the dialog's Attribution box went, the credit being on the map already (2026-11-06, the user).
    ///</summary>
    public static (string Text, string Link) Attribution(MapTileSettings settings)
    {
        //Google's credit is its logo and the copyright its viewport answers, which the corner draws itself; said in words,
        //it is the words its policies allow where the logo does not fit.
        if (settings.IsGoogle)
            return ("Google Maps", null);

        var host = Host(settings.Url);
        string link = null;

        foreach (var known in KnownAttributions)
            if (string.Equals(host, known.Host, StringComparison.OrdinalIgnoreCase))
                link = known.Link;

        var preset = FreeServers.FirstOrDefault(p => p.Url == settings.Url);

        if (preset != null)
            return (preset.Attribution, link);

        foreach (var known in KnownAttributions)
            if (string.Equals(host, known.Host, StringComparison.OrdinalIgnoreCase))
                return (known.Text, known.Link);

        return ($"Tiles: {host}", null);
    }
}

///<summary>A free tile server the dialog offers: its name, kind of map, URL template, the credit it asks for, and a line about it.</summary>
public sealed record MapTilePreset(string Name, string Kind, string Url, string Attribution, string About);

///<summary>
///A tile server's URL template and the key that goes into its <c>{key}</c>; or, since 2026-11-07, Google's Map Tiles API: its
///kind of map, the key, and the session made for them.
///</summary>
public sealed class MapTileSettings
{
    ///<summary><see cref="MapTilesPreference.TileUrl"/> or <see cref="MapTilesPreference.Google"/>.</summary>
    public string Provider { get; set; } = "";

    public string Url { get; set; } = "";
    public string Key { get; set; } = "";

    ///<summary>Google's kind of map (<see cref="GoogleMapTiles.MapTypes"/>).</summary>
    public string MapType { get; set; } = "";

    ///<summary>
    ///Google's session: its token, when it runs out (seconds since 1970, as Google says it), and the kind of map, language and
    ///region it was made for. Kept with the setting so a reload draws under the same session rather than asking for another.
    ///</summary>
    public string Session { get; set; } = "";
    public long SessionExpiry { get; set; }
    public string SessionFor { get; set; } = "";

    ///<summary>The built-in earth chosen over the server kept here, which stays for when it is turned off again.</summary>
    public bool BuiltIn { get; set; }

    ///<summary>Whether the views keep the tiles they have drawn (<see cref="MapTilesPreference.SetKeepInMemoryAsync"/>); on unless turned off.</summary>
    public bool KeepInMemory { get; set; } = true;

    public bool IsGoogle => Provider == MapTilesPreference.Google;

    ///<summary>Whether a server is kept, drawn from or not: a URL, or Google with its key.</summary>
    public bool HasServer
    {
        get
        {
            if (IsGoogle)
                return !string.IsNullOrEmpty(Key);

            return !string.IsNullOrWhiteSpace(Url);
        }
    }

    ///<summary>Whether the earth comes from tiles: a server is kept and the built-in earth is not chosen over it.</summary>
    public bool IsOn => HasServer && !BuiltIn;

    public MapTileSettings Copy()
    {
        return (MapTileSettings)MemberwiseClone();
    }
}
