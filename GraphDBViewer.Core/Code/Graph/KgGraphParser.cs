using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>The outcome of parsing a model's knowledge-graph JSON: the neutral graph plus every repair
///the parser made, or a clear error — never a silent empty or half graph.</summary>
public sealed class KgParseResult
{
    public GraphImport.Graph Graph { get; }
    public List<string> Warnings { get; }
    public string Error { get; }

    public bool IsError => Error != null;

    private KgParseResult(GraphImport.Graph graph, List<string> warnings, string error)
    {
        Graph = graph;
        Warnings = warnings;
        Error = error;
    }

    public static KgParseResult Success(GraphImport.Graph graph, List<string> warnings)
    {
        return new KgParseResult(graph, warnings, null);
    }

    public static KgParseResult Failure(string error)
    {
        return new KgParseResult(null, new List<string>(), error);
    }
}

///<summary>
///The outcome of folding a generated graph against the canvas for Merge mode: the delta script to
///append (addV for genuinely new entities, addE for new edges, property updates for folded entities
///that gained properties), the per-fold warnings, and the post-merge counts the preview shows.
///</summary>
public sealed class KgMergeResult
{
    public string DeltaGremlin { get; }
    public List<string> Warnings { get; }

    ///<summary>The genuinely new nodes the delta adds — what the preview's label breakdown is built
    ///from, so the modal never re-derives fold membership and drifts from the fold itself.</summary>
    public List<GraphImport.Node> AddedNodes { get; }

    public int NewNodes { get; }
    public int NewEdges { get; }
    public int FoldedNodes { get; }
    public int PropertyUpdates { get; }

    public KgMergeResult(string deltaGremlin, List<string> warnings, List<GraphImport.Node> addedNodes, int newEdges, int foldedNodes, int propertyUpdates)
    {
        DeltaGremlin = deltaGremlin;
        Warnings = warnings;
        AddedNodes = addedNodes;
        NewNodes = addedNodes.Count;
        NewEdges = newEdges;
        FoldedNodes = foldedNodes;
        PropertyUpdates = propertyUpdates;
    }
}

///<summary>
///Turns a model's strict-JSON knowledge-graph response into the neutral <see cref="GraphImport.Graph"/>,
///repairing what can be repaired and reporting every repair as a warning: fence stripping, id dedup,
///the entity fold, auto-created edge endpoints, name backfill and the node/edge caps. Also owns the
///fold's second scope — a generated graph against the canvas, for Merge mode — and the delta emission
///that goes with it, built from the same GremlinQueryBuilder pieces ToGremlin uses so the edit parser
///is known to accept every line. Pure and static; no IO.
///</summary>
public static class KgGraphParser
{
    ///<summary>
    ///Caps on one generation, sized so a full-size result fits the 8192 MaxTokens default rather than
    ///chosen — provisional until real documents are measured (see the spec's open questions). KgPrompt
    ///quotes these same constants to the model, so the ask and the enforcement can't drift.
    ///</summary>
    public const int MaxNodes = 100;
    public const int MaxEdges = 200;

    ///<summary>
    ///Property holding the sentence an entity or relationship was extracted from. A plain name rather than
    ///a gdbv* one on purpose: this is data the user is keeping, not viewer bookkeeping that
    ///"Database cleanup" should be free to strip.
    ///</summary>
    public const string SourceProperty = "source";

    ///<summary>Property holding a 0–1 confidence, which the viewer grays the element by.</summary>
    public const string ConfidenceProperty = "confidence";

    ///<summary>
    ///Property holding where in the source text the quoted sentence starts, in characters.
    ///
    ///Not asked of the model — models cannot count characters, and one that tries will confidently invent
    ///a number. It is <b>found</b> instead, by looking the quoted sentence up in the text, which has the
    ///side effect of checking the quote is real: a sentence that is nowhere in the source was not taken
    ///from it.
    ///</summary>
    public const string SourceOffsetProperty = "sourceOffset";

    //Trailing company-form words dropped when normalizing an entity name, so "Acme, Inc." and "Acme"
    //fold. Abbreviations plus their safe full-word forms — but not "company", which is too often the
    //distinguishing token itself. Only ever stripped while at least one other token remains ("Co"
    //alone stays "co").
    private static readonly HashSet<string> CorporateSuffixes = new()
    {
        "inc", "incorporated", "ltd", "limited", "llc", "corp", "corporation", "co", "gmbh", "ag", "plc", "sa", "bv"
    };

    ///<summary>Parses model output into a graph, running every repair and the in-run entity fold.</summary>
    public static KgParseResult Parse(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return KgParseResult.Failure("The model returned nothing.");

        var text = LlmText.StripMarkdownFences(modelOutput);

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return KgParseResult.Failure($"The model's output is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return KgParseResult.Failure("The model's output is not a JSON object.");

            if (!root.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array)
                return KgParseResult.Failure("The model's output has no \"nodes\" array.");

            if (nodesEl.GetArrayLength() > MaxNodes)
                return KgParseResult.Failure($"The model produced {nodesEl.GetArrayLength()} nodes; the cap is {MaxNodes}. Narrow the source text, or extract it in parts.");

            var warnings = new List<string>();
            var graph = new GraphImport.Graph();
            var seenIds = new HashSet<string>();
            int duplicateIds = 0;
            int skippedNodes = 0;
            int missingLabels = 0;

            foreach (var el in nodesEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    skippedNodes++;
                    continue;
                }

                var id = ReadString(el, "id");

                if (string.IsNullOrWhiteSpace(id))
                {
                    skippedNodes++;
                    continue;
                }

                var node = graph.GetOrAdd(id);

                if (!seenIds.Add(id))
                {
                    //Same id twice: first occurrence wins; later ones only contribute missing properties.
                    duplicateIds++;
                    MergeMissingProperties(node, el);
                    continue;
                }

                var label = ReadString(el, "label");

                if (string.IsNullOrWhiteSpace(label))
                {
                    missingLabels++;
                    label = "Entity";
                }

                node.Label = label;
                ReadProperties(el, node.Properties);
                ReadAnnotations(el, node.Properties);
            }

            int autoCreated = 0;
            int skippedEdges = 0;

            if (root.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
            {
                if (edgesEl.GetArrayLength() > MaxEdges)
                    return KgParseResult.Failure($"The model produced {edgesEl.GetArrayLength()} edges; the cap is {MaxEdges}. Narrow the source text, or extract it in parts.");

                foreach (var el in edgesEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        skippedEdges++;
                        continue;
                    }

                    var source = ReadString(el, "source");
                    var target = ReadString(el, "target");

                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    {
                        skippedEdges++;
                        continue;
                    }

                    //An edge naming an undeclared node auto-creates it (GremlinFromJson would silently
                    //drop the edge — the wrong default for model output).
                    autoCreated += EnsureEndpoint(graph, seenIds, source);
                    autoCreated += EnsureEndpoint(graph, seenIds, target);

                    var label = ReadString(el, "label");

                    if (string.IsNullOrWhiteSpace(label))
                        label = "relatedTo";

                    var edge = new GraphImport.Edge { Source = source, Target = target, Label = label };
                    ReadProperties(el, edge.Properties);
                    ReadAnnotations(el, edge.Properties);
                    graph.Edges.Add(edge);
                }
            }

            if (duplicateIds > 0)
                warnings.Add($"Merged {duplicateIds} duplicate node id(s).");

            if (skippedNodes > 0)
                warnings.Add($"Skipped {skippedNodes} node(s) with no id.");

            if (missingLabels > 0)
                warnings.Add($"Defaulted {missingLabels} node(s) without a label to \"Entity\".");

            if (autoCreated > 0)
                warnings.Add($"Auto-created {autoCreated} node(s) that only appeared as edge endpoints.");

            if (skippedEdges > 0)
                warnings.Add($"Skipped {skippedEdges} edge(s) missing a source or target.");

            //The in-run entity fold: different ids that mean the same thing ("Acme" / "Acme Inc.").
            graph = FoldWithinGraph(graph, warnings);

            //Backfill display names last, so a name contributed by a folded duplicate survives.
            int backfilled = 0;

            foreach (var node in graph.Nodes)
            {
                if (!node.Properties.ContainsKey("name") && !node.Properties.ContainsKey("title"))
                {
                    node.Properties["name"] = node.Id;
                    backfilled++;
                }
            }

            if (backfilled > 0)
                warnings.Add($"Backfilled a display name from the id for {backfilled} node(s).");

            return KgParseResult.Success(graph, warnings);
        }
    }

    ///<summary>
    ///Folds a parsed generation against the canvas (Merge mode): an entity whose label and normalized
    ///name match one already on the canvas collapses onto the existing id — the T.id collision resolved
    ///before emission — and the delta script contains addV only for genuinely new entities, addE only
    ///for triples the canvas doesn't have, and property updates for folded entities that gained keys
    ///(the canvas value wins a conflict, and the discarded value is reported).
    ///</summary>
    ///<param name="queryBuilder">The connected database's language; null means Gremlin.</param>
    public static KgMergeResult FoldAgainstCanvas(GraphImport.Graph generated, GraphImport.Graph canvas, IGraphQueryBuilder queryBuilder = null)
    {
        var qb = queryBuilder ?? GremlinQueryBuilderAdapter.Instance;
        var warnings = new List<string>();
        var canvasNodes = new HashSet<GraphImport.Node>(canvas.Nodes);

        //The canvas is authoritative: its nodes seed the fold map and its ids win.
        var authoritative = new Dictionary<(string Label, string Key), GraphImport.Node>();

        foreach (var node in canvas.Nodes)
        {
            var key = FoldKey(node);

            if (key != null && !authoritative.ContainsKey(key.Value))
                authoritative[key.Value] = node;
        }

        var idMap = new Dictionary<string, string>();
        var newNodes = new List<GraphImport.Node>();
        var propertyLines = new List<string>();
        int propertyUpdates = 0;
        int foldedCount = 0;

        foreach (var node in generated.Nodes)
        {
            var key = FoldKey(node);

            if (key != null && authoritative.TryGetValue(key.Value, out var survivor))
            {
                if (ReferenceEquals(survivor, node))
                    continue;

                foldedCount++;
                idMap[node.Id] = survivor.Id;
                warnings.Add($"Merged '{DisplayName(node)}' into existing '{DisplayName(survivor)}' ({survivor.Label}).");

                foreach (var kv in node.Properties)
                {
                    if (!survivor.Properties.TryGetValue(kv.Key, out var existing))
                    {
                        if (canvasNodes.Contains(survivor))
                        {
                            //The canvas node itself is not mutated — the gain is emitted as a staged
                            //property update, which the preview and the commit both see.
                            propertyLines.Add(qb.SetProperty("node", survivor.Id, kv.Key, kv.Value, null));
                            propertyUpdates++;
                        }
                        else
                            survivor.Properties[kv.Key] = kv.Value;
                    }
                    else if (existing != kv.Value)
                        warnings.Add($"Kept '{DisplayName(survivor)}' {kv.Key}='{existing}'; discarded conflicting '{kv.Value}'.");
                }
            }
            else
            {
                if (key != null)
                    authoritative[key.Value] = node;

                newNodes.Add(node);
            }
        }

        //Edges: repoint folded endpoints, skip triples the canvas already has, dedupe within the delta.
        var canvasTriples = new HashSet<string>(canvas.Edges.Select(TripleKey));
        var deltaTriples = new HashSet<string>();
        var edgeLines = new List<string>();
        int existingEdges = 0;

        foreach (var edge in generated.Edges)
        {
            var source = Remap(idMap, edge.Source);
            var target = Remap(idMap, edge.Target);
            var remapped = new GraphImport.Edge { Source = source, Target = target, Label = edge.Label };

            foreach (var kv in edge.Properties)
                remapped.Properties[kv.Key] = kv.Value;

            var triple = TripleKey(remapped);

            if (canvasTriples.Contains(triple))
            {
                existingEdges++;
                continue;
            }

            if (!deltaTriples.Add(triple))
                continue;

            edgeLines.Add(qb.AddEdgeWithProperties(source, remapped.Label, target, remapped.Properties));
        }

        if (existingEdges > 0)
            warnings.Add($"Skipped {existingEdges} edge(s) already on the canvas.");

        var lines = new List<string>();

        foreach (var node in newNodes)
            lines.Add(qb.AddVertexWithProperties(node.Label, node.Id, node.Properties));

        lines.AddRange(edgeLines);
        lines.AddRange(propertyLines);

        return new KgMergeResult(string.Join("\n", lines), warnings, newNodes, edgeLines.Count, foldedCount, propertyUpdates);
    }

    ///<summary>
    ///The render JSON the canvas holds (EffectiveData) as a <see cref="GraphImport.Graph"/>, so Merge
    ///can fold against it. GraphDataConverter.ToTable already yields the same shape, so this is a copy
    ///loop, not a parser.
    ///</summary>
    public static GraphImport.Graph FromRenderJson(JsonElement data)
    {
        var table = GraphDataConverter.ToTable(data);
        var graph = new GraphImport.Graph();

        foreach (var row in table.Nodes)
        {
            if (string.IsNullOrEmpty(row.Id))
                continue;

            var node = graph.GetOrAdd(row.Id);

            if (!string.IsNullOrEmpty(row.Label))
                node.Label = row.Label;

            foreach (var kv in row.Properties)
                node.Properties[kv.Key] = kv.Value;
        }

        foreach (var row in table.Edges)
        {
            if (string.IsNullOrEmpty(row.Source) || string.IsNullOrEmpty(row.Target))
                continue;

            var edge = new GraphImport.Edge { Source = row.Source, Target = row.Target };

            if (!string.IsNullOrEmpty(row.Label))
                edge.Label = row.Label;

            foreach (var kv in row.Properties)
                edge.Properties[kv.Key] = kv.Value;

            graph.Edges.Add(edge);
        }

        return graph;
    }

    ///<summary>
    ///Normalizes an entity name for fold matching: lowercase, punctuation dropped, whitespace collapsed,
    ///and trailing corporate suffixes stripped while another token remains. Empty means "not foldable".
    ///</summary>
    public static string NormalizeEntityKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);

        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append(' ');
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (tokens.Count > 1 && CorporateSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(' ', tokens);
    }

    //── In-run fold ─────────────────────────────────────────────────────

    //Folds same-label nodes whose normalized names match into the first occurrence, unions properties
    //(the survivor wins a conflict, and the discarded value is reported), repoints edges and dedupes
    //ones that collapse onto the same source→target:label triple. Deliberately conservative: exact
    //normalized match within one label only — over-merging destroys information silently, a missed
    //duplicate is visible and fixable by hand.
    private static GraphImport.Graph FoldWithinGraph(GraphImport.Graph graph, List<string> warnings)
    {
        var authoritative = new Dictionary<(string Label, string Key), GraphImport.Node>();
        var idMap = new Dictionary<string, string>();
        var survivors = new List<GraphImport.Node>();

        foreach (var node in graph.Nodes)
        {
            var key = FoldKey(node);

            if (key != null && authoritative.TryGetValue(key.Value, out var survivor))
            {
                idMap[node.Id] = survivor.Id;
                warnings.Add($"Merged '{DisplayName(node)}' into '{DisplayName(survivor)}' ({survivor.Label}).");

                foreach (var kv in node.Properties)
                {
                    if (!survivor.Properties.TryGetValue(kv.Key, out var existing))
                        survivor.Properties[kv.Key] = kv.Value;
                    else if (existing != kv.Value)
                        warnings.Add($"Kept '{DisplayName(survivor)}' {kv.Key}='{existing}'; discarded conflicting '{kv.Value}'.");
                }

                continue;
            }

            if (key != null)
                authoritative[key.Value] = node;

            survivors.Add(node);
        }

        if (idMap.Count == 0)
            return graph;

        //Rebuild so the graph's id index holds only survivors — a stale index entry for a folded id
        //would hand later GetOrAdd callers a node that is no longer in the graph.
        var rebuilt = new GraphImport.Graph();

        foreach (var node in survivors)
        {
            var copy = rebuilt.GetOrAdd(node.Id);
            copy.Label = node.Label;

            foreach (var kv in node.Properties)
                copy.Properties[kv.Key] = kv.Value;
        }

        var triples = new HashSet<string>();
        int collapsed = 0;

        foreach (var edge in graph.Edges)
        {
            var remapped = new GraphImport.Edge
            {
                Source = Remap(idMap, edge.Source),
                Target = Remap(idMap, edge.Target),
                Label = edge.Label
            };

            foreach (var kv in edge.Properties)
                remapped.Properties[kv.Key] = kv.Value;

            if (!triples.Add(TripleKey(remapped)))
            {
                collapsed++;
                continue;
            }

            rebuilt.Edges.Add(remapped);
        }

        if (collapsed > 0)
            warnings.Add($"Removed {collapsed} edge(s) that became duplicates after merging.");

        return rebuilt;
    }

    //── Helpers ─────────────────────────────────────────────────────────

    //The fold key for a node — its label plus the normalized name (else id) — or null when the
    //normalized form is empty, which means "never fold this one".
    private static (string Label, string Key)? FoldKey(GraphImport.Node node)
    {
        string candidate;

        if (node.Properties.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            candidate = name;
        else
            candidate = node.Id;

        var normalized = NormalizeEntityKey(candidate);

        if (normalized.Length == 0)
            return null;

        return (node.Label, normalized);
    }

    private static string DisplayName(GraphImport.Node node)
    {
        if (node.Properties.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return node.Id;
    }

    private static string Remap(Dictionary<string, string> idMap, string id)
    {
        if (idMap.TryGetValue(id, out var mapped))
            return mapped;

        return id;
    }

    private static string TripleKey(GraphImport.Edge edge)
    {
        return $"{edge.Source}\u0001{edge.Target}\u0001{edge.Label}";
    }

    private static int EnsureEndpoint(GraphImport.Graph graph, HashSet<string> seenIds, string id)
    {
        if (seenIds.Contains(id))
            return 0;

        seenIds.Add(id);
        var node = graph.GetOrAdd(id);
        node.Label = "Entity";

        return 1;
    }

    private static string ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();

        return null;
    }

    ///<summary>
    ///Lifts the annotations the model is asked for as top-level fields — the source sentence and the
    ///confidence — into properties. Read from the top level because that is where the prompt puts them,
    ///and skipped when already present so a model that nests them inside "properties" also works.
    ///</summary>
    private static void ReadAnnotations(JsonElement el, Dictionary<string, string> into)
    {
        foreach (var field in new[] { SourceProperty, ConfidenceProperty })
        {
            if (into.ContainsKey(field) || !el.TryGetProperty(field, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                into[field] = value.GetString();
            else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                into[field] = value.GetRawText();
        }
    }

    ///<summary>What locating the provenance quotes found.</summary>
    public readonly record struct ProvenanceLocations(int Located, int NotFound);

    ///<summary>
    ///Finds where each quoted sentence sits in the source text and records the offset, so a reviewer can
    ///go straight to it in a document too long to scan — the sentence alone says what was claimed, not
    ///where to check it.
    ///
    ///It is found rather than asked for: a model cannot count characters, and one asked to will invent a
    ///number that looks right. Which makes this a check as much as a lookup — a quote that is nowhere in
    ///the text was not taken from it, and the count of those is worth telling the reviewer.
    ///</summary>
    public static ProvenanceLocations LocateProvenance(GraphImport.Graph graph, string sourceText)
    {
        int located = 0;
        int notFound = 0;

        if (graph == null || string.IsNullOrEmpty(sourceText))
            return new ProvenanceLocations(0, 0);

        //Whitespace is where a quote and its source most often differ — a model reflows a line break into
        //a space — so the fallback search is over a flattened copy, with a map back to the real offsets.
        var flattened = Flatten(sourceText, out var offsets);

        foreach (var properties in graph.Nodes.Select(n => n.Properties).Concat(graph.Edges.Select(e => e.Properties)))
        {
            if (properties == null || !properties.TryGetValue(SourceProperty, out var quote) || string.IsNullOrWhiteSpace(quote))
                continue;

            int at = Locate(sourceText, flattened, offsets, quote);

            if (at < 0)
            {
                properties.Remove(SourceOffsetProperty);
                notFound++;

                continue;
            }

            properties[SourceOffsetProperty] = at.ToString(CultureInfo.InvariantCulture);
            located++;
        }

        return new ProvenanceLocations(located, notFound);
    }

    //Where the quote starts in the source, or -1. Tried verbatim first, then with both sides flattened.
    private static int Locate(string sourceText, string flattened, List<int> offsets, string quote)
    {
        var trimmed = quote.Trim();

        int at = sourceText.IndexOf(trimmed, StringComparison.Ordinal);

        if (at >= 0)
            return at;

        var flatQuote = Flatten(trimmed, out _);

        if (flatQuote.Length == 0)
            return -1;

        int flatAt = flattened.IndexOf(flatQuote, StringComparison.Ordinal);

        if (flatAt < 0)
            return -1;

        return offsets[flatAt];
    }

    //The text with every run of whitespace collapsed to one space, plus each kept character's offset in
    //the original — so a match in the flattened copy still points at the real place.
    private static string Flatten(string text, out List<int> offsets)
    {
        var sb = new StringBuilder(text.Length);
        offsets = new List<int>(text.Length);

        bool inWhitespace = false;

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (inWhitespace || sb.Length == 0)
                    continue;

                inWhitespace = true;
                sb.Append(' ');
                offsets.Add(i);

                continue;
            }

            inWhitespace = false;
            sb.Append(text[i]);
            offsets.Add(i);
        }

        //A trailing space would let a quote match one character past where it ends.
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
            offsets.RemoveAt(offsets.Count - 1);
        }

        return sb.ToString();
    }

    ///<summary>
    ///Splits text into pieces small enough to extract one at a time.
    ///
    ///A long document cannot be handed over whole: the answer would run past the model's output cap and
    ///arrive as truncated JSON, which the parser can only reject. Splitting is the difference between
    ///extracting a report and being told to shorten it. Pieces break at a paragraph where they can and a
    ///sentence otherwise, because a chunk that ends mid-sentence takes the relationship in it with it.
    ///</summary>
    public static List<string> SplitIntoChunks(string text, int maxChars)
    {
        var chunks = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        var remaining = text.Trim();

        if (maxChars <= 0 || remaining.Length <= maxChars)
        {
            chunks.Add(remaining);

            return chunks;
        }

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars)
            {
                chunks.Add(remaining);

                break;
            }

            int cut = BreakBefore(remaining, maxChars);
            chunks.Add(remaining.Substring(0, cut).Trim());
            remaining = remaining.Substring(cut).Trim();
        }

        return chunks;
    }

    //The last paragraph break within the limit, else the last sentence end, else the limit itself.
    private static int BreakBefore(string text, int limit)
    {
        int paragraph = text.LastIndexOf("\n\n", limit, StringComparison.Ordinal);

        //Not so early that a chunk is mostly empty — a document of one long paragraph would otherwise
        //split every time at its only blank line.
        if (paragraph > limit / 2)
            return paragraph;

        int sentence = -1;

        foreach (var terminator in new[] { ". ", ".\n", "! ", "? " })
        {
            int at = text.LastIndexOf(terminator, limit - 1, StringComparison.Ordinal);

            if (at > sentence)
                sentence = at;
        }

        if (sentence > limit / 2)
            return sentence + 1;

        return limit;
    }

    ///<summary>
    ///Drops entities nothing connects to. A single-shot extraction reliably produces a few — an entity
    ///mentioned once in passing, or one whose only relationship was dropped as malformed — and they are
    ///noise in the committed graph. Returns how many went.
    ///</summary>
    public static int DropOrphans(GraphImport.Graph graph)
    {
        if (graph == null || graph.Nodes.Count == 0)
            return 0;

        var connected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            connected.Add(edge.Source);
            connected.Add(edge.Target);
        }

        var orphans = graph.Nodes.Where(n => !connected.Contains(n.Id)).ToList();

        foreach (var orphan in orphans)
            graph.Nodes.Remove(orphan);

        return orphans.Count;
    }

    ///<summary>
    ///Makes relationship types consistent. A model asked twice will write <c>works at</c>, <c>works_at</c>
    ///and <c>WorksAt</c> for one relationship, which the graph then shows as three — the edge-label
    ///counterpart of the entity fold.
    ///
    ///Schema labels win outright: when a type matches one the connected database already uses, it snaps to
    ///that exact spelling rather than to this method's idea of a good one, so grounding is never undone.
    ///Everything else becomes camelCase. Returns how many edges were changed.
    ///</summary>
    public static int NormalizeRelationshipTypes(GraphImport.Graph graph, IEnumerable<string> schemaEdgeLabels = null)
    {
        if (graph == null || graph.Edges.Count == 0)
            return 0;

        //Keyed by the label stripped to its letters and digits, so every spelling of one relationship meets.
        var schema = new Dictionary<string, string>(StringComparer.Ordinal);

        if (schemaEdgeLabels != null)
            foreach (var label in schemaEdgeLabels)
                if (!string.IsNullOrWhiteSpace(label))
                    schema[FoldLabelKey(label)] = label;

        //Among the model's own spellings, the most common wins; ties go to the first seen, so the result
        //does not depend on dictionary ordering.
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var order = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var edge in graph.Edges)
        {
            var key = FoldLabelKey(edge.Label);

            if (key.Length == 0)
                continue;

            if (!counts.TryGetValue(key, out var spellings))
            {
                spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                counts[key] = spellings;
                order[key] = new List<string>();
            }

            if (!spellings.ContainsKey(edge.Label))
            {
                spellings[edge.Label] = 0;
                order[key].Add(edge.Label);
            }

            spellings[edge.Label]++;
        }

        int changed = 0;

        foreach (var edge in graph.Edges)
        {
            var key = FoldLabelKey(edge.Label);

            if (key.Length == 0)
                continue;

            string chosen;

            if (schema.TryGetValue(key, out var schemaLabel))
                chosen = schemaLabel;
            else
                chosen = CamelCase(PreferredSpelling(counts[key], order[key]));

            if (chosen != edge.Label)
            {
                edge.Label = chosen;
                changed++;
            }
        }

        return changed;
    }

    private static string PreferredSpelling(Dictionary<string, int> spellings, List<string> order)
    {
        var best = order[0];

        foreach (var spelling in order)
            if (spellings[spelling] > spellings[best])
                best = spelling;

        return best;
    }

    //A label reduced to its letters and digits, lowercased — what "works at", "works_at" and "WorksAt"
    //have in common.
    private static string FoldLabelKey(string label)
    {
        var sb = new StringBuilder();

        foreach (var c in label ?? "")
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));

        return sb.ToString();
    }

    ///<summary>Rewrites a label as camelCase — "works at" and "WORKS_AT" both become "worksAt".</summary>
    public static string CamelCase(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return label;

        var words = new List<string>();
        var word = new StringBuilder();

        //Split on separators, and also where an existing camel hump starts, so "worksAt" survives intact.
        for (int i = 0; i < label.Length; i++)
        {
            var c = label[i];

            if (!char.IsLetterOrDigit(c))
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }

                continue;
            }

            bool humpStart = char.IsUpper(c) && i > 0 && char.IsLower(label[i - 1]);

            if (humpStart && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }

            word.Append(c);
        }

        if (word.Length > 0)
            words.Add(word.ToString());

        if (words.Count == 0)
            return label;

        var sb = new StringBuilder(words[0].ToLowerInvariant());

        for (int i = 1; i < words.Count; i++)
        {
            var next = words[i].ToLowerInvariant();
            sb.Append(char.ToUpperInvariant(next[0]));

            if (next.Length > 1)
                sb.Append(next.Substring(1));
        }

        return sb.ToString();
    }

    ///<summary>
    ///Folds a gleaning answer into the first generation: entities the model already emitted keep what they
    ///had, genuinely new ones are added, and a relationship already present is not repeated. Returns how
    ///many nodes and edges the second pass actually contributed.
    ///</summary>
    public static (int Nodes, int Edges) MergeGeneration(GraphImport.Graph into, GraphImport.Graph addition)
    {
        if (into == null || addition == null)
            return (0, 0);

        var existingIds = new HashSet<string>(into.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        int newNodes = 0;

        foreach (var node in addition.Nodes)
        {
            if (existingIds.Contains(node.Id))
            {
                //Already known: the first pass stands, and this one only fills gaps.
                var existing = into.Nodes.First(n => n.Id == node.Id);

                foreach (var pair in node.Properties)
                    if (!existing.Properties.ContainsKey(pair.Key))
                        existing.Properties[pair.Key] = pair.Value;

                continue;
            }

            var added = into.GetOrAdd(node.Id);
            added.Label = node.Label;

            foreach (var pair in node.Properties)
                added.Properties[pair.Key] = pair.Value;

            existingIds.Add(node.Id);
            newNodes++;
        }

        var existingEdges = new HashSet<string>(into.Edges.Select(EdgeKey), StringComparer.Ordinal);
        int newEdges = 0;

        foreach (var edge in addition.Edges)
        {
            if (!existingEdges.Add(EdgeKey(edge)))
                continue;

            into.Edges.Add(edge);
            newEdges++;
        }

        return (newNodes, newEdges);
    }

    private static string EdgeKey(GraphImport.Edge edge)
    {
        return $"{edge.Source}{edge.Label}{edge.Target}";
    }

    private static void ReadProperties(JsonElement el, Dictionary<string, string> into)
    {
        if (!el.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return;

        foreach (var p in props.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (p.Value.ValueKind == JsonValueKind.String)
                into[p.Name] = p.Value.GetString();
            else
                into[p.Name] = p.Value.GetRawText();
        }
    }

    private static void MergeMissingProperties(GraphImport.Node node, JsonElement el)
    {
        var extra = new Dictionary<string, string>();
        ReadProperties(el, extra);

        foreach (var kv in extra)
        {
            if (!node.Properties.ContainsKey(kv.Key))
                node.Properties[kv.Key] = kv.Value;
        }
    }
}
