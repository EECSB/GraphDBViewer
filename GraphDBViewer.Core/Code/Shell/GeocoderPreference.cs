using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Which geocoder finds a place from its words, everywhere the viewer does: Find a place on the Map and the Globe, Find
///coordinates, and the address fields' Find button. Set once in Map settings, beside where the earth comes from, and
///nowhere else (2026-11-06, the user: Find coordinates had its own box, which was not kept, so Find a place always went to
///OpenStreetMap). Empty, the default, is OpenStreetMap's own Nominatim, which needs no account; a Nominatim of one's own,
///or Photon, keeps the words off OpenStreetMap's servers and out of its policy.
///
///Held by <see cref="ShellState.Geocoder"/>, as <see cref="MapTilesPreference"/> is, and kept behind <see cref="IAppStorage"/>:
///the browser in the web edition, the workspace in Treeality.
///</summary>
public sealed class GeocoderPreference
{
    public const string StorageKey = "graphdbviewer:geocoder";

    ///<summary>Geocoders that need no key, offered in Map settings to choose with a click. OpenStreetMap's own is the default.</summary>
    public static readonly IReadOnlyList<GeocoderPreset> FreeServers =
    [
        new("OpenStreetMap", Geocoding.DefaultEndpoint, "OpenStreetMap's own Nominatim. For light use: one request a second, and no bulk lookups."),
        new("Photon", "https://photon.komoot.io/api", "Komoot's Photon, from OpenStreetMap's data. For light use as well.")
    ];

    private readonly IAppStorage _storage;
    private Task _loading;
    private string _endpoint = Geocoding.DefaultEndpoint;

    public GeocoderPreference(IAppStorage storage)
    {
        _storage = storage;
    }

    ///<summary>Raised after the setting changes, so a sentence saying where the words go says the new place.</summary>
    public event Func<Task> Changed;

    ///<summary>The geocoder's search URL, read once from where it is kept: OpenStreetMap's own until another was saved.</summary>
    public async Task<string> GetAsync()
    {
        _loading ??= LoadAsync();
        await _loading;

        return _endpoint;
    }

    private async Task LoadAsync()
    {
        _endpoint = Read(await _storage.GetStringAsync(StorageKey));
    }

    ///<summary>
    ///Keeps a geocoder, or says why it cannot: the problem with the URL, or null once kept. Empty puts OpenStreetMap's own back.
    ///</summary>
    public async Task<string> SetAsync(string endpoint)
    {
        var current = await GetAsync();
        var next = (endpoint ?? "").Trim();

        if (next.Length == 0)
            next = Geocoding.DefaultEndpoint;

        var problem = Problem(next);

        if (problem != null)
            return problem;

        if (next == current)
            return null;

        _endpoint = next;

        if (next == Geocoding.DefaultEndpoint)
            await _storage.RemoveAsync(StorageKey);
        else
            await _storage.SetStringAsync(StorageKey, next);

        var handler = Changed;

        if (handler == null)
            return null;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();

        return null;
    }

    ///<summary>A kept geocoder out of its text: nothing, or a URL that would not be kept now, is OpenStreetMap's own.</summary>
    public static string Read(string text)
    {
        var endpoint = (text ?? "").Trim();

        if (endpoint.Length == 0 || Problem(endpoint) != null)
            return Geocoding.DefaultEndpoint;

        return endpoint;
    }

    ///<summary>What is wrong with a geocoder's URL, in a sentence for Map settings, or null when it can be asked.</summary>
    public static string Problem(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "The geocoder's URL has to start with https:// (or http://) and name a server, as in https://nominatim.example.org/search.";

        if (!string.IsNullOrEmpty(uri.Query))
            return "Give the search's address without a ?query: the words to look up are added to it.";

        return null;
    }
}

///<summary>A geocoder Map settings offers: its name, search URL, and a line about it.</summary>
public sealed record GeocoderPreset(string Name, string Url, string About);
