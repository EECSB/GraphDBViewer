using System;
using System.Globalization;

namespace GraphDBViewerWeb.Code;

///<summary>How the bell and the list say a notification's time and count, the same everywhere.</summary>
public static class NotificationsText
{
    ///<summary>How long ago: <em>just now</em>, minutes, hours, days, then the day itself.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var gone = now - at;

        if (gone < TimeSpan.FromMinutes(1))
            return "just now";

        if (gone < TimeSpan.FromHours(1))
            return ((int)gone.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";

        if (gone < TimeSpan.FromDays(1))
            return ((int)gone.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";

        if (gone < TimeSpan.FromDays(7))
        {
            var days = (int)gone.TotalDays;

            if (days == 1)
                return "yesterday";

            return days.ToString(CultureInfo.InvariantCulture) + " days ago";
        }

        return at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    ///<summary>The badge's count: the number, and 99+ above 99.</summary>
    public static string Badge(int unread)
    {
        if (unread > 99)
            return "99+";

        return unread.ToString(CultureInfo.InvariantCulture);
    }
}
