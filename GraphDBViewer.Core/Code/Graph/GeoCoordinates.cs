using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///Finds a vertex's place on the earth among its properties, the way every graph tool with a map does:
///by convention, from the names people actually store coordinates under. Nothing is configured — a
///graph whose vertices carry a latitude and a longitude under any of the usual names is on the map
///without being told how, which is what makes an imported air-routes graph just work.
///
///Three shapes are read, in this order: a pair of properties (<c>lat</c> + <c>lon</c> and their
///spellings, the viewer's own <c>gdbvLat</c> / <c>gdbvLon</c> first); one property holding both —
///"48.2, 16.4", a GeoJSON point, WKT <c>POINT(lon lat)</c>, or a Neo4j point's text; and nothing, which
///is most vertices. Every number is read culture-invariantly: "48,2" is not 48.2 anywhere here.
///</summary>
public static class GeoCoordinates
{
    ///<summary>Latitude, under the names it is usually stored under. The viewer's reserved key first.</summary>
    public static readonly string[] LatitudeKeys = { GdbvKeys.Lat, "latitude", "lat", "geo_lat", "geolat", "geo_latitude" };

    ///<summary>Longitude, likewise — including the three-letter spellings that are each somebody's habit.</summary>
    public static readonly string[] LongitudeKeys = { GdbvKeys.Lon, "longitude", "lon", "lng", "long", "geo_lon", "geo_lng", "geolon", "geolng", "geo_longitude" };

    ///<summary>One property holding both coordinates, under the names such a property is usually given.</summary>
    public static readonly string[] PointKeys = { "location", "coordinates", "coordinate", "latlon", "latlng", "lat_lon", "lat_lng", "geo", "geolocation", "geopoint", "point", "position", "wkt" };

    //"48.2, 16.4" / "48.2;16.4" / "48.2 16.4" — two numbers, latitude first, the way people write them.
    private static readonly Regex PairText = new(@"^\s*(-?\d+(?:\.\d+)?)\s*[,;\s]\s*(-?\d+(?:\.\d+)?)\s*$", RegexOptions.Compiled);

    //WKT: POINT(lon lat), optionally with an SRID prefix — longitude first, as WKT has it.
    private static readonly Regex WktPoint = new(@"^\s*(?:SRID=\d+;\s*)?POINT\s*\(\s*(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s*\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    //A Neo4j point as its driver prints it: Point{srid=4326, x=16.4, y=48.2} — x is longitude, y latitude.
    private static readonly Regex Neo4jPoint = new(@"srid\s*=\s*(?:4326|4979)\b.*?\bx\s*=\s*(-?\d+(?:\.\d+)?).*?\by\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    ///<summary>
    ///The vertex's latitude and longitude in decimal degrees, when its properties carry them under a
    ///name this recognizes and the values are on the earth. False for the ordinary vertex, which has none.
    ///</summary>
    public static bool TryRead(IReadOnlyDictionary<string, string> properties, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (properties == null || properties.Count == 0)
            return false;

        if (TryFind(properties, LatitudeKeys, out var latitudeText)
            && TryFind(properties, LongitudeKeys, out var longitudeText)
            && TryParseDegrees(latitudeText, out var lat)
            && TryParseDegrees(longitudeText, out var lon)
            && IsOnEarth(lat, lon))
        {
            latitude = lat;
            longitude = lon;

            return true;
        }

        if (TryFind(properties, PointKeys, out var pointText) && TryParsePoint(pointText, out lat, out lon) && IsOnEarth(lat, lon))
        {
            latitude = lat;
            longitude = lon;

            return true;
        }

        return false;
    }

    ///<summary>Whether a pair of numbers is a place on the earth at all.</summary>
    public static bool IsOnEarth(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude))
            return false;

        return latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
    }

    ///<summary>
    ///One coordinate as text: a decimal number, optionally with a degree sign or a hemisphere letter
    ///("48.2°", "48.2 N", "16.4E" — S and W make it negative). Culture-invariant on purpose.
    ///</summary>
    public static bool TryParseDegrees(string text, out double degrees)
    {
        degrees = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim().TrimEnd('°').Trim();
        var sign = 1;

        if (value.Length > 1)
        {
            var last = char.ToUpperInvariant(value[^1]);

            if (last == 'S' || last == 'W')
            {
                sign = -1;
                value = value[..^1].Trim();
            }
            else if (last == 'N' || last == 'E')
                value = value[..^1].Trim();
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            return false;

        degrees = sign * parsed;

        return true;
    }

    ///<summary>
    ///One property holding both coordinates. The order depends on the notation, and each is read the
    ///way its own convention has it: a bare "a, b" is latitude first; GeoJSON, WKT and a Neo4j point
    ///are longitude first.
    ///</summary>
    public static bool TryParsePoint(string text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        var pair = PairText.Match(trimmed);

        if (pair.Success)
            return TryParseDegrees(pair.Groups[1].Value, out latitude) && TryParseDegrees(pair.Groups[2].Value, out longitude);

        var wkt = WktPoint.Match(trimmed);

        if (wkt.Success)
            return TryParseDegrees(wkt.Groups[2].Value, out latitude) && TryParseDegrees(wkt.Groups[1].Value, out longitude);

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return TryParseJsonPoint(trimmed, out latitude, out longitude);

        var neo4j = Neo4jPoint.Match(trimmed);

        if (neo4j.Success)
            return TryParseDegrees(neo4j.Groups[2].Value, out latitude) && TryParseDegrees(neo4j.Groups[1].Value, out longitude);

        return false;
    }

    //A GeoJSON point ({"type":"Point","coordinates":[lon,lat]}), a bare [lon,lat] array read the same
    //way, or an object naming its members ({"lat":..,"lng":..}). Anything else is not a point.
    private static bool TryParseJsonPoint(string text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return TryReadLonLatArray(root, out latitude, out longitude);

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("coordinates", out var coordinates))
                return TryReadLonLatArray(coordinates, out latitude, out longitude);

            var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in root.EnumerateObject())
            {
                if (member.Value.ValueKind == JsonValueKind.Number || member.Value.ValueKind == JsonValueKind.String)
                    members[member.Name] = member.Value.ToString();
            }

            return TryFind(members, LatitudeKeys, out var latText)
                && TryFind(members, LongitudeKeys, out var lonText)
                && TryParseDegrees(latText, out latitude)
                && TryParseDegrees(lonText, out longitude);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadLonLatArray(JsonElement array, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < 2)
            return false;

        var lon = array[0];
        var lat = array[1];

        if (lon.ValueKind != JsonValueKind.Number || lat.ValueKind != JsonValueKind.Number)
            return false;

        longitude = lon.GetDouble();
        latitude = lat.GetDouble();

        return true;
    }

    //The first of the candidate names present with a non-empty value, matched without regard to case
    //— "Latitude" and "LAT" are the same habit as "lat".
    private static bool TryFind(IReadOnlyDictionary<string, string> properties, string[] keys, out string value)
    {
        foreach (var key in keys)
        {
            foreach (var property in properties)
            {
                if (!string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(property.Value))
                    continue;

                value = property.Value;

                return true;
            }
        }

        value = null;

        return false;
    }
}
