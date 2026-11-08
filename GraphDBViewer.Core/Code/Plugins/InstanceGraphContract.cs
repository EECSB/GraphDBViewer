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
    ///Declare the schema (the JanusGraph recipe): property keys with their datatypes and the indexes,
    ///generated from the vocabularies. JanusGraph needs both declared and TinkerGraph needs neither, so
    ///this refuses on anything else. Gated like config — it changes what the server will accept.
    ///POST declares and answers <see cref="InstanceGraphSchemaResult"/>; GET only asks whether the graph
    ///has that schema and answers <see cref="InstanceGraphSchemaState"/>.
    ///</summary>
    public const string SchemaPath = "api/graph/instance/schema";

    ///<summary>
    ///Whether search would actually be served by an index. Read-only and separate from declaring one:
    ///the declaration is the act, and this is the question you ask months later when the search box has
    ///got slow and nobody remembers whether the act was ever performed on this graph.
    ///</summary>
    public const string SearchHealthPath = "api/graph/instance/search-health";

    ///<summary>
    ///The example project (example-project spec): records an instance can be shown with, made by the app's own
    ///endpoints and taken out again whole. GET answers <see cref="InstanceGraphExampleState"/>; POST starts loading
    ///it and DELETE removes it, each answering the same. Gated like config: it writes the whole graph.
    ///</summary>
    public const string ExamplePath = "api/graph/instance/example";

    ///<summary>
    ///Whether an engine can host the instance graph. Only TinkerPop can: it speaks the platform's own
    ///Gremlin and can do everything the app knows how to do. Everything else is refused with the
    ///reason, never silently hidden (ADR 0009 — multi-engine support is the standalone viewer's
    ///feature, not the platform's). That includes Cosmos DB since 2026-09-18, although it speaks the
    ///same query language: the platform's batches are Groovy scripts and its dates typed values, and
    ///Cosmos runs neither.
    ///</summary>
    public static bool CanHost(string databaseType, out string reason)
    {
        if (databaseType == GraphDbProviders.TinkerPop)
        {
            reason = null;

            return true;
        }
        else if (databaseType == GraphDbProviders.CosmosDb)
            reason = "Cosmos DB cannot host the instance graph: the platform writes through Groovy scripts and stores dates as typed values, and Cosmos's Gremlin API takes neither. The viewer can still browse one.";
        else if (databaseType == GraphDbProviders.Sparql)
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

    ///<summary>
    ///What is wrong with the graph's schema, in a sentence, or null when nothing is known to be. Only a
    ///JanusGraph ever has one: it is the engine that fixes a property's datatype from the first value it
    ///stores, so a plugin page shows this before anybody writes into a graph nobody declared.
    ///</summary>
    public string SchemaWarning { get; set; }
}

///<summary>The execute endpoint's body: just the query — the connection is the server's business.</summary>
public sealed class InstanceGraphQuery
{
    public string Query { get; set; }
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
///Whether the graph has the schema this version would declare, asked without declaring anything: which
///of the property keys and indexes are missing, which keys already hold another datatype, and whether
///anything has been written yet. A graph that is not a JanusGraph needs no declared schema and has
///nothing to report.
///</summary>
public sealed class InstanceGraphSchemaState
{
    public bool JanusGraph { get; set; }

    public bool HoldsData { get; set; }

    public List<string> MissingKeys { get; set; } = new();

    ///<summary>Each as a sentence: the key, what it holds, and what the vocabulary says it should.</summary>
    public List<string> WrongKeys { get; set; } = new();

    public List<string> MissingIndexes { get; set; } = new();

    ///<summary>What a page shows, or null when there is nothing to warn about.</summary>
    public string Warning { get; set; }

    ///<summary>Why the graph could not be asked, or null when it answered.</summary>
    public string Error { get; set; }
}

///<summary>The example project, as the instance graph's page shows it: whether it can be loaded, is loading, or is in the graph.</summary>
public sealed class InstanceGraphExampleState
{
    ///<summary>What the example is, in a few words, and a sentence about what it holds.</summary>
    public string Title { get; set; }

    public string Description { get; set; }

    ///<summary>Whether an instance graph is configured at all; nothing else is asked without one.</summary>
    public bool Configured { get; set; }

    ///<summary>Whether the graph holds no records at all: an instance just set up, where the example is offered.</summary>
    public bool Empty { get; set; }

    ///<summary>Whether the example's records are in the graph, wholly or in part.</summary>
    public bool Loaded { get; set; }

    ///<summary>When it was loaded and by whom, while it is in the graph.</summary>
    public DateTimeOffset? LoadedAt { get; set; }

    public string LoadedBy { get; set; }

    ///<summary>Whether a load is running now, and how far it has got: the steps done of all of them, and the one it is on.</summary>
    public bool Running { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public string Step { get; set; }

    ///<summary>Why the last load or removal stopped, or why one is refused; null when nothing went wrong.</summary>
    public string Error { get; set; }

    ///<summary>What the last removal took out: the records, and the change records with them.</summary>
    public long Removed { get; set; }
}
