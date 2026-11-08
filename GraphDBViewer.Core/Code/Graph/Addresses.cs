using System.Globalization;

namespace GraphDBViewerWeb.Code;

///<summary>
///An address, the same everywhere in the app (the user's word, 2026-09-30: "it's a location after all"):
///the text as it is written, and where it is on the earth as a latitude and a longitude in decimal
///degrees, under the names the Map and the Globe read by convention (<see cref="GeoCoordinates"/>), so a
///vertex with its coordinates filled in is placed without anybody saying how. The coordinates are empty
///until somebody finds them from the text with <c>AddressField</c>'s button, or types them. Nothing looks
///an address up on its own: a geocoder is somebody else's service (<see cref="Geocoding"/>).
///</summary>
public static class Addresses
{
    public const string Text = "address";
    public const string Latitude = "latitude";
    public const string Longitude = "longitude";

    ///<summary>The words to look up for an address: its lines as one line, or null for an empty one.</summary>
    public static string QueryFor(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var lines = address.Split('\n')
            .Select(line => line.Trim().TrimEnd(',').Trim())
            .Where(line => line.Length > 0);

        return string.Join(", ", lines);
    }

    ///<summary>
    ///What is wrong with a latitude and a longitude, or null when nothing is. Each may be empty, which is
    ///an address nobody has placed yet; otherwise it is a number of degrees on the earth, read
    ///culture-invariantly, as the graph stores it: "46,05" is not 46.05 anywhere here.
    ///</summary>
    public static string Refusal(string latitude, string longitude)
    {
        if (!Fits(latitude, 90))
            return "The latitude is a number of degrees between -90 and 90.";

        if (!Fits(longitude, 180))
            return "The longitude is a number of degrees between -180 and 180.";

        return null;
    }

    private static bool Fits(string value, double most)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) || !double.IsFinite(degrees))
            return false;

        return degrees >= -most && degrees <= most;
    }
}
