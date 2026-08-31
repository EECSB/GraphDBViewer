using System.Globalization;

namespace GraphDBViewerWeb.Code;

///<summary>
///Small helpers for the per-label node colors: normalizing a CSS hex string, converting it to
///the ARGB form the .xlsx styles part expects, and picking a readable text color for a fill.
///Shared by the table view and the Excel export so they can't diverge.
///</summary>
public static class ColorUtil
{
    ///<summary>Returns the six-digit "RRGGBB" (upper-case) form of a CSS hex color, or null when it isn't one.</summary>
    public static string NormalizeHex(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
            return null;

        var hex = css.Trim().TrimStart('#');

        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";

        if (hex.Length != 6)
            return null;

        foreach (var ch in hex)
            if (!Uri.IsHexDigit(ch))
                return null;

        return hex.ToUpperInvariant();
    }

    ///<summary>The color as OpenXML ARGB ("FFRRGGBB"), or null when it isn't a valid hex color.</summary>
    public static string ToArgb(string css)
    {
        var hex = NormalizeHex(css);

        if (hex == null)
            return null;

        return "FF" + hex;
    }

    ///<summary>True when the fill is dark enough (perceived luminance) that white text reads better than black.</summary>
    public static bool IsDark(string css)
    {
        var hex = NormalizeHex(css);

        if (hex == null)
            return false;

        int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return (0.299 * r + 0.587 * g + 0.114 * b) < 140.0;
    }

    ///<summary>A readable text color ("#ffffff" or "#000000") to place over the given fill.</summary>
    public static string ContrastText(string css)
    {
        if (IsDark(css))
            return "#ffffff";

        return "#000000";
    }
}
