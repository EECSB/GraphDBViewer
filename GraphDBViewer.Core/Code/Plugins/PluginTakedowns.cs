using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>One take-down an administrator has already been shown, kept so it can be repeated until the plugin is gone.</summary>
public sealed class SeenTakedown
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    ///<summary>The plugin's name as the installed record has it, so the reminder can say what it is rather than only its id.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }
}

///<summary>
///Take-downs an administrator has seen on the Plugins page, and the reminder the home page repeats.
///
///<strong>The instance never asks a registry unprompted</strong> (plugin-store open question 4): it
///learns of a take-down only when somebody opens the Store tab, and learning of one uninstalls nothing.
///That leaves a gap this closes — the person who saw the notice may not be the person who decides, and
///the notice was on a tab nobody has a reason to revisit. So what was seen is written down, and the home
///page repeats it.
///
///<strong>Only while the plugin is still installed.</strong> The list is crossed with the installed
///records on the server, so uninstalling the plugin ends the reminder without anybody having to clear
///anything — and a take-down for something never installed here is never shown at all.
///</summary>
public static class PluginTakedowns
{
    ///<summary>Where the host answers this: GET what is still relevant, PUT what the Store tab just saw.</summary>
    public const string Path = "api/plugins/takedowns";

    ///<summary>Where the seen list lives in instance state. Not reserved: it holds no credential, and a page reads it.</summary>
    public const string StateKey = "plugins:takedowns";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<SeenTakedown> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<SeenTakedown>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            //A blob nobody can read is a blob nobody wrote on purpose. An empty list loses a reminder;
            //a throw loses the page.
            return [];
        }
    }

    public static string Write(IEnumerable<SeenTakedown> seen)
    {
        return JsonSerializer.Serialize(seen ?? []);
    }

    ///<summary>
    ///What was already written down, plus what the Store tab has just seen — <strong>merged rather than
    ///replaced</strong>, because a tab that read one source must not erase a take-down learned from
    ///another. The newer reason wins for an id already there, since a maintainer may have said more.
    ///</summary>
    public static IReadOnlyList<SeenTakedown> Merge(IEnumerable<SeenTakedown> kept, IEnumerable<SeenTakedown> seen)
    {
        var merged = new Dictionary<string, SeenTakedown>(StringComparer.OrdinalIgnoreCase);

        foreach (var one in kept ?? [])
        {
            if (!string.IsNullOrWhiteSpace(one?.Id))
                merged[one.Id] = one;
        }

        foreach (var one in seen ?? [])
        {
            if (!string.IsNullOrWhiteSpace(one?.Id))
                merged[one.Id] = one;
        }

        return merged.Values.OrderBy(one => one.Id, StringComparer.Ordinal).ToList();
    }

    ///<summary>
    ///The reminders worth showing: those naming a plugin that is still installed.
    ///
    ///Crossing happens on the SERVER, where the installed list already is — so the home page shows what
    ///it is given rather than fetching a second thing to work it out, and an id uninstalled long ago
    ///simply stops appearing.
    ///</summary>
    public static IReadOnlyList<SeenTakedown> StillInstalled(IEnumerable<SeenTakedown> seen, IEnumerable<string> installedIds)
    {
        var installed = new HashSet<string>(
            (installedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        return (seen ?? [])
            .Where(one => one != null && !string.IsNullOrWhiteSpace(one.Id) && installed.Contains(one.Id))
            .OrderBy(one => one.Id, StringComparer.Ordinal)
            .ToList();
    }

    ///<summary>What the reminder says. One sentence, naming the plugin and the reason, because a banner nobody can act on is a banner people learn to close.</summary>
    public static string Says(SeenTakedown taken)
    {
        var name = taken?.Name;

        if (string.IsNullOrWhiteSpace(name))
            name = taken?.Id;

        var said = $"{name} was taken down from the plugin registry";

        if (!string.IsNullOrWhiteSpace(taken?.Date))
            said += $" on {taken.Date}";

        if (!string.IsNullOrWhiteSpace(taken?.Reason))
            said += $": {taken.Reason}";

        return said + " It is still installed here, and nothing has been changed.";
    }
}
