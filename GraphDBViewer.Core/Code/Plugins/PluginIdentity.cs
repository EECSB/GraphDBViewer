using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///Trust on first use, until signing exists (ADR 0012): the marker the first install of an id had,
///recorded so the next install of the same id can be compared against it. The marker is the install's
///<em>origin</em> — deliberately not the assembly hash, which identifies one build and would make
///every legitimate update read as an impostor. Defense in depth, not a boundary: what it stops is a
///plugin quietly acquiring the configuration and grants of one that was trusted earlier.
///</summary>
public sealed class PluginIdentity
{
    ///<summary>The origin (scheme://host[:port]) the id was first installed from.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("recordedUtc")]
    public DateTimeOffset RecordedUtc { get; set; }

    ///<summary>The comparable part of an install URL — its origin, or null for anything unparsable.</summary>
    public static string OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    ///<summary>An origin marker, from an install by URL.</summary>
    public const string OriginKind = "origin";

    ///<summary>A repository marker, from a store entry.</summary>
    public const string RepositoryKind = "repository";

    ///<summary>A marker for a plugin installed from an uploaded zip, where there is no origin to be.</summary>
    public const string UploadKind = "upload";

    ///<summary>What an upload's marker starts with. The rest is the assembly's hash — see <see cref="MarkerOf"/>.</summary>
    public const string UploadPrefix = "upload:";

    ///<summary>
    ///The marker for a record: <strong>its repository when the store gave it one, its own bytes when
    ///it came out of a zip, its origin otherwise</strong>. The plugin store surfaced what ADR 0012
    ///could not have known — an origin distinguishes nothing on a store where every release is on
    ///<c>github.com</c>, so every entry would match every other. The repository is what identifies a
    ///plugin there.
    ///
    ///<strong>An upload has neither.</strong> A zip carries no origin and no repository, so the only
    ///thing about it that can be compared with a later one is what it contained — which makes the
    ///marker the assembly's hash. Two uploads of the same build match, as they should; a different
    ///build prompts, as it must, because nothing about two files says they came from the same author.
    ///</summary>
    public static string MarkerOf(InstalledPluginRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record?.Repository))
            return record.Repository;

        if (PluginUploads.IsUploaded(record) && !string.IsNullOrWhiteSpace(record.Hash))
            return UploadPrefix + record.Hash;

        return OriginOf(record?.Source);
    }

    ///<summary>
    ///Which kind of marker this is, so the three shapes are never compared as if they were one. An
    ///upload says so in its prefix; a repository is anything else that is not an absolute URL with an
    ///authority.
    ///</summary>
    public static string KindOf(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
            return null;

        //Named rather than inferred. "upload:<hash>" parses as an absolute URI, but with an empty
        //authority — so the origin branch below does not claim it, and without this line every upload
        //would be filed as a repository and compared against real ones as if it were.
        if (marker.StartsWith(UploadPrefix, StringComparison.Ordinal))
            return UploadKind;

        if (Uri.TryCreate(marker, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Authority))
            return OriginKind;

        return RepositoryKind;
    }

    ///<summary>
    ///How a marker reads inside a sentence. An upload's is a hash, which nobody can be asked to
    ///compare in full, so it is named for what it is and shown short.
    ///</summary>
    public static string Describe(string marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
            return "nowhere recorded";

        if (KindOf(marker) != UploadKind)
            return marker;

        var hash = marker.Substring(UploadPrefix.Length);

        if (hash.Length > 12)
            hash = hash.Substring(0, 12);

        return $"a file uploaded here, whose assembly hashed to {hash}…";
    }

    ///<summary>
    ///What to do about an install of an id that is already marked, given the marker it arrives with.
    ///
    ///<list type="bullet">
    ///<item><strong>Agreeing markers of the same kind</strong> are the common case — updating — and
    ///proceed silently.</item>
    ///<item><strong>Differing markers of the same kind</strong> are what ADR 0012 exists for: two
    ///repositories that are not the same repository, or two origins that are not the same origin. The
    ///prompt, as an impostor would get.</item>
    ///<item><strong>Different KINDS are the pre-store case</strong>, and it always prompts (open
    ///question 5, overturning the draft's default). A plugin installed by URL before the store, being
    ///reinstalled from the store, is not a match and not a mismatch — nobody can tell from the two
    ///markers whether it is the same author. Re-marking it silently would let the store quietly adopt
    ///anything installed by URL, and only the administrator's yes should do that.</item>
    ///</list>
    ///</summary>
    public static bool Prompts(string stored, string arriving)
    {
        var was = (stored ?? "").Trim();
        var now = (arriving ?? "").Trim();

        //Nothing recorded is a first install, which prompts about nothing.
        if (was.Length == 0 || now.Length == 0)
            return false;

        if (KindOf(was) != KindOf(now))
            return true;

        return !string.Equals(was, now, StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>The sentence the prompt opens with, naming both markers — a prompt that says "this may not be the same plugin" and nothing else is one nobody can answer.</summary>
    public static string Says(string stored, string arriving)
    {
        if (!Prompts(stored, arriving))
            return null;

        //A file is involved, either side. Two zips prove nothing about each other whichever way they
        //differ: an author's own newer build and somebody else's package look identical from here,
        //because neither carries anything that says who made it. So this case is worded as a question
        //rather than as an accusation — the administrator who picked the file is the only one who knows.
        if (KindOf(stored) == UploadKind || KindOf(arriving) == UploadKind)
            return $"This id was installed from {Describe(stored)}, and this copy comes from {Describe(arriving)}. A file carries nothing that says who made it, so this is being asked rather than assumed.";

        if (KindOf(stored) != KindOf(arriving))
            return $@"This id was installed from {stored} before there was a store, and this copy comes from {arriving}. Nothing can tell from those two whether it is the same plugin, so it is being asked rather than assumed.";

        return $@"This id was first installed from {stored}, and this copy comes from {arriving}. That is what an impostor taking over a trusted id would look like.";
    }
}

///<summary>
///What a plugin id left behind, counted by the host (ADR 0011): the prompt that asks "delete its
///data?" must name a quantity, because consenting to an amount you cannot see is not consent. The
///graph half is absent until an instance graph exists to look in.
///</summary>
///<summary>
///One group of graph labels and who the audit says owns them. An <c>Orphaned</c> group is the point of
///the report; every other group is there so a clean answer can be told apart from an unread one.
///</summary>
public sealed class GraphLabelGroup
{
    ///<summary>The plugin id, or a phrase for the groups that are not a plugin's.</summary>
    public string Namespace { get; set; }

    ///<summary>"Platform", "Bundled", "Installed", "Orphaned" or "Yours".</summary>
    public string Owner { get; set; }

    public List<string> Labels { get; set; } = new();

    public long Vertices { get; set; }

    ///<summary>Edges touching the group's labels. Counted for orphans only — nothing else is being decided about.</summary>
    public long Edges { get; set; }

    ///<summary>A few keys from under the namespace, so somebody can look before deciding. Orphans only.</summary>
    public List<string> Sample { get; set; } = new();
}

///<summary>
///What the whole instance graph holds, by owner: the audit that does not need a plugin id, because it
///exists to find what nobody remembers installing (ledger item 20).
///</summary>
public sealed class GraphOrphanReport
{
    ///<summary>Where the host answers this: <c>api/plugins/orphans</c>. No id — that is the point of it.</summary>
    public const string Path = "api/plugins/orphans";

    ///<summary>Whether an instance graph is designated. Without one there is nothing to audit and the groups are empty.</summary>
    public bool InstanceGraphConfigured { get; set; }

    ///<summary>Why nothing could be read, when that is the answer.</summary>
    public string Error { get; set; }

    public List<GraphLabelGroup> Groups { get; set; } = new();
}

public sealed class PluginLeftovers
{
    ///<summary>Where the host answers this, for one plugin id: <c>api/plugins/leftovers?id=…</c>.</summary>
    public const string Path = "api/plugins/leftovers";

    ///<summary>Where the graph half is destroyed, for one plugin id (ADR 0011, question 2): <c>POST api/plugins/leftovers/purge?id=…</c>.</summary>
    public const string PurgePath = "api/plugins/leftovers/purge";

    ///<summary>Whether a settings blob is stored, and how big it is.</summary>
    public bool Configuration { get; set; }

    public int ConfigurationBytes { get; set; }

    ///<summary>How many role claims carry permissions under the plugin's id — grants an administrator made.</summary>
    public int PermissionGrants { get; set; }

    ///<summary>Whether an identity marker from an earlier install exists.</summary>
    public bool Identity { get; set; }

    ///<summary>Whether an instance graph is designated. Without one the two counts below are zero and mean nothing.</summary>
    public bool InstanceGraphConfigured { get; set; }
    ///<summary>
    ///What the plugin left in the graph, best-effort: vertices under the labels it owns (a bundled plugin's
    ///vocabulary) or under its id prefix (a third-party plugin, ADR 0009), and the edges touching them.
    ///Counted for the prompt. Removing it is the purge beside this (ADR 0011, question 2, built
    ///2026-09-13), which refuses without a backup that vouches and leaves any label another installed
    ///plugin owns too.
    ///</summary>
    public int GraphVertices { get; set; }
    public int GraphEdges { get; set; }

    ///<summary>
    ///The containers the plugin asked the Provisioner for, by name (ADR 0011 §3): they stop with the
    ///plugin and start with it again — stopped, not removed, their volumes and data in place. Empty
    ///where no Provisioner runs, which is also the truth: nothing could have been provisioned.
    ///</summary>
    public List<string> Containers { get; set; } = new();

    ///<summary>Why the list above cannot be trusted just now — a Provisioner that is configured but did not answer — or null.</summary>
    public string ContainersProblem { get; set; }

    ///<summary>
    ///Why no backup vouches for destroying this plugin's data, or null when one does (the backups spec,
    ///criteria 6 and 7; ADR 0011 question 2). <strong>The purge refuses on this</strong> since
    ///2026-09-13; before that it was shown here so an administrator saw the sentence they would be told
    ///then, which is the sentence they are told now.
    ///</summary>
    public string BackupRefusal { get; set; }

    ///<summary>
    ///The labels a purge would leave alone because another installed plugin owns them too, and empty
    ///when none are shared. <c>Item</c>, <c>Category</c> and <c>Parameter</c> between Parts and PIM are
    ///the whole of it today (ADR 0013), and the prompt names them rather than a purge quietly skipping
    ///them.
    ///</summary>
    public List<string> SharedLabels { get; set; } = new();

    ///<summary>When the last backup was taken, or null when there has never been one.</summary>
    public DateTimeOffset? BackupTakenAt { get; set; }

    ///<summary>
    ///Where the host stops or starts a plugin's containers: <c>POST api/plugins/containers?id=…&amp;action=stop|start</c>,
    ///called by the Plugins page right after the change of state the containers follow.
    ///</summary>
    public const string ContainersPath = "api/plugins/containers";
}

///<summary>What stopping or starting a plugin's containers did: the names acted on, and a sentence when the Provisioner could not do all of it.</summary>
public sealed class PluginContainersOutcome
{
    public List<string> Names { get; set; } = new();

    public string Error { get; set; }
}

///<summary>
///What a purge did: how many vertices it removed, and which labels it left standing because somebody
///else owns them too. <see cref="Error"/> is the refusal — no backup vouches, the graph could not be
///asked, or a label would not drop — and a purge that refused removed nothing.
///</summary>
public sealed class PluginPurgeResult
{
    public bool Ok { get; set; }

    public string Error { get; set; }

    ///<summary>Vertices removed. Their edges went with them, so they are not counted separately.</summary>
    public int Dropped { get; set; }

    ///<summary>Labels left alone because another installed plugin owns them too.</summary>
    public List<string> Kept { get; set; } = new();
}
