using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads an ArangoDB schema, in phases, because AQL forces the shape:
///
///  1. <c>COLLECTIONS()</c> lists the collection names. It is the only way to discover them, and it is
///     also all it gives — the <c>type</c> field it returns is null, so it cannot say which are edges.
///  2. A query <i>generated from those names</i> samples each collection for its document count, whether
///     it holds edges (its documents carry <c>_from</c>), and its attribute keys. This second pass is
///     unavoidable: AQL cannot iterate a collection named by an expression, so the names have to be known
///     before a query can look inside them.
///  3. The edge collections, now known, are sampled for their (from, edge, to) collection triples.
///
///Attribute keys come from a sample rather than a declaration — ArangoDB is schemaless, so what a
///collection "has" is only ever what its documents happen to carry.
///</summary>
public sealed class AqlSchemaSource : IGraphSchemaSource
{
    public static readonly AqlSchemaSource Instance = new();

    ///<summary>Documents sampled per collection when collecting attribute keys and relationship triples.</summary>
    public const int SampleSize = 200;

    ///<summary>Lists the user's collections, skipping ArangoDB's own (which all start with an underscore).</summary>
    public const string CollectionNames = @"FOR c IN COLLECTIONS()
FILTER LEFT(c.name, 1) != '_'
SORT c.name
RETURN c.name";

    ///<summary>What the viewer learned about one collection.</summary>
    public sealed class CollectionInfo
    {
        public string Name { get; init; } = "";
        public long Count { get; init; }
        public bool IsEdge { get; init; }
        public List<string> Keys { get; init; } = new();
    }

    public async Task<SchemaReadResult> ReadVocabularyAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var names = await ReadCollectionNamesAsync(db, cancellationToken);

        if (names.Failed)
            return new SchemaReadResult { HasErrors = true };

        var info = await ReadCollectionInfoAsync(db, names.Names, cancellationToken);

        if (info.Failed)
            return new SchemaReadResult { HasErrors = true };

        var vertexCounts = new Dictionary<string, long>();
        var edgeCounts = new Dictionary<string, long>();
        var keys = new Dictionary<string, List<string>>();

        foreach (var collection in info.Collections)
        {
            if (collection.IsEdge)
                edgeCounts[collection.Name] = collection.Count;
            else
                vertexCounts[collection.Name] = collection.Count;

            keys[collection.Name] = collection.Keys;
        }

        return new SchemaReadResult
        {
            Vocabulary = SchemaBuilder.ExtractVocabulary(vertexCounts, edgeCounts, keys),
            HasErrors = false
        };
    }

    public async Task<string> BuildSchemaGraphJsonAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var names = await ReadCollectionNamesAsync(db, cancellationToken);
        var info = await ReadCollectionInfoAsync(db, names.Names, cancellationToken);

        var vertexCounts = new Dictionary<string, long>();
        var edgeCounts = new Dictionary<string, long>();
        var keys = new Dictionary<string, List<string>>();
        var edgeCollections = new List<string>();

        foreach (var collection in info.Collections)
        {
            if (collection.IsEdge)
            {
                edgeCounts[collection.Name] = collection.Count;
                edgeCollections.Add(collection.Name);
            }
            else
            {
                vertexCounts[collection.Name] = collection.Count;
            }

            keys[collection.Name] = collection.Keys;
        }

        //A failed phase degrades the meta-graph rather than failing it — the same best-effort the Gremlin
        //source has. Worth knowing when debugging: a broken triples query shows as a graph with nodes but
        //no edges, not as an error.
        var triples = await ReadTriplesAsync(db, edgeCollections, cancellationToken);

        return SchemaBuilder.BuildSchemaGraphJson(vertexCounts, edgeCounts, keys, triples);
    }

    ///<summary>The collection names, and whether the query that read them failed.</summary>
    public async Task<(List<string> Names, bool Failed)> ReadCollectionNamesAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var result = await db.ExecuteAsync(CollectionNames, cancellationToken);

        if (result.IsError)
            return (new List<string>(), true);

        return (ReadNames(result), false);
    }

    ///<summary>Reads the bare-value rows the name query answers with.</summary>
    public static List<string> ReadNames(GraphDbResult result)
    {
        var names = new List<string>();

        if (result.IsError || result.Table == null)
            return names;

        foreach (var row in result.Table.Rows)
            if (row.TryGetValue(ArangoConverter.ValueColumn, out var name) && !string.IsNullOrWhiteSpace(name))
                names.Add(name);

        return names;
    }

    ///<summary>
    ///Builds the second-phase query: one row per collection with its count, whether it holds edges, and the
    ///attribute keys its sampled documents carry. The collection names are written into the query text
    ///because AQL will not take them any other way.
    ///</summary>
    public static string BuildCollectionInfoQuery(IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0)
            return "RETURN []";

        var entries = new List<string>();

        foreach (var name in names)
        {
            var c = AqlQueryBuilder.QuoteIdentifier(name);
            var literal = "'" + AqlQueryBuilder.Escape(name) + "'";

            entries.Add($@"{{ name: {literal}, count: LENGTH({c}), "
                + $@"isEdge: LENGTH((FOR d IN {c} FILTER HAS(d, '_from') LIMIT 1 RETURN 1)) > 0, "
                + $@"keys: UNIQUE(FLATTEN((FOR d IN {c} LIMIT {SampleSize} RETURN ATTRIBUTES(d, true)))) }}");
        }

        return $"FOR info IN [{string.Join(", ", entries)}] RETURN info";
    }

    ///<summary>Runs the second phase and reads its rows.</summary>
    public async Task<(List<CollectionInfo> Collections, bool Failed)> ReadCollectionInfoAsync(
        IGraphDb db,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        if (names == null || names.Count == 0)
            return (new List<CollectionInfo>(), false);

        var result = await db.ExecuteAsync(BuildCollectionInfoQuery(names), cancellationToken);

        if (result.IsError)
            return (new List<CollectionInfo>(), true);

        return (ReadCollectionInfo(result), false);
    }

    public static List<CollectionInfo> ReadCollectionInfo(GraphDbResult result)
    {
        var collections = new List<CollectionInfo>();

        if (result.IsError || result.Table == null)
            return collections;

        foreach (var row in result.Table.Rows)
        {
            var name = Cell(row, "name");

            if (name == null)
                continue;

            long.TryParse(Cell(row, "count"), out var count);

            collections.Add(new CollectionInfo
            {
                Name = name,
                Count = count,
                IsEdge = string.Equals(Cell(row, "isEdge"), "true", StringComparison.OrdinalIgnoreCase),
                Keys = ReadKeyArray(Cell(row, "keys"))
            });
        }

        return collections;
    }

    ///<summary>
    ///Builds the third-phase query: the distinct (from-collection, edge-collection, to-collection) triples,
    ///sampled from each edge collection.
    ///</summary>
    public static string BuildTriplesQuery(IReadOnlyList<string> edgeCollections)
    {
        if (edgeCollections == null || edgeCollections.Count == 0)
            return "RETURN []";

        var parts = new List<string>();

        foreach (var name in edgeCollections)
        {
            var c = AqlQueryBuilder.QuoteIdentifier(name);
            var literal = "'" + AqlQueryBuilder.Escape(name) + "'";

            //The keys are quoted because "in" is an AQL keyword — bare, it is a syntax error, and the
            //triples would silently come back empty.
            parts.Add($@"(FOR e IN {c} LIMIT {SampleSize} "
                + "COLLECT o = PARSE_IDENTIFIER(e._from).collection, i = PARSE_IDENTIFIER(e._to).collection "
                + $"RETURN {{ \"out\": o, \"edge\": {literal}, \"in\": i }})");
        }

        if (parts.Count == 1)
            return $"FOR t IN {parts[0]} RETURN t";

        return $"FOR t IN UNION({string.Join(", ", parts)}) RETURN t";
    }

    public async Task<List<(string Out, string Edge, string In)>> ReadTriplesAsync(
        IGraphDb db,
        IReadOnlyList<string> edgeCollections,
        CancellationToken cancellationToken = default)
    {
        if (edgeCollections == null || edgeCollections.Count == 0)
            return new List<(string, string, string)>();

        var result = await db.ExecuteAsync(BuildTriplesQuery(edgeCollections), cancellationToken);

        return ReadTriples(result);
    }

    public static List<(string Out, string Edge, string In)> ReadTriples(GraphDbResult result)
    {
        var triples = new List<(string, string, string)>();

        if (result.IsError || result.Table == null)
            return triples;

        foreach (var row in result.Table.Rows)
        {
            var outLabel = Cell(row, "out");
            var edge = Cell(row, "edge");
            var inLabel = Cell(row, "in");

            if (outLabel == null || edge == null || inLabel == null)
                continue;

            triples.Add((outLabel, edge, inLabel));
        }

        return triples;
    }

    //The keys column arrives as a JSON array, since a table cell holds a nested value as its JSON text.
    private static List<string> ReadKeyArray(string json)
    {
        var keys = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
            return keys;

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return keys;

            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    keys.Add(item.GetString());
        }
        catch { }

        return keys;
    }

    private static string Cell(Dictionary<string, string> row, string column)
    {
        if (row.TryGetValue(column, out var value) && !string.IsNullOrEmpty(value))
            return value;

        return null;
    }
}
