using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a model is told about drawing a knowledge graph from text. Mirrors <see cref="NlQueryPrompt"/>: pure and static, and the
///labels have three states — the connected graph's own (use them where they fit), read but empty (say so) and unknown (invent
///them) — so an empty vocabulary is never asserted as one to keep to. Must stay database-free; the caller passes the vocabulary.
///
///Until 2026-11-02 this built the one-shot prompt of a window of its own, with a request to send unreadable JSON again and a
///second pass for what was missed. Generation is Ask AI's since, a tool in a conversation (<see cref="GraphProposalRunner"/>),
///where a graph that cannot be read is refused and sent again, and /missed asks for what was left out.
///</summary>
public static class KgPrompt
{
    ///<summary>
    ///About how much of a text one proposed graph covers, in characters: so a full-size graph for it fits the 8192-token answer
    ///the connections ship with, the reasoning behind the node and edge caps too.
    ///</summary>
    public const int PartChars = 6000;

    ///<summary>
    ///What Ask AI's model is told about the graph tool (<see cref="GraphProposalRunner"/>, 2026-11-02): when to propose, the
    ///extraction's rules, and what the person ticked of the knowledge-graph window's options, in that window's words: labels
    ///from the connected graph's own, the source sentence, confidence, a long text in pieces, and a second look for what was
    ///missed. Or, with the tool switched off in the picker, that it is. The caps quoted are the parser's own constants, so the
    ///ask and the enforcement cannot drift apart.
    ///</summary>
    public static void AppendProposalRules(StringBuilder sb, SchemaVocabulary schema, IReadOnlyCollection<string> toolsOff, KgExtractionOptions options)
    {
        var name = GraphProposalRunner.ProposeName;

        if (toolsOff.Contains(name))
        {
            sb.AppendLine($"- {name} is switched off in this conversation: the user turned it off in the window's tool picker. When they ask for a graph of a text, say so, and that it can be switched back on there.");

            return;
        }

        options ??= new KgExtractionOptions();

        sb.AppendLine($"- When the user asks for a graph of a text, what they typed, a file they sent or an article, extract it yourself and call {name} with it. They see it previewed and decide whether to use it; calling it changes nothing, and you never write a proposed graph to the database with a query.");
        sb.AppendLine("- In a proposed graph, refer to each real-world entity by ONE canonical name, resolving pronouns, abbreviations and variant spellings to it; give every node a short lowercase id and a human-readable \"name\" property; and name relationships as camelCase verbs, such as worksAt.");
        sb.AppendLine($"- Propose at most {KgGraphParser.MaxNodes} nodes and {KgGraphParser.MaxEdges} edges in one call; prefer the most important entities and relationships.");

        if (options.SplitLongText)
            sb.AppendLine($"- A text longer than about {PartChars:N0} characters is proposed in pieces of about that size, one call per piece, with the same ids for entities already proposed so the pieces merge.");

        //Three states, as the window's had them: a vocabulary to keep to, a graph read and found empty, and no grounding asked
        //for, which invents, so an empty vocabulary is never asserted as one to keep to.
        bool hasVocabulary = schema != null
            && (schema.VertexLabels is { Count: > 0 }
                || schema.EdgeLabels is { Count: > 0 }
                || schema.PropertyKeys is { Count: > 0 });

        if (options.GroundInSchema && hasVocabulary)
            sb.AppendLine("- In a proposed graph, use ONLY the connected graph's vertex labels, edge labels and property keys, listed below.");
        else if (options.GroundInSchema && schema != null)
            sb.AppendLine("- The connected graph is empty: it has no labels or property keys to match. Invent a proposed graph's labels from the text itself, concise and consistent.");
        else
            sb.AppendLine("- Invent a proposed graph's labels from the text itself, concise and consistent (Person, Company, worksAt).");

        if (options.SourceSentence)
            sb.AppendLine($"- Give every node and every edge a \"{KgGraphParser.SourceSentenceProperty}\" field: the sentence from the text it came from, quoted verbatim and trimmed to one sentence.");

        if (options.Confidence)
            sb.AppendLine($"- Give every node and every edge a \"{KgGraphParser.ConfidenceProperty}\" field between 0 and 1: how certain the text is about that entity or relationship. State it plainly: 1 when the text says it outright, lower when you inferred it.");

        if (options.AskAgain)
            sb.AppendLine($"- After each graph you propose, look at the text once more for what you missed, and call {name} again with only that, with the same ids for entities already proposed; when nothing was missed, say so instead.");
    }
}
