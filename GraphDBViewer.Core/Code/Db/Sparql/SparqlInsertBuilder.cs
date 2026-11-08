using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///The SPARQL side of <see cref="IGraphQueryBuilder"/>, and only its writing half: it turns a graph the app
///composed — a generated knowledge graph — into SPARQL Update text to run against an endpoint. Each element
///is its own <c>INSERT DATA</c> ending in a semicolon, so the lines are one request when joined and each
///still runs on its own, which is what the preview, the counts and the staged buffer all assume.
///
///<strong>It is not the provider's builder.</strong> A SPARQL connection composes nothing on the user's
///behalf — no Load DB, no expand, no staged edits — and the app has no update endpoint to commit through,
///so <see cref="GraphDbProviders"/> leaves those capabilities off and its builder null. Giving the provider
///one would switch that reading on. This is handed to the knowledge-graph modal alone, which until
///2026-09-16 previewed Gremlin <c>addV</c> that no SPARQL endpoint can run.
///
///<strong>What a triple cannot carry.</strong> A relationship's properties — the confidence and source the
///generator can attach to one — have nowhere to live in a plain triple. Reification or RDF-star would each
///be a larger claim than this makes, and RDF-star is not answered by every store, so they are left out and
///the modal says so rather than the script pretending otherwise.
///
///<strong>Names.</strong> An id that is already an IRI or a blank node is written as it stands, so a graph
///read from an endpoint keeps its own names and a merge points at the real thing. Anything else — "alice",
///"Person", "worksAt", which is what a generated graph carries — is minted under <see cref="Base"/>.
///</summary>
public sealed class SparqlInsertBuilder : IGraphQueryBuilder
{
    ///<summary>
    ///The namespace a minted name lives in. A URN because this app owns no domain to put one under, and an
    ///invented http IRI would look resolvable and never be; replacing it is one find-and-replace.
    ///</summary>
    public const string Base = "urn:kg:";

    public static readonly SparqlInsertBuilder Instance = new();

    //An id the app did not mint: an absolute IRI or a blank node label. Deliberately narrow — "acme:inc"
    //from a model is a name, not a scheme, so only a real scheme separator or a URN counts.
    private static readonly Regex Named = new(@"^(_:|urn:|[A-Za-z][A-Za-z0-9+.-]*://)", RegexOptions.IgnoreCase);

    //Every SPARQL Update form, for the read-only guard.
    private static readonly Regex Updates = new(@"\b(INSERT|DELETE|LOAD|CLEAR|CREATE|DROP|COPY|MOVE|ADD)\b", RegexOptions.IgnoreCase);

    //── Writing ─────────────────────────────────────────────────────────

    public string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var statements = new List<string>();

        if (!string.IsNullOrWhiteSpace(label))
            statements.Add($"a {Iri(label)}");

        foreach (var property in properties ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(property.Key))
                statements.Add($"{Iri(property.Key)} {Literal(property.Value)}");

        //A subject with nothing to say about it is no triple at all; SPARQL has no bare node to write.
        if (statements.Count == 0)
            throw new NotSupportedException("An entity with no label and no properties cannot be written as RDF: a triple needs something to say about it.");

        return Insert($"{Iri(id)} {string.Join(" ; ", statements)}");
    }

    public string AddEdge(string sourceId, string label, string targetId)
    {
        return Insert($"{Iri(sourceId)} {Iri(label)} {Iri(targetId)}");
    }

    ///<summary>The triple, without the properties offered for it — see the note on this class.</summary>
    public string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        return AddEdge(sourceId, label, targetId);
    }

    public bool WritesEdgeProperties => false;

    public string SetProperty(string type, string id, string key, string value, string idType)
    {
        if (type == "edge")
            throw new NotSupportedException("A triple is not a thing a property can be set on. Only an entity's properties can be written as RDF.");

        return Insert($"{Iri(id)} {Iri(key)} {Literal(value)}");
    }

    //── What it does not do ─────────────────────────────────────────────

    ///<summary>Nothing is staged for a SPARQL connection, so there is nothing to read back — and an empty
    ///list is the documented safe answer: the canvas then shows the loaded graph unchanged.</summary>
    public List<GraphEdit> ParseEdits(string buffer)
    {
        return new List<GraphEdit>();
    }

    ///<summary>True for every SPARQL Update form, which is what this builder writes and a read must not be.</summary>
    public bool IsMutating(string query)
    {
        return !string.IsNullOrWhiteSpace(query) && Updates.IsMatch(query);
    }

    public string AddVertex(string label)
    {
        throw Unwritable();
    }

    public string AddVertexWithName(string label, string name)
    {
        throw Unwritable();
    }

    public string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        throw Unwritable();
    }

    public string AddVertexWithNameOnEarth(string label, string name, double latitude, double longitude)
    {
        throw Unwritable();
    }

    //An entity with no id has no IRI to be written under, and RDF has no id to hand back the way a graph
    //database does. Every graph this writes names its entities, so nothing reaches these.
    private static NotSupportedException Unwritable()
    {
        return new NotSupportedException("An entity has to be named before it can be written as RDF: RDF has no id of its own to give one.");
    }

    public string DropVertex(string id, string idType)
    {
        throw Readonly();
    }

    public string DropEdge(string id, string idType)
    {
        throw Readonly();
    }

    public string DropProperty(string type, string id, string key, string idType)
    {
        throw Readonly();
    }

    public string DropAllViewerProperties()
    {
        throw Readonly();
    }

    private static NotSupportedException Readonly()
    {
        return new NotSupportedException("This writes a graph out as SPARQL to run elsewhere; it does not delete. Run the deletion against your endpoint yourself.");
    }

    public string LimitedVertices(int limit)
    {
        throw Unbrowsable();
    }

    public string FullGraph(int? limit)
    {
        throw Unbrowsable();
    }

    public string Neighbors(string id, int limit)
    {
        throw Unbrowsable();
    }

    public string InEdges(string vertexId)
    {
        throw Unbrowsable();
    }

    public string OutEdges(string vertexId)
    {
        throw Unbrowsable();
    }

    public string VertexDisplayLabel(string vertexId)
    {
        throw Unbrowsable();
    }

    public List<EdgeInfo> ParseEdgeList(GraphDbResult result)
    {
        throw Unbrowsable();
    }

    public string ParseDisplayLabel(GraphDbResult result)
    {
        throw Unbrowsable();
    }

    //A SPARQL connection writes its own queries: the viewer never composes one on the user's behalf, which
    //is why its provider has no builder at all. Reaching these means something switched that on.
    private static NotSupportedException Unbrowsable()
    {
        return new NotSupportedException("A SPARQL connection composes no queries on your behalf; this only writes a graph out as SPARQL.");
    }

    //── Names and values ────────────────────────────────────────────────

    ///<summary>An IRI as the script writes it: the name the graph already has, or one minted under <see cref="Base"/>.</summary>
    public static string Iri(string value)
    {
        var name = value ?? "";

        if (Named.IsMatch(name))
        {
            if (name.StartsWith("_:", StringComparison.Ordinal))
                return name;

            return $"<{Bracketed(name)}>";
        }

        return $"<{Base}{Uri.EscapeDataString(name)}>";
    }

    //A name the graph already has, as it can stand inside the angle brackets. What an IRI cannot hold there (SPARQL
    //1.1's IRIREF: a space or a control character, <, >, ", {, }, |, ^, a backtick and a backslash) is written as the
    //%XX of its byte, each being ASCII; the rest of the name, its ://, #, ?, a letter beyond ASCII and any %XX already
    //there, is the name's own and stays, which is why this is not Uri.EscapeDataString. A generated graph can mint
    //"http://example.org/a thing", and until 2026-11-07 it was written as it stood: a space made an invalid triple,
    //and a > closed the IRI early, so the rest of the name ran on as SPARQL.
    private static string Bracketed(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (c <= ' ' || c is '<' or '>' or '"' or '{' or '}' or '|' or '^' or '`' or '\\')
                sb.Append('%').Append(((int)c).ToString("X2"));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    ///<summary>
    ///A value as a quoted string, with what a SPARQL string cannot hold escaped. Every value is a string:
    ///a number or a date would have to be guessed at a datatype, and a wrong datatype is a wrong fact.
    ///</summary>
    public static string Literal(string value)
    {
        var text = (value ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");

        return $"\"{text}\"";
    }

    //One operation per element, ended with a semicolon so that the lines joined together are one request.
    private static string Insert(string triple)
    {
        return $"INSERT DATA {{ {triple} . }} ;";
    }
}
