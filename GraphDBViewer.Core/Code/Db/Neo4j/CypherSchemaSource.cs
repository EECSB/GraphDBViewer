using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads a Neo4j / Memgraph schema with plain Cypher aggregation and normalizes it into the same facts
///<see cref="SchemaBuilder"/> builds the meta-graph from. Deliberately not <c>db.labels()</c> /
///<c>apoc.meta.*</c>: aggregating over the data needs no procedures, no APOC install and no special
///privileges, and answers the per-label property keys the viewer wants in the same pass.
///
///Where Gremlin answers these as GraphSON maps, Cypher answers as rows — which is the whole reason schema
///reading is a per-provider seam rather than a shared helper.
///</summary>
public sealed class CypherSchemaSource : IGraphSchemaSource
{
    public static readonly CypherSchemaSource Instance = new();

    ///<summary>Node labels with instance counts. A node with several labels counts once under each.</summary>
    public const string NodeLabelCounts = @"MATCH (n) UNWIND labels(n) AS label
RETURN label AS label, count(*) AS count";

    ///<summary>Relationship types with counts.</summary>
    public const string RelationshipTypeCounts = @"MATCH ()-[r]->()
RETURN type(r) AS label, count(*) AS count";

    ///<summary>Distinct property keys per node label, one row per (label, key).</summary>
    public const string NodePropertyKeys = @"MATCH (n) UNWIND labels(n) AS label UNWIND keys(n) AS key
RETURN DISTINCT label AS label, key AS key";

    ///<summary>Distinct (startLabel, relationshipType, endLabel) triples.</summary>
    public const string RelationshipTriples = @"MATCH (a)-[r]->(b) UNWIND labels(a) AS outLabel UNWIND labels(b) AS inLabel
RETURN outLabel AS out, type(r) AS edge, inLabel AS in, count(*) AS count";

    public async Task<SchemaReadResult> ReadVocabularyAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var nodeLabels = await db.ExecuteAsync(NodeLabelCounts, cancellationToken);
        var relTypes = await db.ExecuteAsync(RelationshipTypeCounts, cancellationToken);
        var keys = await db.ExecuteAsync(NodePropertyKeys, cancellationToken);

        var vocabulary = SchemaBuilder.ExtractVocabulary(
            ReadCounts(nodeLabels),
            ReadCounts(relTypes),
            ReadKeys(keys));

        return new SchemaReadResult
        {
            Vocabulary = vocabulary,
            HasErrors = nodeLabels.IsError || relTypes.IsError || keys.IsError
        };
    }

    public async Task<string> BuildSchemaGraphJsonAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var nodeLabels = await db.ExecuteAsync(NodeLabelCounts, cancellationToken);
        var relTypes = await db.ExecuteAsync(RelationshipTypeCounts, cancellationToken);
        var keys = await db.ExecuteAsync(NodePropertyKeys, cancellationToken);
        var triples = await db.ExecuteAsync(RelationshipTriples, cancellationToken);

        return SchemaBuilder.BuildSchemaGraphJson(
            ReadCounts(nodeLabels),
            ReadCounts(relTypes),
            ReadKeys(keys),
            ReadTriples(triples));
    }

    ///<summary>Reads label/count rows into a map. A failed query reads as empty, exactly as Gremlin's does.</summary>
    public static Dictionary<string, long> ReadCounts(GraphDbResult result)
    {
        var counts = new Dictionary<string, long>();

        if (result.IsError || result.Table == null)
            return counts;

        foreach (var row in result.Table.Rows)
        {
            var label = Cell(row, "label");

            if (label == null)
                continue;

            long.TryParse(Cell(row, "count"), out var count);
            counts[label] = count;
        }

        return counts;
    }

    ///<summary>Folds the (label, key) rows into per-label key lists.</summary>
    public static Dictionary<string, List<string>> ReadKeys(GraphDbResult result)
    {
        var keys = new Dictionary<string, List<string>>();

        if (result.IsError || result.Table == null)
            return keys;

        foreach (var row in result.Table.Rows)
        {
            var label = Cell(row, "label");
            var key = Cell(row, "key");

            if (label == null || key == null)
                continue;

            if (!keys.TryGetValue(label, out var list))
            {
                list = new List<string>();
                keys[label] = list;
            }

            if (!list.Contains(key))
                list.Add(key);
        }

        return keys;
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

    private static string Cell(Dictionary<string, string> row, string column)
    {
        if (row.TryGetValue(column, out var value) && !string.IsNullOrEmpty(value))
            return value;

        return null;
    }
}
