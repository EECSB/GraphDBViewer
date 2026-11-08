using System;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///Builds the system prompt that turns a natural-language request into a database query, grounded in the
///connected graph's schema, and cleans the model's reply. Pure and unit-tested; the schema comes from the
///same <see cref="SchemaVocabulary"/> the editor autocomplete already fetches.
///</summary>
public static class NlQueryPrompt
{
    ///<summary>Human-readable query-language name for the editor language id (gremlin/cypher/sparql).</summary>
    public static string LanguageDisplayName(string language)
    {
        if (language == "cypher")
            return "openCypher";
        else if (language == "sparql")
            return "SPARQL";
        else if (language == "aql")
            return "AQL";
        else if (language == "dql")
            return "DQL";

        return "Gremlin";
    }

    ///<summary>
    ///The system prompt: instruct the model to emit one query in the target language, using only the
    ///schema's labels/keys, with no prose or markdown. Schema sections are omitted when empty, and the
    ///"use only these" rule goes with them — see the three states below.
    ///</summary>
    public static string BuildSystemPrompt(string language, SchemaVocabulary schema, bool toolsAvailable = false)
    {
        var name = LanguageDisplayName(language);
        var sb = new StringBuilder();

        sb.AppendLine($"You generate {name} queries for a graph database.");
        sb.AppendLine($"Given the user's request, output a single {name} query that answers it.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY the query. No explanation, no comments, no markdown code fences.");

        AppendSchemaRules(sb, schema);
        AppendTraversalRules(sb);

        if (toolsAvailable)
            sb.AppendLine($"- You may call {QueryToolRunner.ReadToolName} to run read-only queries against the live graph to inspect the data and verify your query before answering. When you are confident, reply with ONLY the final query and no tool call.");

        AppendSchema(sb, schema);

        return sb.ToString().Trim();
    }

    ///<summary>
    ///What a step actually does, which is the mistake that looks most like an empty database.
    ///
    ///"A route from Portland to LA" was answered with a single out-step and came back with nothing — the
    ///route exists, but it runs through San Francisco, and one step only ever crosses one edge. So the
    ///query had asked whether the two cities are adjacent, which is a different question, and the empty
    ///answer to it was correct and useless.
    ///</summary>
    private static void AppendTraversalRules(StringBuilder sb)
    {
        sb.AppendLine("- A route, a path, a connection, or whether one thing can be reached from another is rarely one hop. A single traversal step crosses exactly one edge, so a query built from one step asks whether the two are directly joined — a different question, whose empty answer says nothing about whether a path exists. Use a repeating traversal that goes until it arrives, and bound it so it cannot wander forever.");
        sb.AppendLine("- Edge direction is how the data happened to be written, not what the question meant. If following edges one way finds nothing, follow them either way before concluding there is no connection.");
    }

    ///<summary>
    ///The rule about what the model may name, which depends on what is known about the schema.
    ///
    ///Three states, not two. A null vocabulary means the schema was never read — disconnected, or the
    ///queries failed. A vocabulary that was read but holds nothing means the graph really is empty. Both
    ///used to emit "use ONLY the labels listed below" with nothing listed, which constrains the model to
    ///the empty set and then tells it to answer anyway — an invitation to invent a schema and sound
    ///certain about it.
    ///</summary>
    private static void AppendSchemaRules(StringBuilder sb, SchemaVocabulary schema, bool canCheck = false)
    {
        bool hasVocabulary = schema != null
            && (schema.VertexLabels is { Count: > 0 }
                || schema.EdgeLabels is { Count: > 0 }
                || schema.PropertyKeys is { Count: > 0 });

        if (hasVocabulary)
        {
            sb.AppendLine("- Use ONLY the vertex labels, edge labels and property keys listed below.");
            sb.AppendLine("- If the request cannot be answered from this schema, output the closest valid query.");
        }
        else if (schema != null && canCheck)
            sb.AppendLine("- The last schema read came back empty, which usually means the graph holds no vertices — but it is a fact about that read, not a promise about the graph now. Check with a query before telling the user their graph is empty, and take labels from the request itself if it really is.");
        else if (schema != null)
            sb.AppendLine("- The graph is empty — it holds no vertices yet, so there are no labels or property keys to match. Take them from the request itself.");
        else
            sb.AppendLine("- The graph's schema is unknown, so no labels or property keys are given. Take them from the request itself and don't assume any others exist.");
    }

    ///<summary>The schema itself, listed for the model. Each section is omitted when it holds nothing.</summary>
    private static void AppendSchema(StringBuilder sb, SchemaVocabulary schema)
    {
        if (schema == null)
            return;

        if (schema.VertexLabels is { Count: > 0 })
            sb.AppendLine($"\nVertex labels: {string.Join(", ", schema.VertexLabels)}");

        if (schema.EdgeLabels is { Count: > 0 })
            sb.AppendLine($"Edge labels: {string.Join(", ", schema.EdgeLabels)}");

        if (schema.PropertyKeys is { Count: > 0 })
            sb.AppendLine($"Property keys: {string.Join(", ", schema.PropertyKeys)}");
    }

    ///<summary>
    ///The system prompt for a running conversation, rather than the one-shot above.
    ///
    ///The two differ on one point and it matters: the one-shot prompt forbids prose, because whatever
    ///comes back is dropped straight into the editor. Here a person is being talked to, so the model is
    ///asked to explain itself and to fence the query instead, which is what makes it extractable from a
    ///reply that is mostly words. Everything else — the schema, and the three states of knowing it — is
    ///the same, and is shared rather than restated.
    ///</summary>
    public static string BuildChatSystemPrompt(
        string language,
        SchemaVocabulary schema,
        bool toolsAvailable = false,
        ToolApprovalMode mode = ToolApprovalMode.Ask,
        IReadOnlyCollection<string> toolsOff = null,
        bool canLook = false,
        bool canPropose = false,
        KgExtractionOptions extraction = null)
    {
        var name = LanguageDisplayName(language);
        var sb = new StringBuilder();

        sb.AppendLine($"You are a {name} assistant for a graph database, talking with someone in a chat panel beside their query editor.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Answer conversationally, and briefly. You may ask a clarifying question instead of guessing.");
        sb.AppendLine($"- When you propose a query, put it in a fenced ```{language} block, on its own, with nothing else inside the fence. Put at most one such block in a reply, and put it last.");
        sb.AppendLine("- Refer to what was said earlier in the conversation rather than starting over each time.");

        AppendSchemaRules(sb, schema, toolsAvailable);
        AppendTraversalRules(sb);
        AppendToolRules(sb, toolsAvailable, mode, toolsOff ?? Array.Empty<string>());
        AppendLookRules(sb, canLook, mode, toolsOff ?? Array.Empty<string>());

        if (canPropose)
        {
            KgPrompt.AppendProposalRules(sb, schema, toolsOff ?? Array.Empty<string>(), extraction);
            AppendWikipediaRules(sb, mode, toolsOff ?? Array.Empty<string>());
        }

        AppendSchema(sb, schema);

        return sb.ToString().Trim();
    }

    ///<summary>
    ///The picture tool (<see cref="GraphPictureRunner"/>, 2026-11-02, GDS Viewer's look): what it shows and what it does
    ///not, so a model looks for shape and takes numbers from queries, and whether a look waits for the person.
    ///</summary>
    private static void AppendLookRules(StringBuilder sb, bool canLook, ToolApprovalMode mode, IReadOnlyCollection<string> toolsOff)
    {
        if (!canLook)
            return;

        if (toolsOff.Contains(GraphPictureRunner.LookName))
        {
            sb.AppendLine($"- {GraphPictureRunner.LookName} is switched off in this conversation: the user turned it off in the window's tool picker. Where a picture of the graph would have helped, say so, and that it can be switched back on there.");

            return;
        }

        sb.AppendLine($"- You may call {GraphPictureRunner.LookName} to see the graph as the user's view draws it now, in 2D or 3D, with its colors and layout. Use it to see the graph's shape, its clusters and what stands out; take counts, names and properties from queries rather than from the picture.");

        if (ApprovingToolRunner.NeedsApproval(mode, false))
            sb.AppendLine($"- A call to {GraphPictureRunner.LookName} is shown to the user and made only if they approve it.");
    }

    ///<summary>
    ///The Wikipedia tool (<see cref="WikipediaRunner"/>, 2026-11-02, offered with the graph tool): an article the person names
    ///is read rather than remembered, and whether a read waits for them.
    ///</summary>
    private static void AppendWikipediaRules(StringBuilder sb, ToolApprovalMode mode, IReadOnlyCollection<string> toolsOff)
    {
        if (toolsOff.Contains(WikipediaRunner.ReadName))
        {
            sb.AppendLine($"- {WikipediaRunner.ReadName} is switched off in this conversation: the user turned it off in the window's tool picker. When they name a Wikipedia article, say that it can be switched back on there, rather than working from memory.");

            return;
        }

        sb.AppendLine($"- When the user names a Wikipedia article, by its title or a link, call {WikipediaRunner.ReadName} to read it rather than working from what you remember of it.");

        if (ApprovingToolRunner.NeedsApproval(mode, false))
            sb.AppendLine($"- A call to {WikipediaRunner.ReadName} is shown to the user and made only if they approve it.");
    }

    ///<summary>
    ///What the model is told about its own reach, which is not fixed: the same panel runs it on a leash,
    ///off one, or off one with the graph open to it. A model that thinks every call will be vetted writes
    ///as though nothing it asks for can land, and one that thinks nothing is watched writes as though
    ///everything it asks for is free — so the mode in force is stated rather than implied.
    ///</summary>
    private static void AppendToolRules(StringBuilder sb, bool toolsAvailable, ToolApprovalMode mode, IReadOnlyCollection<string> toolsOff)
    {
        if (!toolsAvailable)
            return;

        if (toolsOff.Contains(QueryToolRunner.ReadToolName))
            sb.AppendLine($"- {QueryToolRunner.ReadToolName} is switched off in this conversation: the user turned it off in the window's tool picker. Where you would have run a query to look, say what you would have looked for, and that it can be switched back on there.");
        else
        {
            sb.AppendLine($"- You may call {QueryToolRunner.ReadToolName} to run a read-only query against the live graph and see the result.");

            if (ApprovingToolRunner.NeedsApproval(mode, false))
                sb.AppendLine("- Every read call is shown to the user and run only if they approve it, so explain what you are checking before you call it, and expect that they may decline.");
            else
                sb.AppendLine("- Read calls run immediately, without anyone approving them, and the user sees each one. Keep them small and targeted, and say what you learned from them.");

            sb.AppendLine("- An empty result is a lead, not an answer. What is stored rarely matches how somebody typed it, so before reporting that something is not there: try other capitalizations, match on part of a value instead of all of it, expand or contract abbreviations the user is likely to have used (\"LA\" for \"Los Angeles\", \"NYC\" for \"New York\"), and list the values that property actually holds so you can see what you are working with. Only say the graph does not contain something once you have looked at what it does contain.");
            sb.AppendLine("- Do those follow-up calls yourself rather than asking the user whether to. Asking them to guess at capitalization is asking them to do the looking you are holding the tool for.");
        }

        //With the write tool switched off the conversation only reads, which is said in place of "never say you cannot
        //make changes": it is true here, by the user's own choice.
        if (toolsOff.Contains(QueryToolRunner.WriteToolName))
        {
            sb.AppendLine($"- {QueryToolRunner.WriteToolName} is switched off in this conversation: the user turned it off in the window's tool picker, so this conversation only reads. When they ask for a change, propose the query in a fenced block for them to run, and say that the tool can be switched back on there.");

            return;
        }

        sb.AppendLine($"- You may call {QueryToolRunner.WriteToolName} to CHANGE the graph. This cannot be undone. Only write when the user has asked for the data to change, make the smallest change that does what they asked, read first when you are unsure what is already there, and never delete more than was asked for.");

        if (ApprovingToolRunner.NeedsApproval(mode, true))
            sb.AppendLine("- A write call is shown to the user and run only if they approve it. Calling the tool is how you ask them, so when they want something changed, call it — never answer that you are unable to make changes, and never tell them to run it themselves instead.");
        else
            sb.AppendLine("- Write calls run immediately, without anyone approving them. After writing, say plainly what you changed.");
    }

    ///<summary>
    ///The query a chat reply is proposing, or null when it proposed none. A fenced block is the agreed
    ///signal, so a reply that is only prose yields nothing to run rather than the prose itself.
    ///</summary>
    public static string ExtractProposedQuery(string reply)
    {
        return LlmText.ExtractFencedBlock(reply);
    }

    ///<summary>
    ///Strips a surrounding markdown code fence (```lang … ```) and trims, so a fenced reply still drops
    ///cleanly into the editor. The logic lives in <see cref="LlmText.StripMarkdownFences"/>, shared with
    ///the knowledge-graph parser.
    ///</summary>
    public static string CleanQuery(string modelOutput)
    {
        return LlmText.StripMarkdownFences(modelOutput);
    }
}
