using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///Builds the queries the viewer runs on the user's behalf — everything beyond the query the user typed:
///loading the database, expanding a node, and the edits staged into the Generated tab. One implementation
///per query language (<see cref="GremlinQueryBuilder"/> via its adapter, <see cref="CypherQueryBuilder"/>),
///picked by the connection's <see cref="GraphDbProvider"/>.
///
///This is the seam the <see cref="GraphDbCapabilities"/> flags were waiting on: every browse / traverse /
///stage-edits feature was built from Gremlin strings, so an engine without a builder had to leave those
///capabilities off. A provider that supplies one can switch them on.
///</summary>
public interface IGraphQueryBuilder
{
    //── Browse (GraphDbCapabilities.BrowseGraph) ────────────────────────

    ///<summary>The first <paramref name="limit"/> vertices — the "Load DB" query when edges aren't wanted.</summary>
    string LimitedVertices(int limit);

    ///<summary>Vertices with their edges, capped at <paramref name="limit"/> (null for uncapped).</summary>
    string FullGraph(int? limit);

    ///<summary>
    ///Vertices of one label whose <c>name</c> is exactly one of these, capped at <paramref name="limit"/> —
    ///how a generated entity is matched against what the database already holds, without loading the graph
    ///to find out. <c>name</c> and not some other key because that is what the generator writes and what the
    ///viewer displays; a graph that names its entities otherwise matches nothing, which is the honest
    ///answer rather than a guess at which key meant a name.
    ///
    ///<strong>Exactly</strong>, where the fold that follows normalizes: a generated "Acme Inc." finds a
    ///stored "Acme Inc." and not a stored "Acme". Normalizing would have to happen in the database, in each
    ///language, over an unindexed expression — a scan of somebody's graph per generated name. What this
    ///catches is the common case, a model naming an entity the way the graph already does.
    ///
    ///Null where the language cannot ask it, and the knowledge-graph modal then does not offer the match.
    ///</summary>
    string VerticesByName(string label, IReadOnlyList<string> names, int limit)
    {
        return null;
    }

    //── Traverse (GraphDbCapabilities.Traverse) ─────────────────────────

    ///<summary>The neighborhood of one vertex — expand-on-double-click.</summary>
    string Neighbors(string id, int limit);

    ///<summary>Incoming edges of a vertex, with the vertex on the other end.</summary>
    string InEdges(string vertexId);

    ///<summary>Outgoing edges of a vertex, with the vertex on the other end.</summary>
    string OutEdges(string vertexId);

    ///<summary>A single vertex's display label, for naming an edge's endpoints.</summary>
    string VertexDisplayLabel(string vertexId);

    ///<summary>
    ///Reads an <see cref="InEdges"/> / <see cref="OutEdges"/> answer into the connections panel's rows.
    ///It belongs next to the query because the two are one contract: Gremlin projects GraphSON objects,
    ///Cypher answers with plain rows, and only whoever wrote the query knows which to expect.
    ///</summary>
    List<EdgeInfo> ParseEdgeList(GraphDbResult result);

    ///<summary>Reads a <see cref="VertexDisplayLabel"/> answer, or null when it produced nothing.</summary>
    string ParseDisplayLabel(GraphDbResult result);

    //── Staged edits (GraphDbCapabilities.StageEdits) ───────────────────

    string AddVertex(string label);
    string AddVertexWithName(string label, string name);
    string AddVertexWithNameAt(string label, string name, double x, double y);
    string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties);
    string AddEdge(string sourceId, string label, string targetId);
    string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties);

    ///<summary>
    ///False where the language cannot hold a property on a relationship: an RDF triple carries none, and a
    ///GUN link is a key holding a node rather than a thing of its own. Both drop them rather than writing
    ///them somewhere they would not be found again, and what the generated-graph preview does with this is
    ///say so — the generator can put a confidence and a source on every relationship it finds, and losing
    ///those without a word is the kind of quiet the rest of that preview exists to prevent.
    ///</summary>
    bool WritesEdgeProperties => true;

    ///<summary>Deletes a vertex and its incident edges. <paramref name="idType"/> is the engine's id type, or null.</summary>
    string DropVertex(string id, string idType);
    string DropEdge(string id, string idType);

    ///<summary><paramref name="type"/> is "node" or "edge".</summary>
    string SetProperty(string type, string id, string key, string value, string idType);
    string DropProperty(string type, string id, string key, string idType);

    ///<summary>Strips every viewer-written property (the gdbv* keys) from the whole graph — Database cleanup.</summary>
    string DropAllViewerProperties();

    //── Guards and parsing ──────────────────────────────────────────────

    ///<summary>
    ///True when the query would change the graph. Guards the read-only query tool handed to the AI
    ///(<see cref="GraphDbCapabilities.AiTools"/>) and the step debugger, and marks a query Ask AI proposes in this
    ///engine's language as one that modifies the graph.
    ///</summary>
    bool IsMutating(string query);

    ///<summary>
    ///Why the query is not run as a read, or null when it is one: what the read-only query tool tells the model, and the
    ///debugger the user, so either can put the query right. An engine whose guard can say no more than
    ///<see cref="IsMutating"/> says that it writes.
    ///</summary>
    string ReadRefusal(string query)
    {
        if (IsMutating(query))
            return "That query mutates the graph, which is not allowed here. Only read-only queries can be run.";

        return null;
    }

    ///<summary>
    ///Reads the staged-edit buffer back into structured edits, so the canvas can preview uncommitted
    ///changes ("reflect database state" off). Returning an empty list is safe — the viewer then shows the
    ///loaded baseline unchanged.
    ///</summary>
    List<GraphEdit> ParseEdits(string buffer);
}
