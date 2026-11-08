using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>What a proposed graph's card shows, for the mode chosen: the script it stages, and what the script does.</summary>
public sealed class GraphProposalPreview
{
    ///<summary>The queries using it stages, empty when nothing in it is new, and null when the language cannot write it.</summary>
    public string Script { get; init; }

    ///<summary>How many entities and relationships, and in Merge how many fold into what is drawn.</summary>
    public string Counts { get; init; }

    ///<summary>The labels of what it adds, the most used first, or null for none.</summary>
    public string Labels { get; init; }

    ///<summary>Every repair and every fold, each said: a preview that hides one is not a review.</summary>
    public List<string> Warnings { get; init; } = new();

    ///<summary>Why the script could not be composed, or null.</summary>
    public string Error { get; init; }
}

///<summary>
///A knowledge graph the model proposes in Ask AI (2026-11-02, the user's choice of folding knowledge-graph generation into
///the one AI window): read from the call's arguments with every repair the generation window made, and previewed for the mode
///the person picks. What the window did between the model's answer and its Use button lives here, so the call's answer to
///the model and the card the person sees say the same thing about the same graph.
///</summary>
public static class GraphProposal
{
    ///<summary>
    ///The graph a call proposed, with its repairs: the parser's (fences, duplicate ids, a missing end, a missing name, the
    ///caps, entities folded within the answer), relationship names made consistent against the graph's own, and each quoted
    ///sentence looked for in the conversation's text, where one that is not there is named.
    ///</summary>
    ///<param name="sourceText">What the conversation holds as text (<see cref="AiChat.SourceText"/>), which a quote is looked for in.</param>
    ///<param name="filesNotRead">Whether the conversation sent a PDF or a picture, whose words are not in <paramref name="sourceText"/>.</param>
    public static KgParseResult Read(string argumentsJson, IEnumerable<string> schemaEdgeLabels, string sourceText, bool filesNotRead)
    {
        var parsed = KgGraphParser.Parse(argumentsJson);

        if (parsed.IsError)
            return parsed;

        var warnings = new List<string>(parsed.Warnings);
        var normalized = KgGraphParser.NormalizeRelationshipTypes(parsed.Graph, schemaEdgeLabels);

        if (normalized > 0)
            warnings.Add($"Made {normalized} relationship type(s) consistent.");

        var located = KgGraphParser.LocateProvenance(parsed.Graph, sourceText ?? "");

        if (located.NotFound > 0 && filesNotRead)
            warnings.Add($"{located.NotFound} quoted sentence(s) are not in the conversation's text. A PDF's or a picture's words are not searched, so check those claims against the file.");
        else if (located.NotFound > 0)
            warnings.Add($"{located.NotFound} quoted sentence(s) are not in the conversation's text. Check those claims.");

        return KgParseResult.Success(parsed.Graph, warnings);
    }

    ///<summary>
    ///What using the graph would do, for the mode chosen. Replace previews the whole graph; Merge folds it against what is
    ///drawn first, and against what the database holds when that was looked up, so an entity that folds is not counted as
    ///new and the fold is said. Dropping unconnected entities works on a copy, so it can be undone by unticking it.
    ///</summary>
    public static GraphProposalPreview Preview(
        GraphImport.Graph proposed,
        IReadOnlyList<string> repairs,
        bool merge,
        bool dropOrphans,
        GraphImport.Graph canvas,
        GraphImport.Graph databaseMatches,
        IGraphQueryBuilder builder)
    {
        var warnings = new List<string>(repairs);
        var graph = proposed;

        if (dropOrphans)
        {
            graph = proposed.Clone();
            var dropped = KgGraphParser.DropOrphans(graph);

            if (dropped > 0)
                warnings.Add($"Dropped {dropped} unconnected entity(s).");
        }

        //Where the language holds no property on a relationship (SPARQL, GUN), the relationships are still written and their
        //properties are not, which is worth a word: with quotes and confidence asked for, that is a property on every one.
        if (builder is { WritesEdgeProperties: false })
        {
            var annotated = graph.Edges.Count(edge => edge.Properties.Count > 0);

            if (annotated > 0)
                warnings.Add($"{annotated} relationship(s) carry properties this language cannot write; the relationships themselves are in the script.");
        }

        //An engine can refuse to compose these at all (Dgraph cannot correlate a proposed graph's ids across mutations), and
        //the card says so rather than failing.
        try
        {
            if (!merge)
                return new GraphProposalPreview
                {
                    Script = GraphImport.ToQueries(graph, builder),
                    Counts = $"{graph.Nodes.Count} node(s) · {graph.Edges.Count} edge(s)",
                    Labels = BreakdownOf(graph.Nodes),
                    Warnings = warnings
                };

            var against = canvas.Clone();

            //What the database holds is folded against beside what is drawn, and the drawing wins a tie by coming first: it
            //is what is in front of the person.
            if (databaseMatches != null)
            {
                against.Nodes.AddRange(databaseMatches.Nodes);

                if (databaseMatches.Nodes.Count > 0)
                    warnings.Add($"{databaseMatches.Nodes.Count} entity(s) are already in the database; new relationships attach to those. Relationships themselves are not checked against it.");
                else
                    warnings.Add("No entity here is in the database yet, by name.");
            }

            var folded = KgGraphParser.FoldAgainstCanvas(graph, against, builder);

            warnings.AddRange(folded.Warnings);

            return new GraphProposalPreview
            {
                Script = folded.DeltaGremlin,
                Counts = $"{folded.NewNodes} new node(s) · {folded.NewEdges} new edge(s) · {folded.FoldedNodes} merged into existing · {folded.PropertyUpdates} property update(s)",
                Labels = BreakdownOf(folded.AddedNodes),
                Warnings = warnings
            };
        }
        catch (NotSupportedException refused)
        {
            return new GraphProposalPreview { Warnings = warnings, Error = refused.Message };
        }
    }

    ///<summary>
    ///Whether there is a database to ask and a language to ask it in. An engine whose builder has no lookup by name (SPARQL,
    ///GUN, and anything added without one) is not offered the match rather than guessed at.
    ///</summary>
    public static bool CanMatchDatabase(IGraphQueryBuilder builder, Func<string, CancellationToken, Task<GraphDbResult>> runQuery)
    {
        return runQuery != null && builder?.VerticesByName("probe", new[] { "probe" }, 1) != null;
    }

    ///<summary>
    ///The entities the connected database already holds, asked for by label and name: one read per label, with every proposed
    ///name of that label in it, so a proposed "Acme" points at the Acme the database has rather than being written twice.
    ///Only entities: a relationship between two entities the graph holds cannot be asked for by name, and the preview says so.
    ///</summary>
    ///<param name="warnings">Told about a label the database could not be asked about.</param>
    public static async Task<GraphImport.Graph> DatabaseMatchesAsync(
        GraphImport.Graph proposed,
        IGraphQueryBuilder builder,
        Func<string, CancellationToken, Task<GraphDbResult>> runQuery,
        List<string> warnings,
        CancellationToken token)
    {
        var found = new GraphImport.Graph();

        foreach (var group in proposed.Nodes.GroupBy(node => node.Label))
        {
            var names = group.Select(EntityName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();

            if (names.Count == 0)
                continue;

            var answer = await runQuery(builder.VerticesByName(group.Key, names, KgGraphParser.MaxNodes), token);

            if (answer.IsError)
            {
                warnings.Add($"The database could not be asked about {group.Key} entities ({answer.Error}).");

                continue;
            }

            foreach (var row in GraphDataConverter.ToTable(answer.Data).Nodes)
            {
                var node = new GraphImport.Node { Id = row.Id, Label = row.Label };

                foreach (var property in row.Properties)
                    node.Properties[property.Key] = property.Value;

                found.Nodes.Add(node);
            }
        }

        return found;
    }

    ///<summary>What the call answers the model: what the person now sees, the repairs made to it, and that nothing happens until they use it.</summary>
    public static string AnswerFor(KgParseResult read)
    {
        var graph = read.Graph;
        var answer = $"The user sees this graph as a proposal: {graph.Nodes.Count} entities and {graph.Edges.Count} relationships";
        var labels = BreakdownOf(graph.Nodes);

        if (labels != null)
            answer += $" ({labels})";

        answer += ".";

        if (read.Warnings.Count > 0)
            answer += " The app repaired it: " + string.Join(" ", read.Warnings);

        return answer + " Nothing is drawn or written until they use it, in place of their drawing or merged into it. Say in a sentence or two what you extracted, without repeating the graph. Propose again only when they ask for a change, or for text you have not covered yet.";
    }

    //The name an entity is looked up by, which is the one the fold matches on: its name property, else its id.
    private static string EntityName(GraphImport.Node node)
    {
        if (node.Properties.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return node.Id;
    }

    private static string BreakdownOf(List<GraphImport.Node> nodes)
    {
        if (nodes.Count == 0)
            return null;

        return string.Join(" · ", nodes.GroupBy(node => node.Label)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} ×{group.Count()}"));
    }
}
