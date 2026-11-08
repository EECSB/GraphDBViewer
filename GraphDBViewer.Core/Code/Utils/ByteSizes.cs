using System;
using System.Globalization;

namespace GraphDBViewerWeb.Code;

///<summary>
///A size in bytes as a person reads it, the same everywhere in the app (2026-10-03): bytes under a kilobyte, then KB,
///MB, GB and TB of 1024 each, to one decimal place where there is one, and in the invariant culture, so "1.5 MB" in
///every browser. Until then a dozen places wrote their own: some in the reader's culture ("1,5 MB"), some never past
///MB ("2048 MB"), one in whole kilobytes.
///</summary>
public static class ByteSizes
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    public static string Human(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        var size = bytes / 1024.0;
        var unit = 0;

        //Rounded first, so a size just short of the next unit reads as one of it: 1048575 bytes is "1 MB", not "1024 KB".
        while (unit < Units.Length - 1 && Math.Round(size, 1) >= 1024)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString("0.#", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
