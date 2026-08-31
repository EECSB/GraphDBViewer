using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///Builds the system prompt that turns source text into a strict-JSON knowledge graph. Mirrors
///<see cref="NlQueryPrompt"/>: pure and static, and the schema grounding has three states —
///populated (constrain to the listed labels), read-but-empty (say so) and unknown (drop the
///constraint) — so an empty vocabulary never asserts a constraint over nothing. Must stay
///database-free; the caller passes the vocabulary in.
///</summary>
public static class KgPrompt
{
    ///<summary>
    ///The system prompt. Pass the connected schema's vocabulary to ground labels in it (schema-guided
    ///mode), or null for freeform extraction. The caps quoted to the model are
    ///<see cref="KgGraphParser.MaxNodes"/> / <see cref="KgGraphParser.MaxEdges"/> — the same constants
    ///the parser enforces, so the ask and the enforcement can't drift apart.
    ///</summary>
    public static string BuildSystemPrompt(SchemaVocabulary schema)
    {
        return BuildSystemPrompt(schema, false, false);
    }

    ///<param name="includeProvenance">
    ///Ask for the sentence each entity and relationship came from, stored as a
    ///<see cref="KgGraphParser.SourceProperty"/> property. It grounds review in the text — the reviewer can
    ///see *why* the model claimed something — at the cost of a longer answer.
    ///</param>
    ///<param name="includeConfidence">
    ///Ask for a 0–1 confidence per relationship. The viewer draws anything below
    ///<see cref="GraphDataConverter.LowConfidenceThreshold"/> in gray, so a guess looks like a guess.
    ///</param>
    public static string BuildSystemPrompt(SchemaVocabulary schema, bool includeProvenance, bool includeConfidence)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You extract a knowledge graph from the user's text.");
        sb.AppendLine("Output a single JSON object of exactly this shape:");
        sb.AppendLine(ShapeExample(includeProvenance, includeConfidence));
        sb.AppendLine("Rules:");
        sb.AppendLine("- Output ONLY the JSON. No explanation, no comments, no markdown code fences.");
        sb.AppendLine("- Give every node a short lowercase id and a human-readable \"name\" property.");
        sb.AppendLine("- Use canonical entity names: refer to each real-world entity by ONE name everywhere — resolve pronouns, abbreviations and variant spellings to it instead of emitting near-duplicate nodes.");
        sb.AppendLine($"- Extract at most {KgGraphParser.MaxNodes} nodes and {KgGraphParser.MaxEdges} edges; prefer the most important entities and relationships.");

        if (includeProvenance)
            sb.AppendLine($"- Give every node and every edge a \"{KgGraphParser.SourceProperty}\" field: the sentence from the text it came from, quoted verbatim and trimmed to one sentence.");

        if (includeConfidence)
            sb.AppendLine($"- Give every node and every edge a \"{KgGraphParser.ConfidenceProperty}\" field between 0 and 1: how certain the text is about that entity or relationship. State it plainly — 1 when the text says it outright, lower when you inferred it.");

        //Three states, mirroring NlQueryPrompt: a populated vocabulary pins the labels; a read-but-empty
        //graph says so; an unknown schema (freeform, or the schema was never read) just asks for
        //consistent invented labels — never "use only what's listed" with nothing listed.
        bool hasVocabulary = schema != null
            && (schema.VertexLabels is { Count: > 0 }
                || schema.EdgeLabels is { Count: > 0 }
                || schema.PropertyKeys is { Count: > 0 });

        if (hasVocabulary)
        {
            sb.AppendLine("- Use ONLY the vertex labels, edge labels and property keys listed below.");

            if (schema.VertexLabels is { Count: > 0 })
                sb.AppendLine($"\nVertex labels: {string.Join(", ", schema.VertexLabels)}");

            if (schema.EdgeLabels is { Count: > 0 })
                sb.AppendLine($"Edge labels: {string.Join(", ", schema.EdgeLabels)}");

            if (schema.PropertyKeys is { Count: > 0 })
                sb.AppendLine($"Property keys: {string.Join(", ", schema.PropertyKeys)}");
        }
        else if (schema != null)
            sb.AppendLine("- The connected graph is empty — it has no labels or property keys to match. Invent concise, consistent labels from the text itself.");
        else
            sb.AppendLine("- Invent concise, consistent labels from the text itself (e.g. Person, Company, worksAt).");

        return sb.ToString().Trim();
    }

    //The shape example grows only by what was asked for, so a model is never shown a field the run does
    //not want — the surest way to stop it emitting one anyway.
    private static string ShapeExample(bool includeProvenance, bool includeConfidence)
    {
        var node = @"{""id"":""acme"",""label"":""Company"",""properties"":{""name"":""Acme""}";
        var edge = @"{""source"":""alice"",""target"":""acme"",""label"":""worksAt"",""properties"":{}";

        if (includeProvenance)
        {
            node += $@",""{KgGraphParser.SourceProperty}"":""Alice works at Acme.""";
            edge += $@",""{KgGraphParser.SourceProperty}"":""Alice works at Acme.""";
        }

        if (includeConfidence)
        {
            node += $@",""{KgGraphParser.ConfidenceProperty}"":0.8";
            edge += $@",""{KgGraphParser.ConfidenceProperty}"":0.9";
        }

        return $@"{{""nodes"":[{node}}}],""edges"":[{edge}}}]}}";
    }

    ///<summary>
    ///The follow-up that asks the model what it missed — the "gleaning" pass. Extraction in one shot
    ///reliably leaves things behind, and simply asking again is the cheapest way to find them; the answer
    ///is merged into the first, so only genuinely new entities and relationships survive.
    ///</summary>
    public static string BuildGleaningPrompt(string previousJson)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You already extracted this knowledge graph from the text:");
        sb.AppendLine(previousJson);
        sb.AppendLine();
        sb.AppendLine("Now find what you MISSED. Output a JSON object of the same shape containing ONLY the entities and relationships that are not already above.");
        sb.AppendLine("Use the same ids for entities that already exist, so the new relationships attach to them.");
        sb.AppendLine("If nothing was missed, output {\"nodes\":[],\"edges\":[]}.");

        return sb.ToString().Trim();
    }
}
