namespace GraphDBViewerWeb.Components;

///<summary>
///One entry of a <see cref="SavedPicker"/>: its key, the name it is shown by, and whether it is fixed, as the instance's
///own graph is, with nothing to rename, copy or delete.
///</summary>
public record SavedPickerItem(string Key, string Name, bool Fixed = false)
{
    ///<summary>The name Duplicate gives the copy of <paramref name="name"/>: "Dev graph (copy)", then "(copy 2)" and on.</summary>
    public static string CopyName(string name, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken);
        var wanted = $"{name} (copy)";

        if (!names.Contains(wanted))
            return wanted;

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} (copy {n})";

            if (!names.Contains(candidate))
                return candidate;
        }
    }

    ///<summary>
    ///<paramref name="entries"/> with the one under <paramref name="from"/> moved to <paramref name="to"/>, where it was in
    ///the order, rather than last as a remove and add would put it.
    ///</summary>
    public static Dictionary<string, T> Renamed<T>(Dictionary<string, T> entries, string from, string to)
    {
        var renamed = new Dictionary<string, T>();

        foreach (var entry in entries)
        {
            if (entry.Key == from)
                renamed[to] = entry.Value;
            else
                renamed[entry.Key] = entry.Value;
        }

        return renamed;
    }

    ///<summary><paramref name="entries"/> with <paramref name="value"/> under <paramref name="key"/> right after <paramref name="after"/>.</summary>
    public static Dictionary<string, T> InsertedAfter<T>(Dictionary<string, T> entries, string after, string key, T value)
    {
        var inserted = new Dictionary<string, T>();

        foreach (var entry in entries)
        {
            inserted[entry.Key] = entry.Value;

            if (entry.Key == after)
                inserted[key] = value;
        }

        if (!inserted.ContainsKey(key))
            inserted[key] = value;

        return inserted;
    }
}
