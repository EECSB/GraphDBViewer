using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>Who wrote a plugin, as its entry claims.</summary>
public sealed class StoreAuthor
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }
}

///<summary>
///One released version of a plugin. <see cref="Sha256"/> is what the installer is told to expect, so
///the bytes it fetches are the bytes the registry reviewed.
///</summary>
public sealed class StoreVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("hostApiVersion")]
    public string HostApiVersion { get; set; }

    [JsonPropertyName("manifest")]
    public string Manifest { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; }

    [JsonPropertyName("released")]
    public string Released { get; set; }
}

///<summary>An entry taken down, with the reason. Never an action — a notice (open question 4).</summary>
public sealed class StoreRemoval
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }
}

///<summary>One plugin in the registry.</summary>
public sealed class StoreEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("author")]
    public StoreAuthor Author { get; set; }

    ///<summary>The identity marker for anything installed from this entry (ADR 0012, as the store reshapes it).</summary>
    [JsonPropertyName("repository")]
    public string Repository { get; set; }

    [JsonPropertyName("license")]
    public string License { get; set; }

    ///<summary><c>official</c> or <c>community</c>. <strong>The entry's own claim</strong>, shown as such: a source is data, and the app trusts one no more than another.</summary>
    [JsonPropertyName("trust")]
    public string Trust { get; set; }

    ///<summary>When the maintainer merged the entry or its newest version. Shown for community entries.</summary>
    [JsonPropertyName("reviewed")]
    public string Reviewed { get; set; }

    ///<summary>Relative to the index's own URL, so a mirror that copies the tree whole keeps working.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; }

    [JsonPropertyName("readme")]
    public string Readme { get; set; }

    [JsonPropertyName("versions")]
    public List<StoreVersion> Versions { get; set; } = new();

    ///<summary>Which source this entry was read from. Filled by the client, never by the file.</summary>
    [JsonIgnore]
    public string Source { get; set; }
}

///<summary>The registry's index, as the app reads it.</summary>
public sealed class StoreIndex
{
    [JsonPropertyName("generated")]
    public string Generated { get; set; }

    [JsonPropertyName("entries")]
    public List<StoreEntry> Entries { get; set; } = new();

    [JsonPropertyName("removed")]
    public List<StoreRemoval> Removed { get; set; } = new();
}

///<summary>A store index that would not parse, in the words the panel shows.</summary>
public sealed class StoreIndexException : Exception
{
    public StoreIndexException(string message) : base(message)
    {
    }
}

///<summary>
///The plugin store's decisions, with no HTTP and no UI (plugin-store criterion 20's first bullet).
///
///<strong>Tolerant about what it does not need and strict about what it does.</strong> An index written
///by a newer registry will carry fields this build has never heard of, and refusing it would make every
///registry change a flag day for every instance. What it will not tolerate is an entry with no id or no
///version, because an entry nobody can install is a card that lies.
///
///<strong>An entry with nothing compatible is SHOWN, not hidden</strong> (criterion 12), with the host
///API version it would need. Hiding it would mean an administrator never learns that upgrading unlocks
///something; a card that says "needs Treeality 2.0" teaches them.
///
///<strong>The trust badge is the entry's own claim.</strong> Nothing here promotes or demotes it: a
///source is data, and this app trusts one source no more than another (criterion 7). The panel says
///which source a card came from for exactly that reason.
///</summary>
public static class PluginStore
{
    private static readonly JsonSerializerOptions Reading = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    ///<summary>
    ///The index, or a refusal naming what was wrong. Unknown fields are ignored by construction —
    ///<c>System.Text.Json</c> skips what no property matches — and the required ones are checked here
    ///rather than left to surface as a blank card later.
    ///</summary>
    public static StoreIndex Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new StoreIndexException("The index was empty.");

        StoreIndex index;

        try
        {
            index = JsonSerializer.Deserialize<StoreIndex>(json, Reading);
        }
        catch (JsonException ex)
        {
            throw new StoreIndexException("The index is not valid JSON: " + ex.Message);
        }

        if (index == null)
            throw new StoreIndexException("The index was empty.");

        index.Entries ??= new();
        index.Removed ??= new();

        foreach (var entry in index.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new StoreIndexException("An entry in the index has no id.");

            entry.Versions ??= new();

            foreach (var version in entry.Versions)
            {
                if (string.IsNullOrWhiteSpace(version.Version))
                    throw new StoreIndexException($"A version of '{entry.Id}' has no version number.");

                if (string.IsNullOrWhiteSpace(version.Manifest))
                    throw new StoreIndexException($"Version {version.Version} of '{entry.Id}' names no manifest.");
            }
        }

        foreach (var removal in index.Removed)
        {
            if (string.IsNullOrWhiteSpace(removal.Id))
                throw new StoreIndexException("A removal in the index has no id.");
        }

        return index;
    }

    ///<summary>
    ///The newest version this host can run, or null when none of them is. Highest by
    ///<see cref="Compare"/> rather than by position: a registry may list versions in any order, and the
    ///one at the end is not necessarily the latest.
    ///</summary>
    public static StoreVersion NewestCompatible(StoreEntry entry, IReadOnlyCollection<string> supported)
    {
        StoreVersion newest = null;

        foreach (var version in entry?.Versions ?? [])
        {
            if (!Supports(supported, version.HostApiVersion))
                continue;

            if (newest == null || Compare(version.Version, newest.Version) > 0)
                newest = version;
        }

        return newest;
    }

    ///<summary>
    ///What every version of an entry needs, when none of them runs here — the sentence a card shows
    ///instead of a button, so an administrator learns that an upgrade would unlock it.
    ///</summary>
    public static string Needs(StoreEntry entry)
    {
        var wanted = new List<string>();

        foreach (var version in entry?.Versions ?? [])
        {
            var api = (version.HostApiVersion ?? "").Trim();

            if (api.Length > 0 && !wanted.Contains(api))
                wanted.Add(api);
        }

        if (wanted.Count == 0)
            return "This entry names no host API version, so nothing here can tell whether it would run.";

        return $"Needs host API {string.Join(" or ", wanted)}.";
    }

    ///<summary>
    ///The version an installed record could move to, or null. A version is an update when it is NEWER
    ///than what is installed — never merely different, because a registry that re-listed an older
    ///version would otherwise offer a downgrade as an update.
    ///</summary>
    public static StoreVersion UpdateFor(InstalledPluginRecord record, StoreEntry entry, IReadOnlyCollection<string> supported)
    {
        var installed = record?.Manifest?.Version;

        if (string.IsNullOrWhiteSpace(installed))
            return null;

        var newest = NewestCompatible(entry, supported);

        if (newest == null)
            return null;

        if (Compare(newest.Version, installed) <= 0)
            return null;

        return newest;
    }

    ///<summary>
    ///What re-fetching a URL-installed plugin's own manifest turned up, and what to say about it.
    ///
    ///<strong>A plugin installed from a URL has no entry and no reviewed hash</strong>, so there is
    ///nothing to compare the bytes against. The check is worth having anyway — most plugins will never
    ///be in a registry — and the sentence has to be honest about it: <strong>an update from a URL is
    ///only as trusted as that URL was</strong>, which is exactly as trusted as the original install.
    ///</summary>
    public static string UpdateFromUrl(InstalledPluginRecord record, PluginManifest fetched, IReadOnlyCollection<string> supported)
    {
        if (record?.Manifest == null)
            return "There is nothing here to update.";

        if (fetched == null)
            return "That URL did not answer with a manifest.";

        //Not an update at all: a different plugin at the same address. The page swaps by position, so
        //this would hand the old id's settings and grants to a stranger.
        if (!string.Equals(fetched.Id, record.Manifest.Id, StringComparison.Ordinal))
            return $@"That manifest is now ""{fetched.Id}"", not ""{record.Manifest.Id}"". An update has to be the same plugin.";

        if (Compare(fetched.Version, record.Manifest.Version) <= 0)
            return $"Already on {record.Manifest.Version}, and the URL still offers {fetched.Version}.";

        if (!Supports(supported, fetched.HostApiVersion))
            return $"{fetched.Version} is there, and it needs host API {fetched.HostApiVersion}, which this build does not run.";

        return null;
    }

    ///<summary>What the offer says once <see cref="UpdateFromUrl"/> has found one. Its own method so the page and a test read the same sentence.</summary>
    public static string UpdateOffer(InstalledPluginRecord record, PluginManifest fetched)
    {
        return $"{fetched.Version} is available, up from {record.Manifest.Version}. It comes from the URL this was installed from and carries no reviewed hash, so it is exactly as trusted as that URL.";
    }

    ///<summary>The removal for an id, or null. What crossing an index's take-downs with the installed list comes to.</summary>
    public static StoreRemoval Removal(StoreIndex index, string id)
    {
        foreach (var removal in index?.Removed ?? [])
        {
            if (string.Equals(removal.Id, id, StringComparison.OrdinalIgnoreCase))
                return removal;
        }

        return null;
    }

    ///<summary>
    ///Entries from several sources, merged by id. <strong>The first source in the list wins</strong>,
    ///and the entry remembers which source it came from — a mirror carrying the same id does not get to
    ///replace the official entry by being read second.
    ///</summary>
    public static IReadOnlyList<StoreEntry> Merge(IEnumerable<(string Source, StoreIndex Index)> read)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<StoreEntry>();

        foreach (var (source, index) in read ?? [])
        {
            foreach (var entry in index?.Entries ?? [])
            {
                if (!seen.Add(entry.Id))
                    continue;

                entry.Source = source;
                merged.Add(entry);
            }
        }

        return merged;
    }

    ///<summary>
    ///Whether a card matches what somebody typed: the name, the description, the author or the id. Case
    ///and surrounding space do not matter, and an empty search matches everything rather than nothing —
    ///a search box that empties to a blank page is a search box people stop using.
    ///</summary>
    public static bool Matches(StoreEntry entry, string search)
    {
        var text = (search ?? "").Trim();

        if (text.Length == 0)
            return true;

        foreach (var field in new[] { entry?.Name, entry?.Description, entry?.Author?.Name, entry?.Id })
        {
            if (field != null && field.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    ///<summary>A URL in the index resolved against the source it came from, so relative paths in a mirror stay inside the mirror.</summary>
    public static string Resolve(string source, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(source, UriKind.Absolute, out var root))
            return null;

        if (Uri.TryCreate(root, path, out var resolved))
            return resolved.ToString();

        return null;
    }

    //── Versions ───────────────────────────────────────────────────────────────────────────────

    private static bool Supports(IReadOnlyCollection<string> supported, string wanted)
    {
        var api = (wanted ?? "").Trim();

        if (api.Length == 0)
            return false;

        foreach (var one in supported ?? [])
        {
            if (string.Equals(one, api, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    ///<summary>
    ///Two version strings, compared part by part as numbers where both parts are numbers and as text
    ///where either is not. <strong>Not <see cref="string.CompareOrdinal(string, string)"/></strong>:
    ///that makes 1.10.0 older than 1.9.0, which would hide the update everybody is waiting for. A part
    ///nobody wrote counts as zero, so 1.2 and 1.2.0 are the same version.
    ///</summary>
    public static int Compare(string left, string right)
    {
        var one = Parts(left);
        var two = Parts(right);
        var length = Math.Max(one.Count, two.Count);

        for (var at = 0; at < length; at++)
        {
            var first = "0";
            var second = "0";

            if (at < one.Count)
                first = one[at];

            if (at < two.Count)
                second = two[at];

            var verdict = ComparePart(first, second);

            if (verdict != 0)
                return verdict;
        }

        return 0;
    }

    private static int ComparePart(string left, string right)
    {
        if (int.TryParse(left, out var first) && int.TryParse(right, out var second))
            return first.CompareTo(second);

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The dotted parts of a version, with any pre-release suffix left on the part it is attached to.</summary>
    private static List<string> Parts(string version)
    {
        var text = (version ?? "").Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text.Substring(1);

        return text.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
