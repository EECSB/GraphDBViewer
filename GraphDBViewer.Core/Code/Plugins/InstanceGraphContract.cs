namespace GraphDBViewerWeb.Code;

///<summary>
///The instance graph's wire shape (instance-graph spec, ADRs 0009/0010): the one graph plugins read
///and write, held as a <em>designated connection</em> — the same connection machinery the viewer
///already has, marked by role and scope rather than by type. It lives in instance state under a
///reserved key, and everything about it goes through its own endpoints: the connection carries the
///graph's credentials, so the generic instance-state surface refuses the key outright, configuration
///is gated on its own permission, and execution never hands the connection to a browser at all — the
///server dials, which is also what makes a container addressed by name reachable in the first place.
///</summary>
public static class InstanceGraphContract
{
    ///<summary>Where the connection lives in the instance store. Reserved: the generic
    ///<c>api/instance</c> surface refuses it, because the value holds credentials.</summary>
    public const string Key = "instance:graph";

    ///<summary>Whether one is configured, and roughly what it is — no secrets. Any admitted user.</summary>
    public const string StatusPath = "api/graph/instance/status";

    ///<summary>
    ///Runs a <em>reading</em> query against the instance graph, server-side. Gated on
    ///<c>instance.query</c> since 2026-09-08 — it reaches everything every plugin has stored, past the
    ///pages that usually shape it, including the person fields a separate permission exists to hide —
    ///and writes are refused through it whoever holds that permission.
    ///</summary>
    public const string ExecutePath = "api/graph/instance/execute";

    ///<summary>The stored connection itself. Gated on the configure permission — it holds the key.</summary>
    public const string ConfigPath = "api/graph/instance/config";

    ///<summary>Probes a candidate connection without storing it. Gated like config.</summary>
    public const string TestPath = "api/graph/instance/test";

    ///<summary>
    ///Save now (the instance-graph-backend research, question 5): writes a file-backed TinkerGraph to its
    ///own file on demand, so a crash loses "since the last save" rather than everything since the last
    ///start. Gated like config — it writes the server's disk. Answers <see cref="InstanceGraphSaveResult"/>.
    ///</summary>
    public const string SavePath = "api/graph/instance/save";

    ///<summary>
    ///The autosave interval, and what the last one this server ran did. Gated like config: it decides
    ///how often the server rewrites its own disk. GET answers <see cref="InstanceGraphAutosaveState"/>,
    ///PUT takes <see cref="InstanceGraphAutosaveSetting"/>.
    ///</summary>
    public const string AutosavePath = "api/graph/instance/autosave";

    ///<summary>
    ///Declare the schema (the JanusGraph recipe): property keys with their datatypes and the indexes,
    ///generated from the vocabularies. JanusGraph needs both declared and TinkerGraph needs neither, so
    ///this refuses on anything else. Gated like config — it changes what the server will accept.
    ///Answers <see cref="InstanceGraphSchemaResult"/>.
    ///</summary>
    public const string SchemaPath = "api/graph/instance/schema";

    ///<summary>
    ///Whether search would actually be served by an index. Read-only and separate from declaring one:
    ///the declaration is the act, and this is the question you ask months later when the search box has
    ///got slow and nobody remembers whether the act was ever performed on this graph.
    ///</summary>
    public const string SearchHealthPath = "api/graph/instance/search-health";

    ///<summary>Where the interval lives in instance state — a number of minutes, and no credential, so it is not reserved.</summary>
    public const string AutosaveKey = "instance:graph:autosave";

    ///<summary>What an instance that has never been told runs at: five minutes, the user's call of 2026-09-07.</summary>
    public const int AutosaveDefaultMinutes = 5;

    ///<summary>The shortest interval accepted. A save is a full rewrite of the whole graph.</summary>
    public const int AutosaveFloorMinutes = 5;

    ///<summary>A day. Longer than this is off in all but name, and off says so plainly.</summary>
    public const int AutosaveCeilingMinutes = 1440;

    ///<summary>What the settings page offers, in order. Zero is off; any other value in range is accepted by the endpoint.</summary>
    public static readonly int[] AutosaveChoices = [0, 5, 15, 30, 60, 240, 720, 1440];

    ///<summary>Why an interval is refused, or null when it is fine. Refusals name the number, so an administrator learns the rule rather than the fact that one exists.</summary>
    public static string ValidateAutosave(int minutes)
    {
        if (minutes == 0)
            return null;

        if (minutes < 0)
            return "An autosave interval cannot be negative. Use 0 to turn it off.";

        if (minutes < AutosaveFloorMinutes)
            return $"An autosave interval is 0 (off) or at least {AutosaveFloorMinutes} minutes: every save rewrites the whole graph, and a shorter one would spend the server on writing rather than answering.";

        if (minutes > AutosaveCeilingMinutes)
            return $"An autosave interval of more than {AutosaveCeilingMinutes} minutes is off in all but name. Use 0, which says so.";

        return null;
    }

    ///<summary>How an interval reads in a sentence — "off", or the number of minutes, hours or days it is.</summary>
    public static string AutosaveLabel(int minutes)
    {
        if (minutes <= 0)
            return "Off";

        if (minutes < 60)
            return $"Every {minutes} minutes";

        if (minutes == 60)
            return "Every hour";

        if (minutes < 1440)
            return $"Every {minutes / 60} hours";

        return "Every day";
    }

    ///<summary>
    ///Whether an engine can host the instance graph. TinkerPop and Cosmos speak the platform's own
    ///Gremlin and can do everything the app knows how to do; everything else is refused with the
    ///reason, never silently hidden (ADR 0009 — multi-engine support is the standalone viewer's
    ///feature, not the platform's).
    ///</summary>
    public static bool CanHost(string databaseType, out string reason)
    {
        if (databaseType == GraphDbProviders.TinkerPop || databaseType == GraphDbProviders.CosmosDb)
        {
            reason = null;

            return true;
        }

        if (databaseType == GraphDbProviders.Sparql)
            reason = "SPARQL endpoints cannot host the instance graph: there is no query builder for them, so nothing could compose a query on a plugin's behalf — and no writes.";
        else if (databaseType == GraphDbProviders.Gun)
            reason = "GUN cannot host the instance graph: it cannot enumerate itself, so there is no way to load what plugins stored.";
        else if (databaseType == GraphDbProviders.Neo4j || databaseType == GraphDbProviders.Arango || databaseType == GraphDbProviders.Dgraph)
            reason = "The instance graph is Apache TinkerPop (ADR 0009): the platform is developed against Gremlin, and the other engines are the standalone viewer's feature — each is missing something plugins rely on, like multi-statement scripts or step debugging.";
        else
            reason = $@"""{databaseType}"" is not an engine this build knows.";

        return false;
    }
}

///<summary>What any admitted caller may know about the instance graph: whether there is one.</summary>
public sealed class InstanceGraphStatus
{
    public bool Configured { get; set; }

    public string DatabaseType { get; set; }

    ///<summary>The host it points at — display only, so an administrator recognizes which server.</summary>
    public string Host { get; set; }
}

///<summary>The execute endpoint's body: just the query — the connection is the server's business.</summary>
public sealed class InstanceGraphQuery
{
    public string Query { get; set; }
}

///<summary>What Save now did — or why it did not, in a sentence the page shows as it is.</summary>
public sealed class InstanceGraphSaveResult
{
    public bool Saved { get; set; }

    ///<summary>Why nothing was written: no graph, a graph that persists on its own, a TinkerGraph with no
    ///file, or a server that failed mid-write — in which case the file it had is untouched.</summary>
    public string Error { get; set; }

    ///<summary>The file, as the server sees it.</summary>
    public string Location { get; set; }

    ///<summary>The format written — the one the graph is configured to read at its next start.</summary>
    public string Format { get; set; }

    public long Bytes { get; set; }

    public long Vertices { get; set; }

    public long Edges { get; set; }

    public static InstanceGraphSaveResult Refused(string error)
    {
        return new InstanceGraphSaveResult { Saved = false, Error = error };
    }
}

///<summary>One statement of the schema declaration and what it did, as the report lists it.</summary>
public sealed class InstanceGraphSchemaStep
{
    ///<summary>What it declared: "Property keys", "The identity index", or a label's name.</summary>
    public string Title { get; set; }

    ///<summary>What happened — what was made, or that it was already there.</summary>
    public string Detail { get; set; }
}

///<summary>
///What declaring the schema did, step by step. It stops at the first refusal, so a report that is not
///<see cref="Ok"/> lists the steps that DID run and names the one that stopped it — a schema half
///declared is a thing somebody has to be able to see the shape of.
///</summary>
public sealed class InstanceGraphSchemaResult
{
    public bool Ok { get; set; }

    ///<summary>Why it stopped, in a sentence — the graph is not a JanusGraph, the vocabularies disagree, or the server refused a statement.</summary>
    public string Error { get; set; }

    public List<InstanceGraphSchemaStep> Steps { get; set; } = new();

    public static InstanceGraphSchemaResult Refused(string error)
    {
        return new InstanceGraphSchemaResult { Ok = false, Error = error };
    }
}

///<summary>
///The autosave's setting and its last run (the instance-graph-backend research, question 5's other
///half). The interval is what the server keeps; the rest describes this server's own run, so a
///restart shows nothing until the first one has happened.
///</summary>
public sealed class InstanceGraphAutosaveState
{
    ///<summary>Minutes between saves. Zero is off.</summary>
    public int Minutes { get; set; }

    ///<summary>When the next one falls due, or null when the interval is off.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    ///<summary>When the last one ran, or null when none has on this server.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    public bool LastSaved { get; set; }

    ///<summary>Why the last one wrote nothing — the same sentence <em>Save now</em> would have shown.</summary>
    public string LastError { get; set; }

    public long LastVertices { get; set; }

    public long LastEdges { get; set; }

    public long LastBytes { get; set; }
}

///<summary>The autosave endpoint's body: the interval, in minutes, and nothing else.</summary>
public sealed class InstanceGraphAutosaveSetting
{
    public int Minutes { get; set; }
}
