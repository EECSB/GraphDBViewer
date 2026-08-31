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
    ///(<see cref="GraphDbCapabilities.AiTools"/>) and the step debugger.
    ///</summary>
    bool IsMutating(string query);

    ///<summary>
    ///Reads the staged-edit buffer back into structured edits, so the canvas can preview uncommitted
    ///changes ("reflect database state" off). Returning an empty list is safe — the viewer then shows the
    ///loaded baseline unchanged.
    ///</summary>
    List<GraphEdit> ParseEdits(string buffer);
}
