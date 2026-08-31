using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads a Dgraph schema. Unlike the other engines this needs almost no work: Dgraph <b>declares</b> its
///schema, so one <c>schema {}</c> query answers every question the viewer has — which predicates exist,
///which of them are edges (<c>type: "uid"</c>), and which types carry which predicates.
///
///Two follow-ups the declaration cannot give:
///
///  * <b>Counts.</b> A schema says a type exists, not how many nodes have it, so a second query counts
///    each — generated from the type names, because a DQL block has to name its type.
///  * <b>Relationship triples.</b> The schema says <c>knows</c> is a <c>uid</c> predicate; it does not say
///    a Person knows a Person. That is data, so it is sampled: which types are seen at each end.
///</summary>
public sealed class DgraphSchemaSource : IGraphSchemaSource
{
    public static readonly DgraphSchemaSource Instance = new();

    ///<summary>Nodes sampled per edge predicate when working out which types it connects.</summary>
    public const int SampleSize = 200;

    ///<summary>The declaration, whole. Dgraph answers with both its predicates and its types.</summary>
    public const string SchemaQuery = "schema {}";

    ///<summary>Dgraph's own predicates and types describe the cluster, not the user's graph.</summary>
    public const string ReservedPrefix = "dgraph.";

    ///<summary>What the declaration said.</summary>
    public sealed class DgraphSchema
    {
        ///<summary>Predicates holding values — a node's properties.</summary>
        public List<string> ScalarPredicates { get; } = new();

        ///<summary>Predicates holding nodes — Dgraph's edges.</summary>
        public List<string> EdgePredicates { get; } = new();

        ///<summary>Each declared type and the predicates it carries.</summary>
        public Dictionary<string, List<string>> Types { get; } = new(StringComparer.Ordinal);
    }

    public async Task<SchemaReadResult> ReadVocabularyAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var read = await ReadSchemaAsync(db, cancellationToken);

        if (read.Schema == null)
            return new SchemaReadResult { HasErrors = true };

        var counts = await ReadTypeCountsAsync(db, read.Schema, cancellationToken);

        return new SchemaReadResult
        {
            Vocabulary = SchemaBuilder.ExtractVocabulary(counts, EdgeCounts(read.Schema), TypeKeys(read.Schema)),
            HasErrors = false
        };
    }

    public async Task<string> BuildSchemaGraphJsonAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var read = await ReadSchemaAsync(db, cancellationToken);

        if (read.Schema == null)
            return SchemaBuilder.BuildSchemaGraphJson(
                new Dictionary<string, long>(),
                new Dictionary<string, long>(),
                new Dictionary<string, List<string>>(),
                new List<(string Out, string Edge, string In)>());

        var counts = await ReadTypeCountsAsync(db, read.Schema, cancellationToken);
        var triples = await ReadTriplesAsync(db, read.Schema, cancellationToken);

        return SchemaBuilder.BuildSchemaGraphJson(counts, EdgeCounts(read.Schema), TypeKeys(read.Schema), triples);
    }

    ///<summary>Runs <c>schema {}</c> and reads the declaration, or returns a null schema when it failed.</summary>
    public static async Task<(DgraphSchema Schema, string Error)> ReadSchemaAsync(IGraphDb db, CancellationToken cancellationToken = default)
    {
        var result = await db.ExecuteAsync(SchemaQuery, cancellationToken);

        if (result.IsError)
            return (null, result.Error);

        //The answer is rows rather than a graph — a predicate is not a node — so it arrives as a table with
        //the raw response kept alongside. The raw is what carries the nesting this needs.
        return (ParseSchema(result.RawResponse), null);
    }

    ///<summary>Reads a <c>schema {}</c> response body into the declaration it describes.</summary>
    public static DgraphSchema ParseSchema(string responseJson)
    {
        var schema = new DgraphSchema();

        if (string.IsNullOrWhiteSpace(responseJson))
            return schema;

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(responseJson);
        }
        catch
        {
            return schema;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data))
                return schema;

            if (data.TryGetProperty("schema", out var predicates) && predicates.ValueKind == JsonValueKind.Array)
                foreach (var predicate in predicates.EnumerateArray())
                {
                    var name = Text(predicate, "predicate");

                    if (name == null || name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
                        continue;

                    //"uid" is Dgraph's way of saying this predicate holds nodes — which is to say, edges.
                    if (Text(predicate, "type") == "uid")
                        schema.EdgePredicates.Add(name);
                    else
                        schema.ScalarPredicates.Add(name);
                }

            if (data.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                foreach (var type in types.EnumerateArray())
                {
                    var name = Text(type, "name");

                    if (name == null || name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
                        continue;

                    var fields = new List<string>();

                    if (type.TryGetProperty("fields", out var declared) && declared.ValueKind == JsonValueKind.Array)
                        foreach (var field in declared.EnumerateArray())
                        {
                            var fieldName = Text(field, "name");

                            if (fieldName != null && !fieldName.StartsWith(ReservedPrefix, StringComparison.Ordinal))
                                fields.Add(fieldName);
                        }

                    schema.Types[name] = fields;
                }
        }

        return schema;
    }

    ///<summary>How many nodes carry each declared type — a block per type, since a block must name one.</summary>
    public static string TypeCountQuery(IEnumerable<string> types)
    {
        var blocks = types
            .Select((type, i) => $"t{i}(func: type({DqlQueryBuilder.Predicate(type)})) {{ count(uid) }}")
            .ToList();

        if (blocks.Count == 0)
            return null;

        return "{ " + string.Join(" ", blocks) + " }";
    }

    ///<summary>
    ///A sample of each edge predicate with the types at both ends, which is the only way to learn what
    ///connects to what — Dgraph declares that a predicate holds nodes, never which kind.
    ///</summary>
    public static string TripleQuery(IEnumerable<string> edgePredicates)
    {
        var blocks = edgePredicates
            .Select((predicate, i) =>
            {
                var name = DqlQueryBuilder.Predicate(predicate);

                return $"e{i}(func: has({name}), first: {SampleSize}) {{ dgraph.type {name} {{ dgraph.type }} }}";
            })
            .ToList();

        if (blocks.Count == 0)
            return null;

        return "{ " + string.Join(" ", blocks) + " }";
    }

    private static async Task<Dictionary<string, long>> ReadTypeCountsAsync(IGraphDb db, DgraphSchema schema, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var types = schema.Types.Keys.ToList();
        var query = TypeCountQuery(types);

        if (query == null)
            return counts;

        var result = await db.ExecuteAsync(query, cancellationToken);

        if (result.IsError)
            return counts;

        //Block i answered for type i, and a block with no matches answers with nothing at all.
        var byBlock = ReadBlockCounts(result.RawResponse);

        for (int i = 0; i < types.Count; i++)
        {
            byBlock.TryGetValue($"t{i}", out var count);
            counts[types[i]] = count;
        }

        return counts;
    }

    private static async Task<List<(string Out, string Edge, string In)>> ReadTriplesAsync(IGraphDb db, DgraphSchema schema, CancellationToken cancellationToken)
    {
        var triples = new List<(string Out, string Edge, string In)>();
        var query = TripleQuery(schema.EdgePredicates);

        if (query == null)
            return triples;

        var result = await db.ExecuteAsync(query, cancellationToken);

        if (result.IsError || string.IsNullOrWhiteSpace(result.RawResponse))
            return triples;

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(result.RawResponse);
        }
        catch
        {
            return triples;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data))
                return triples;

            for (int i = 0; i < schema.EdgePredicates.Count; i++)
            {
                if (!data.TryGetProperty($"e{i}", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    continue;

                var predicate = schema.EdgePredicates[i];

                foreach (var row in rows.EnumerateArray())
                {
                    var from = DgraphConverter.ReadLabel(row);

                    if (!row.TryGetProperty(predicate, out var targets))
                        continue;

                    foreach (var target in Nodes(targets))
                    {
                        var to = DgraphConverter.ReadLabel(target);
                        var triple = (from, predicate, to);

                        if (!triples.Contains(triple))
                            triples.Add(triple);
                    }
                }
            }
        }

        return triples;
    }

    //Every edge predicate counts as a relationship kind; how many of each is data the schema never claims,
    //so the meta-graph shows them without a number rather than with a wrong one.
    private static Dictionary<string, long> EdgeCounts(DgraphSchema schema)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var predicate in schema.EdgePredicates)
            counts[predicate] = 0;

        return counts;
    }

    private static Dictionary<string, List<string>> TypeKeys(DgraphSchema schema)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var type in schema.Types)
            keys[type.Key] = type.Value.Where(f => !schema.EdgePredicates.Contains(f)).ToList();

        return keys;
    }

    ///<summary>Reads a count-per-block answer — <c>{"data":{"t0":[{"count":2}]}}</c>.</summary>
    public static Dictionary<string, long> ReadBlockCounts(string responseJson)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(responseJson))
            return counts;

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(responseJson);
        }
        catch
        {
            return counts;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data))
                return counts;

            foreach (var block in data.EnumerateObject())
            {
                if (block.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var row in block.Value.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Object
                        && row.TryGetProperty("count", out var count)
                        && count.TryGetInt64(out var value))
                        counts[block.Name] = value;
            }
        }

        return counts;
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;

            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                yield return item;
    }

    private static string Text(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }
}
