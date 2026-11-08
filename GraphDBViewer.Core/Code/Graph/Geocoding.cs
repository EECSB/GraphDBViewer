using System.Globalization;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>A vertex the Map cannot place, and the words that would be looked up for it.</summary>
public sealed record GeoCandidate(string Id, string IdType, string TypeLabel, string Name, string Query);

///<summary>
///What one lookup came back with: a place, nothing at all, or a reason it could not be asked. Nothing
///and a failure are kept apart on purpose — an address the world does not know is the graph's business
///to fix, and a geocoder that refused is not.
///</summary>
public sealed record GeoHit(double Latitude, double Longitude, string Found, string Error)
{
    public bool Located => Error == null && Found != null;

    ///<summary>
    ///How many degrees what was found spans, the larger of its height and its width, when the geocoder said: a country
    ///is wide, a house a speck. The Map and the Globe frame a place found by it (2026-11-06, the user).
    ///</summary>
    public double? Span { get; init; }

    public static GeoHit Nothing()
    {
        return new GeoHit(0, 0, null, null);
    }

    public static GeoHit Failed(string error)
    {
        return new GeoHit(0, 0, null, error);
    }

    public static GeoHit At(double latitude, double longitude, string found)
    {
        return new GeoHit(latitude, longitude, found, null);
    }
}

///<summary>
///Turning a place written in words into a place on the earth — the one thing in the Map that cannot be
///done here. The earth is a bundled image, coordinates are read from the graph itself, and everything
///else about these views works with the network unplugged; a geocoder is somebody else's service, and
///the names asked about leave the browser to reach it. So nothing here happens on its own: the caller
///shows what would be sent and where, and sends only on the word of the person whose graph it is.
///
///Nominatim is the default because it is OpenStreetMap's own, needs no account, and answers CORS. Its
///usage policy asks for no more than one request a second and for the caller to identify itself, which
///a browser does with its Referer. An endpoint of your own — a self-hosted Nominatim, or Photon, which
///answers GeoJSON instead and is read as such — is a field in the dialog.
///</summary>
public static class Geocoding
{
    public const string DefaultEndpoint = "https://nominatim.openstreetmap.org/search";

    ///<summary>Nominatim's policy: at most one request a second, which the caller waits out between lookups.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    ///<summary>
    ///How many lookups in one run is enough to say something about. OpenStreetMap's policy forbids bulk
    ///geocoding without defining it, so this is drawn where a run stops looking like somebody finding a
    ///few places and starts looking like a machine working through a list: about a minute of requests at
    ///the pace above. It warns and nothing more — whose graph it is is whose decision it is.
    ///</summary>
    public const int Bulk = 50;

    ///<summary>
    ///Whether the lookups would go to OpenStreetMap's own service, whose terms this is not. A geocoder
    ///somebody runs themselves is theirs to work as hard as they like.
    ///</summary>
    public static bool IsPublicNominatim(string endpoint)
    {
        return string.Equals(HostOf(endpoint), "nominatim.openstreetmap.org", StringComparison.OrdinalIgnoreCase);
    }

    //The properties that hold a place in words, in the order an address is written. The first one present
    //from each line is taken, and the lines are joined, because "Vienna, Austria" finds what "Vienna"
    //leaves ambiguous. "location" is here as well as in GeoCoordinates.PointKeys: a location that parsed
    //as a point is not a candidate at all, so one that reaches this is words.
    private static readonly string[][] AddressLines =
    [
        ["address", "street", "streetAddress", "postalAddress", "addressLine"],
        ["postcode", "postalCode", "zip", "zipCode"],
        ["city", "town", "village", "locality", "place", "location"],
        ["state", "province", "region", "county"],
        ["country", "countryName"]
    ];

    ///<summary>
    ///What to look up for a vertex: its address as the properties spell it, its name, or both. Null when
    ///it has neither, which is a vertex nothing can be asked about rather than a lookup worth a request.
    ///
    ///A street-level address stands alone — a building's name in front of it is another thing to look
    ///for, not a better description of the same one. Anything vaguer than a street does not: a vertex
    ///named Springfield in a country called USA is asked about as "Springfield, USA", because "USA" on
    ///its own finds a country, which is a confident wrong answer. A name already among the parts is not
    ///said twice.
    ///</summary>
    public static string QueryFor(IReadOnlyDictionary<string, string> properties, string name)
    {
        var parts = new List<string>();
        var hasStreet = false;

        if (properties != null)
            for (int line = 0; line < AddressLines.Length; line++)
            {
                if (!GeoCoordinates.TryFind(properties, AddressLines[line], out var value))
                    continue;

                if (line == 0)
                    hasStreet = true;

                parts.Add(value.Trim());
            }

        if (hasStreet)
            return string.Join(", ", parts);

        var trimmed = (name ?? "").Trim();

        if (trimmed.Length > 0 && !parts.Any(part => string.Equals(part, trimmed, StringComparison.OrdinalIgnoreCase)))
            parts.Insert(0, trimmed);

        if (parts.Count == 0)
            return null;

        return string.Join(", ", parts);
    }

    ///<summary>The host the lookups would go to, for the sentence that says so before any of them does.</summary>
    public static string HostOf(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return uri.Host;

        return endpoint;
    }

    ///<summary>
    ///One lookup. A Nominatim search answers a list, best first, each with a <c>lat</c> and a <c>lon</c> as
    ///text; Photon answers GeoJSON (see <see cref="Read"/>). The words travel in the query string because
    ///that is the only way either is asked — there is no form to post them in.
    ///</summary>
    public static async Task<GeoHit> LookupAsync(HttpClient http, string endpoint, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GeoHit.Nothing();

        //Nominatim is asked for its list by name. Photon answers GeoJSON whatever it is asked, and refuses a
        //parameter it does not know, format among them.
        var format = "format=jsonv2&";

        if (IsPhoton(endpoint))
            format = "";

        var url = $"{endpoint}?{format}limit=1&q={Uri.EscapeDataString(query)}";

        try
        {
            using var response = await http.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return GeoHit.Failed($"{(int)response.StatusCode} {response.ReasonPhrase}");

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return Read(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return GeoHit.Failed(ex.Message);
        }
    }

    ///<summary>
    ///Whether the endpoint is Photon's, whose search is at <c>/api</c> where Nominatim's is at <c>/search</c>.
    ///</summary>
    private static bool IsPhoton(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        return uri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The first place in a geocoder's answer, or nothing. Public so a test can hand it a body.</summary>
    public static GeoHit Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            //Nominatim answers a list. GeoJSON answers an object holding one, which is what Photon always
            //answers and Nominatim does when asked for it.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("features", out var features))
                root = features;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return GeoHit.Nothing();

            var first = root[0];

            if (first.ValueKind != JsonValueKind.Object)
                return GeoHit.Nothing();

            double latitude;
            double longitude;
            string found;
            double? span;

            //A GeoJSON feature keeps its place in its geometry, longitude first as GeoJSON writes every position,
            //and its words under its properties. Read for a lat and a lon as Nominatim's list is, every Photon
            //answer was no place (until 2026-10-03).
            if (first.TryGetProperty("geometry", out var geometry))
            {
                if (!TryPoint(geometry, out latitude, out longitude))
                    return GeoHit.Nothing();

                found = FeatureText(first);
                span = PhotonSpan(first);
            }
            else
            {
                if (!TryDegrees(first, "lat", out latitude) || !TryDegrees(first, "lon", out longitude))
                    return GeoHit.Nothing();

                found = Text(first, "display_name") ?? Text(first, "name");
                span = NominatimSpan(first);
            }

            if (!GeoCoordinates.IsOnEarth(latitude, longitude))
                return GeoHit.Nothing();

            return GeoHit.At(latitude, longitude, found ?? "") with { Span = span };
        }
        catch (JsonException)
        {
            return GeoHit.Failed("the answer was not JSON");
        }
    }

    //Nominatim's box round what it found: south, north, west and east, as text.
    private static double? NominatimSpan(JsonElement place)
    {
        if (!place.TryGetProperty("boundingbox", out var box) || box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4)
            return null;

        var sides = new double[4];

        for (int i = 0; i < 4; i++)
            if (!TryNumber(box[i], out sides[i]))
                return null;

        return SpanOf(sides[1] - sides[0], sides[3] - sides[2]);
    }

    //Photon's extent round what it found, when it has one: west, north, east and south, as numbers. A house has none.
    private static double? PhotonSpan(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;

        if (!properties.TryGetProperty("extent", out var extent) || extent.ValueKind != JsonValueKind.Array || extent.GetArrayLength() != 4)
            return null;

        var sides = new double[4];

        for (int i = 0; i < 4; i++)
            if (!TryNumber(extent[i], out sides[i]))
                return null;

        return SpanOf(sides[1] - sides[3], sides[2] - sides[0]);
    }

    //The larger of a box's height and width, or nothing for a box that is no box.
    private static double? SpanOf(double height, double width)
    {
        var span = Math.Max(Math.Abs(height), Math.Abs(width));

        if (double.IsNaN(span) || span > 360)
            return null;

        return span;
    }

    //A number, written as one or as text.
    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out value);
        else if (element.ValueKind == JsonValueKind.String)
            return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        return false;
    }

    //A GeoJSON point's latitude and longitude, from its coordinates, which put the longitude first.
    private static bool TryPoint(JsonElement geometry, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (geometry.ValueKind != JsonValueKind.Object || Text(geometry, "type") != "Point")
            return false;

        if (!geometry.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() < 2)
            return false;

        if (coordinates[0].ValueKind != JsonValueKind.Number || coordinates[1].ValueKind != JsonValueKind.Number)
            return false;

        longitude = coordinates[0].GetDouble();
        latitude = coordinates[1].GetDouble();

        return true;
    }

    //A house, which Photon gives no name, is its address, in the parts Photon gives it: each line its parts,
    //the lines in the order an address is written.
    private static readonly string[][] PhotonAddress =
    [
        ["street", "housenumber"],
        ["postcode", "city"],
        ["country"]
    ];

    //What a GeoJSON feature found, in words: Nominatim's display_name, Photon's name, or the address of a house.
    private static string FeatureText(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;

        var named = Text(properties, "display_name") ?? Text(properties, "name");

        if (named != null)
            return named;

        var lines = PhotonAddress
            .Select(line => string.Join(" ", line.Select(key => Text(properties, key)).Where(part => !string.IsNullOrWhiteSpace(part))))
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return null;

        return string.Join(", ", lines);
    }

    ///<summary>A coordinate as the viewer stores it: invariant text, so "48,2" is 48.2 nowhere.</summary>
    public static string Degrees(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool TryDegrees(JsonElement element, string name, out double value)
    {
        value = 0;

        if (!element.TryGetProperty(name, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();

            return true;
        }

        return GeoCoordinates.TryParseDegrees(property.GetString(), out value);
    }

    private static string Text(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            return property.GetString();

        return null;
    }
}
