using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///Pure Gremlin query-string builder. No execution — just string generation.
///Centralizes escaping, ID formatting, and all the query shapes used by the UI.
///</summary>
public static class GremlinQueryBuilder
{
    //── Low-level helpers ───────────────────────────────────────────────

    ///<summary>Escapes a value for use inside a single-quoted Gremlin string literal.</summary>
    public static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
    }

    ///<summary>Numeric IDs are emitted bare; everything else is single-quoted and escaped.</summary>
    public static string FormatId(string id)
    {
        if (long.TryParse(id, out _))
            return id;
        else
            return $"'{Escape(id)}'";
    }

    ///<summary>
    ///Formats an id literal using the element's GraphSON id type, so it matches the stored element.
    ///Gremlin is type-strict on edge ids: an edge whose id is a Long must be written g.E(123L) — a
    ///bare 123 is an Integer and matches nothing. Int32 ids stay bare; unknown / non-numeric types
    ///fall back to the type-less heuristic (bare number, else quoted string). Vertex lookups are
    ///lenient about numeric width, so this mainly matters for edges.
    ///</summary>
    public static string FormatId(string id, string graphSonType)
    {
        if (id == null)
            return FormatId(id);

        if ((graphSonType == "g:Int64" || graphSonType == "g:Long") && long.TryParse(id, out _))
            return id + "L";

        if ((graphSonType == "g:Int32" || graphSonType == "g:Integer") && long.TryParse(id, out _))
            return id;

        return FormatId(id);
    }

    //── Element traversal prefixes ──────────────────────────────────────

    ///<summary>Returns "g.V(id)" for nodes or "g.E(id)" for edges.</summary>
    public static string ElementPrefix(string type, string id)
    {
        if (type == "node")
            return $"g.V({FormatId(id)})";
        else
            return $"g.E({FormatId(id)})";
    }

    ///<summary>Like <see cref="ElementPrefix(string, string)"/> but formats the id with its GraphSON type.</summary>
    public static string ElementPrefix(string type, string id, string idType)
    {
        if (type == "node")
            return $"g.V({FormatId(id, idType)})";
        else
            return $"g.E({FormatId(id, idType)})";
    }

    //── Creation ────────────────────────────────────────────────────────

    public static string AddVertex(string label)
    {
        return $"g.addV('{Escape((label ?? string.Empty).Trim())}')";
    }

    ///<summary>
    ///addV with a single "name" property — used by quick-add to give a fresh vertex a temporary,
    ///editable display name. The label itself is immutable once committed, so the renameable name
    ///lives in a property instead.
    ///</summary>
    public static string AddVertexWithName(string label, string name)
    {
        return $"g.addV('{Escape((label ?? string.Empty).Trim())}').property('name', '{Escape(name)}')";
    }

    ///<summary>
    ///addV with a name property plus the reserved gdbvX / gdbvY position properties — used by
    ///click-to-place so the new vertex is pinned exactly where the user clicked (and the position
    ///persists once committed).
    ///</summary>
    public static string AddVertexWithNameAt(string label, string name, double x, double y)
    {
        var xs = x.ToString(CultureInfo.InvariantCulture);
        var ys = y.ToString(CultureInfo.InvariantCulture);

        return $"g.addV('{Escape((label ?? string.Empty).Trim())}').property('name', '{Escape(name)}').property('{GdbvKeys.X}', '{xs}').property('{GdbvKeys.Y}', '{ys}')";
    }

    ///<summary>
    ///Builds an addE query going from sourceId to targetId.
    ///Uses the anonymous traversal __.V(...) inside .to() as required by TinkerPop.
    ///</summary>
    public static string AddEdge(string sourceId, string label, string targetId)
    {
        return $"g.V({FormatId(sourceId)}).addE('{Escape((label ?? string.Empty).Trim())}').to(__.V({FormatId(targetId)}))";
    }

    ///<summary>addV with an explicit id (via T.id) and one .property() per entry — used by graph-text import.</summary>
    public static string AddVertexWithProperties(string label, string id, IReadOnlyDictionary<string, string> properties)
    {
        var sb = new StringBuilder();
        sb.Append($"g.addV('{Escape((label ?? string.Empty).Trim())}').property(T.id, {FormatId(id)})");

        foreach (var kv in properties)
            sb.Append($".property('{Escape(kv.Key)}', '{Escape(kv.Value)}')");

        return sb.ToString();
    }

    ///<summary>addE from sourceId to targetId with edge properties appended — used by graph-text import.</summary>
    public static string AddEdgeWithProperties(string sourceId, string label, string targetId, IReadOnlyDictionary<string, string> properties)
    {
        var sb = new StringBuilder();
        sb.Append(AddEdge(sourceId, label, targetId));

        foreach (var kv in properties)
            sb.Append($".property('{Escape(kv.Key)}', '{Escape(kv.Value)}')");

        return sb.ToString();
    }

    //── Deletion ────────────────────────────────────────────────────────

    public static string DropVertex(string id)
    {
        return $"g.V({FormatId(id)}).drop()";
    }

    public static string DropEdge(string id)
    {
        return $"g.E({FormatId(id)}).drop()";
    }

    ///<summary>Drops the element by type — "node" → drop vertex, otherwise drop edge.</summary>
    public static string DropElement(string type, string id)
    {
        if (type == "node")
            return DropVertex(id);
        else
            return DropEdge(id);
    }

    public static string DropVertex(string id, string idType)
    {
        return $"g.V({FormatId(id, idType)}).drop()";
    }

    public static string DropEdge(string id, string idType)
    {
        return $"g.E({FormatId(id, idType)}).drop()";
    }

    ///<summary>Drops the element by type, formatting the id with its GraphSON type.</summary>
    public static string DropElement(string type, string id, string idType)
    {
        if (type == "node")
            return DropVertex(id, idType);
        else
            return DropEdge(id, idType);
    }

    //── Property mutation ───────────────────────────────────────────────

    public static string SetProperty(string type, string id, string key, string value)
    {
        return $"{ElementPrefix(type, id)}.property('{Escape(key)}', '{Escape(value)}')";
    }

    public static string DropProperty(string type, string id, string key)
    {
        return $"{ElementPrefix(type, id)}.properties('{Escape(key)}').drop()";
    }

    public static string SetProperty(string type, string id, string key, string value, string idType)
    {
        return $"{ElementPrefix(type, id, idType)}.property('{Escape(key)}', '{Escape(value)}')";
    }

    public static string DropProperty(string type, string id, string key, string idType)
    {
        return $"{ElementPrefix(type, id, idType)}.properties('{Escape(key)}').drop()";
    }

    ///<summary>Drops every viewer-reserved (gdbv*) property — layout position, per-node style, image /
    ///3D model — from all vertices and all edges, so the user can strip viewer-only metadata from the
    ///graph. Vertex drop on the first line, edge drop on the second.</summary>
    public static string DropAllViewerProperties()
    {
        var keys = "'" + string.Join("','", GdbvKeys.All) + "'";
        return $@"g.V().properties({keys}).drop()
g.E().properties({keys}).drop()";
    }

    //── Graph loading ──────────────────────────────────────────────────

    ///<summary>Cheap query used to verify the connection.</summary>
    public const string TestConnection = "g.V().limit(1)";

    public static string LimitedVertices(int limit)
    {
        return $"g.V().limit({limit})";
    }

    ///<summary>
    ///Full visualization payload: every vertex (including isolated, edgeless ones)
    ///followed by every edge. The earlier bothE()-based traversal silently dropped
    ///vertices with no edges; folding the vertices and unioning their out-edges keeps them.
    ///Pass a limit to cap how many root vertices are walked, or null for the whole graph.
    ///</summary>
    public static string FullGraph(int? limit)
    {
        if (limit.HasValue)
            return $"g.V().limit({limit.Value}).fold().union(__.unfold(), __.unfold().outE())";
        else
            return "g.V().fold().union(__.unfold(), __.unfold().outE())";
    }

    ///<summary>A vertex's incident edges and neighbor vertices — used for incremental (double-click) expansion.</summary>
    ///<summary>A node's incident edges and neighbor vertices, capped at `limit` edges (limit ≤ 0 = uncapped).</summary>
    public static string Neighbors(string id, int limit)
    {
        if (limit <= 0)
            return $"g.V({FormatId(id)}).union(__.bothE(), __.bothE().otherV())";

        return $"g.V({FormatId(id)}).bothE().limit({limit}).union(__.identity(), __.otherV())";
    }

    //── Schema ──────────────────────────────────────────────────────────

    ///<summary>Vertex labels with counts: { label: count }.</summary>
    public const string SchemaVertexLabels = "g.V().groupCount().by(label())";

    ///<summary>Edge labels with counts: { label: count }.</summary>
    public const string SchemaEdgeLabels = "g.E().groupCount().by(label())";

    ///<summary>Distinct property keys per vertex label: { label: [keys] }.</summary>
    public const string SchemaVertexKeys = "g.V().group().by(label()).by(properties().key().dedup().fold())";

    ///<summary>Distinct (outLabel, edgeLabel, inLabel) relationship triples.</summary>
    public const string SchemaEdgeTriples = "g.E().project('out','edge','in').by(outV().label()).by(label()).by(inV().label()).dedup()";

    //── Inspection ─────────────────────────────────────────────────────

    //Coalesce expression used to derive a human-readable label for a vertex.
    private const string DisplayLabelCoalesce = "coalesce(values('name','Name','title','Title'), label())";

    public static string VertexDisplayLabel(string vertexId)
    {
        return $"g.V({FormatId(vertexId)}).{DisplayLabelCoalesce}";
    }

    ///<summary>
    ///Lists inbound edges for a vertex, projecting (edge id, edge label, source-vertex id, source-vertex display label).
    ///</summary>
    public static string InEdges(string vertexId)
    {
        return $@"g.V({FormatId(vertexId)}).inE().as('e').outV().as('v')
            .project('eId','eLabel','vId','vLabel')
            .by(select('e').id()).by(select('e').label())
            .by(select('v').id())
            .by(select('v').{DisplayLabelCoalesce})";
    }

    ///<summary>
    ///Lists outbound edges for a vertex, projecting (edge id, edge label, target-vertex id, target-vertex display label).
    ///</summary>
    public static string OutEdges(string vertexId)
    {
        return $@"g.V({FormatId(vertexId)}).outE().as('e').inV().as('v')
            .project('eId','eLabel','vId','vLabel')
            .by(select('e').id()).by(select('e').label())
            .by(select('v').id())
            .by(select('v').{DisplayLabelCoalesce})";
    }
}

///<summary>One top-level step of a Gremlin traversal, with its character range in the original query.</summary>
public class GremlinStep
{
    public string Text { get; set; }
    public string Name { get; set; }
    public int Start { get; set; }
    public int End { get; set; }
    public bool IsSource { get; set; }//the leading "g" (or "__" that starts an anonymous sub-traversal)
    public bool IsModulator { get; set; }//by/option/from/to/times/emit/until/with — can't be debugged on their own
    public bool IsTerminal { get; set; }//toList/next/iterate/profile/explain — not a traversal step
    public bool Debuggable { get; set; }//a step we can run a prefix up to
    public int Depth { get; set; }//0 = top-level; deeper for steps inside a __-sub-traversal (ParseTree only)
}

///<summary>
///Splits a Gremlin traversal into its top-level steps (respecting string literals and nested
///parentheses) so the debugger can run growing prefixes of the query. Pure and unit-tested; also
///reports whether a query mutates the graph (so the debugger can refuse to run those steps).
///</summary>
public static class GremlinStepParser
{
    private static readonly HashSet<string> Modulators = new()
    {
        "by", "option", "from", "to", "times", "emit", "until", "with", "read", "write"
    };

    private static readonly HashSet<string> Terminals = new()
    {
        "toList", "next", "tryNext", "iterate", "toSet", "toBulkSet", "hasNext", "fill", "promise", "profile", "explain"
    };

    private static readonly HashSet<string> Mutating = new()
    {
        "addV", "addE", "drop", "property", "mergeV", "mergeE"
    };

    ///<summary>Splits a traversal into top-level steps, each tagged with its role and character range.</summary>
    public static List<GremlinStep> Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<GremlinStep>();

        var steps = SplitTopLevelSteps(query, 0, query.Length);
        MarkRoles(steps);

        return steps;
    }

    //Splits query[start..end) into its top-level (depth-0 within the range) steps — respecting string
    //literals and nested brackets — with absolute character ranges. Roles aren't marked here.
    private static List<GremlinStep> SplitTopLevelSteps(string query, int start, int end)
    {
        var steps = new List<GremlinStep>();

        int depth = 0;
        char stringChar = '\0';
        bool inString = false;
        bool escape = false;
        int tokenStart = -1;

        for (int i = start; i < end; i++)
        {
            char c = query[i];

            if (tokenStart < 0 && !char.IsWhiteSpace(c))
                tokenStart = i;

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
                depth++;
            else if (c == ')' || c == ']' || c == '}')
                depth--;
            else if (c == '.' && depth == 0 && tokenStart >= 0)
            {
                steps.Add(MakeStep(query, tokenStart, i));
                tokenStart = -1;
            }
        }

        if (tokenStart >= 0)
            steps.Add(MakeStep(query, tokenStart, end));

        return steps;
    }

    ///<summary>
    ///Parses a traversal into a pre-order flat list of steps carrying their Depth, recursing into the
    ///__-prefixed sub-traversal arguments of steps (where/repeat/and/or/not/union/local/by/…). Char ranges
    ///are absolute (into the original query); Depth 0 = top-level. Lets the debugger step into sub-traversals.
    ///</summary>
    public static List<GremlinStep> ParseTree(string query)
    {
        var result = new List<GremlinStep>();

        if (!string.IsNullOrWhiteSpace(query))
            ParseTreeRange(query, 0, query.Length, 0, result);

        return result;
    }

    private static void ParseTreeRange(string query, int start, int end, int depth, List<GremlinStep> result)
    {
        var steps = SplitTopLevelSteps(query, start, end);

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            MarkRole(step, i == 0);
            step.Depth = depth;
            result.Add(step);

            foreach (var range in SubTraversalRanges(query, step))
                ParseTreeRange(query, range.Start, range.End, depth + 1, result);
        }
    }

    //The absolute ranges of the __-prefixed sub-traversal arguments inside a step's parentheses — e.g. the
    //__.out() in where(__.out()), or each branch of and(__.a(), __.b()). Literals, predicates (P.gt(5)) and
    //enums aren't sub-traversals, so only args that begin with "__" are returned.
    private static List<(int Start, int End)> SubTraversalRanges(string query, GremlinStep step)
    {
        var ranges = new List<(int Start, int End)>();

        int open = OuterParenOpen(query, step);
        if (open < 0)
            return ranges;

        int argStart = open + 1;
        int depth = 0;
        char stringChar = '\0';
        bool inString = false;
        bool escape = false;

        for (int i = open + 1; i < step.End; i++)
        {
            char c = query[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                depth++;
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                if (depth == 0)
                {
                    AddSubTraversal(query, argStart, i, ranges);
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddSubTraversal(query, argStart, i, ranges);
                argStart = i + 1;
            }
        }

        return ranges;
    }

    //The index of the step's outer "(" (the one after its name), respecting strings, or -1 if it has none.
    private static int OuterParenOpen(string query, GremlinStep step)
    {
        char stringChar = '\0';
        bool inString = false;
        bool escape = false;

        for (int i = step.Start; i < step.End; i++)
        {
            char c = query[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
                continue;
            }

            if (c == '(')
                return i;
        }

        return -1;
    }

    //Adds query[start..end) to `ranges` as a sub-traversal only when, once whitespace-trimmed, it begins
    //with "__" (an anonymous sub-traversal — the reliably-detectable form).
    private static void AddSubTraversal(string query, int start, int end, List<(int Start, int End)> ranges)
    {
        int s = start;
        while (s < end && char.IsWhiteSpace(query[s]))
            s++;

        int e = end;
        while (e > s && char.IsWhiteSpace(query[e - 1]))
            e--;

        if (e - s >= 2 && query[s] == '_' && query[s + 1] == '_')
            ranges.Add((s, e));
    }

    ///<summary>True when the query contains a graph-mutating step (addV/addE/drop/property/mergeV/mergeE) outside a string.</summary>
    public static bool IsMutating(string query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        char stringChar = '\0';
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
                continue;
            }

            if (!char.IsLetter(c) && c != '_')
                continue;

            int j = i;
            while (j < query.Length && (char.IsLetterOrDigit(query[j]) || query[j] == '_'))
                j++;

            var name = query.Substring(i, j - i);

            int k = j;
            while (k < query.Length && char.IsWhiteSpace(query[k]))
                k++;

            if (Mutating.Contains(name) && k < query.Length && query[k] == '(')
                return true;

            i = j - 1;
        }

        return false;
    }

    private static GremlinStep MakeStep(string query, int start, int endExclusive)
    {
        int s = start;
        while (s < endExclusive && char.IsWhiteSpace(query[s]))
            s++;

        int e = endExclusive;
        while (e > s && char.IsWhiteSpace(query[e - 1]))
            e--;

        var text = query.Substring(s, e - s);

        return new GremlinStep { Text = text, Name = StepName(text), Start = s, End = e };
    }

    private static string StepName(string text)
    {
        int i = 0;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        return text.Substring(0, i);
    }

    private static void MarkRoles(List<GremlinStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
            MarkRole(steps[i], i == 0);
    }

    //Tags a single step's role. isFirst marks a leading "g" (or a "__" that starts a sub-traversal) as the source.
    private static void MarkRole(GremlinStep step, bool isFirst)
    {
        if (isFirst && (step.Name == "g" || step.Name == "__"))
            step.IsSource = true;

        if (Modulators.Contains(step.Name))
            step.IsModulator = true;

        if (Terminals.Contains(step.Name))
            step.IsTerminal = true;

        step.Debuggable = !step.IsSource && !step.IsModulator && !step.IsTerminal && step.Name.Length > 0;
    }
}

///<summary>The kind of graph mutation a parsed <see cref="GraphEdit"/> represents.</summary>
public enum GraphEditKind
{
    AddNode,
    AddEdge,
    RemoveNode,
    RemoveEdge,
    SetProperty,
    DropProperty
}

///<summary>
///One graph mutation recovered from the staged "Generated" buffer by <see cref="GremlinEditParser"/>.
///Used to build the optimistic (uncommitted) view of the graph.
///</summary>
public class GraphEdit
{
    public GraphEditKind Kind { get; set; }
    public string Type { get; set; }//"node" / "edge" — for the property ops and element removals
    public string Id { get; set; }//element id (a temporary "__opt_*" id for a not-yet-committed add)
    public string Label { get; set; }//addV / addE label
    public string Source { get; set; }//addE source vertex id
    public string Target { get; set; }//addE target vertex id
    public string Key { get; set; }//property key (set / drop)
    public string Value { get; set; }//property value (set)
    public Dictionary<string, string> Properties { get; set; } = new();//inline props on an addV / addE
}

///<summary>
///Parses the exact query shapes emitted by <see cref="GremlinQueryBuilder"/> (as staged in the
///"Generated" buffer) into <see cref="GraphEdit"/> operations, so the canvas can preview uncommitted
///changes. Best-effort: any statement it doesn't recognize is silently skipped (it stays staged and
///commits normally). Reuses <see cref="GremlinStepParser.Parse"/> for string/paren-aware step splitting.
///</summary>
public static class GremlinEditParser
{
    ///<summary>Parses every recognized mutation in the buffer, in order. Unrecognised statements are skipped.</summary>
    public static List<GraphEdit> Parse(string buffer)
    {
        var edits = new List<GraphEdit>();

        if (string.IsNullOrWhiteSpace(buffer))
            return edits;

        var statements = SplitStatements(buffer);

        for (int i = 0; i < statements.Count; i++)
        {
            try
            {
                ParseStatement(statements[i], i, edits);
            }
            catch { }
        }

        return edits;
    }

    ///<summary>
    ///Merges freshly regenerated property lines for one element into an existing staged buffer: every
    ///set/drop-property line for that element whose key is in <paramref name="touchedKeys"/> is dropped and
    ///replaced by <paramref name="regeneratedLines"/>, while every other line — other elements, other keys,
    ///and non-property queries (adds / deletes / links) — is preserved. Lets property edits across several
    ///elements accumulate instead of overwriting one another, and drops a line for a reverted key.
    ///</summary>
    public static string MergePropertyEdits(
        string existingBuffer,
        string elementType,
        string elementId,
        IEnumerable<string> touchedKeys,
        IEnumerable<string> regeneratedLines)
    {
        HashSet<string> keys;
        if (touchedKeys == null)
            keys = new HashSet<string>();
        else
            keys = new HashSet<string>(touchedKeys);

        var result = new List<string>();

        var existing = (existingBuffer ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in existing)
            if (!IsPropertyEditFor(line, elementType, elementId, keys))
                result.Add(line);

        if (regeneratedLines != null)
            foreach (var line in regeneratedLines)
                if (!string.IsNullOrWhiteSpace(line))
                    result.Add(line);

        return string.Join("\n", result);
    }

    //True when the statement is a set/drop-property op targeting the given element and one of the keys.
    private static bool IsPropertyEditFor(string statement, string elementType, string elementId, HashSet<string> keys)
    {
        foreach (var edit in Parse(statement))
            if ((edit.Kind == GraphEditKind.SetProperty || edit.Kind == GraphEditKind.DropProperty)
                && edit.Type == elementType
                && edit.Id == elementId
                && keys.Contains(edit.Key))
                return true;

        return false;
    }

    //Interprets one traversal into zero or more edit ops appended to the list. lineIndex seeds a
    //stable temp id for a newly-added vertex/edge so re-parsing on each keystroke doesn't thrash.
    private static void ParseStatement(string statement, int lineIndex, List<GraphEdit> edits)
    {
        var steps = GremlinStepParser.Parse(statement);

        if (steps.Count == 0)
            return;

        string elementType = null;//"node" / "edge" — the V(id)/E(id) currently targeted
        string elementId = null;
        GraphEdit pendingAdd = null;//an addV/addE being built up on this line
        string pendingDropKey = null;//set by .properties('k'), consumed by the next .drop()

        foreach (var step in steps)
        {
            var name = step.Name;
            var args = StepInnerArgs(step.Text);

            if (name == "V" || name == "E")
            {
                if (name == "V")
                    elementType = "node";
                else
                    elementType = "edge";

                if (args.Count > 0)
                    elementId = NormalizeElementId(args[0]);
            }
            else if (name == "addV")
            {
                string label = "";
                if (args.Count > 0)
                    label = StripQuotes(args[0]);

                pendingAdd = new GraphEdit { Kind = GraphEditKind.AddNode, Type = "node", Id = StagedIds.ForVertex(lineIndex), Label = label };
                elementType = "node";
                elementId = pendingAdd.Id;
                edits.Add(pendingAdd);
            }
            else if (name == "addE")
            {
                string label = "";
                if (args.Count > 0)
                    label = StripQuotes(args[0]);

                string source = null;
                if (elementType == "node")
                    source = elementId;

                pendingAdd = new GraphEdit { Kind = GraphEditKind.AddEdge, Type = "edge", Id = StagedIds.ForEdge(lineIndex), Label = label, Source = source };
                edits.Add(pendingAdd);
            }
            else if (name == "to")
            {
                if (pendingAdd != null && pendingAdd.Kind == GraphEditKind.AddEdge && args.Count > 0)
                    pendingAdd.Target = ExtractVId(args[0]);
            }
            else if (name == "from")
            {
                if (pendingAdd != null && pendingAdd.Kind == GraphEditKind.AddEdge && args.Count > 0)
                    pendingAdd.Source = ExtractVId(args[0]);
            }
            else if (name == "property")
            {
                if (args.Count >= 2 && args[0].Trim() == "T.id")
                {
                    if (pendingAdd != null)
                    {
                        pendingAdd.Id = StripQuotes(args[1]);
                        elementId = pendingAdd.Id;
                    }
                }
                else if (args.Count >= 2)
                {
                    var key = StripQuotes(args[0]);
                    var value = StripQuotes(args[1]);

                    if (pendingAdd != null)
                        pendingAdd.Properties[key] = value;
                    else if (elementId != null)
                        edits.Add(new GraphEdit { Kind = GraphEditKind.SetProperty, Type = elementType, Id = elementId, Key = key, Value = value });
                }
            }
            else if (name == "properties")
            {
                if (args.Count > 0)
                    pendingDropKey = StripQuotes(args[0]);
            }
            else if (name == "drop")
            {
                if (pendingDropKey != null && elementId != null)
                {
                    edits.Add(new GraphEdit { Kind = GraphEditKind.DropProperty, Type = elementType, Id = elementId, Key = pendingDropKey });
                    pendingDropKey = null;
                }
                else if (elementId != null && pendingAdd == null)
                {
                    if (elementType == "node")
                        edits.Add(new GraphEdit { Kind = GraphEditKind.RemoveNode, Type = "node", Id = elementId });
                    else
                        edits.Add(new GraphEdit { Kind = GraphEditKind.RemoveEdge, Type = "edge", Id = elementId });
                }
            }
        }
    }

    //Splits the buffer into individual traversals on top-level newlines and semicolons (respecting
    //string literals and nested brackets), dropping blank statements.
    ///<summary>
    ///The buffer's statements: split on a newline or a <c>;</c> at depth zero, trimmed, empties dropped.
    ///Public because the stand-in ids are keyed to this numbering, so committing has to use it too.
    ///</summary>
    public static List<string> SplitStatements(string buffer)
    {
        var statements = new List<string>();

        int depth = 0;
        bool inString = false;
        char stringChar = '\0';
        bool escape = false;
        int start = 0;

        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
            }
            else if (c == '(' || c == '[' || c == '{')
                depth++;
            else if (c == ')' || c == ']' || c == '}')
                depth--;
            else if ((c == '\n' || c == ';') && depth == 0)
            {
                AddStatement(statements, buffer.Substring(start, i - start));
                start = i + 1;
            }
        }

        AddStatement(statements, buffer.Substring(start));

        return statements;
    }

    private static void AddStatement(List<string> list, string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.Length > 0)
            list.Add(trimmed);
    }

    //The comma-separated argument list inside a step's outer parentheses (e.g. property('k','v') →
    //["'k'", "'v'"]), respecting strings and nested brackets. Empty when the step has no parens.
    private static List<string> StepInnerArgs(string stepText)
    {
        int open = stepText.IndexOf('(');

        if (open < 0)
            return new List<string>();

        int depth = 0;
        int close = -1;
        bool inString = false;
        char stringChar = '\0';
        bool escape = false;

        for (int i = open; i < stepText.Length; i++)
        {
            char c = stepText[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
            }
            else if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;

                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0)
            return new List<string>();

        var inner = stepText.Substring(open + 1, close - open - 1);

        return SplitTopLevel(inner, ',');
    }

    //Splits on the separator at bracket depth zero and outside string literals; trims each part.
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();

        if (string.IsNullOrEmpty(s))
            return parts;

        int depth = 0;
        bool inString = false;
        char stringChar = '\0';
        bool escape = false;
        int start = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == stringChar)
                    inString = false;

                continue;
            }

            if (c == '\'' || c == '"')
            {
                inString = true;
                stringChar = c;
            }
            else if (c == '(' || c == '[' || c == '{')
                depth++;
            else if (c == ')' || c == ']' || c == '}')
                depth--;
            else if (c == sep && depth == 0)
            {
                parts.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        parts.Add(s.Substring(start).Trim());

        return parts;
    }

    //Pulls the id out of a to()/from() argument like "__.V(2)" or "V('a')".
    private static string ExtractVId(string argText)
    {
        if (string.IsNullOrEmpty(argText))
            return null;

        int idx = argText.IndexOf("V(");

        if (idx < 0)
            return null;

        var args = StepInnerArgs(argText.Substring(idx));

        if (args.Count > 0)
            return StripQuotes(args[0]);

        return null;
    }

    //Strips a surrounding single/double quote pair and unescapes the contents (inverse of
    //GremlinQueryBuilder.Escape); returns a bare (e.g. numeric) argument unchanged.
    private static string StripQuotes(string arg)
    {
        if (arg == null)
            return null;

        arg = arg.Trim();

        if (arg.Length >= 2 && ((arg[0] == '\'' && arg[^1] == '\'') || (arg[0] == '"' && arg[^1] == '"')))
            return Unescape(arg.Substring(1, arg.Length - 2));

        return arg;
    }

    //Normalizes a V(id)/E(id) argument back to the plain id used in the graph data, so a staged mutation
    //previews against the right element. A quoted string id is taken verbatim (it may legitimately end in
    //'L'); an unquoted numeric literal may carry a Long suffix from the typed builder — g.E(123L) — which
    //is stripped so it still matches the rendered element's id "123".
    private static string NormalizeElementId(string arg)
    {
        if (arg == null)
            return null;

        var trimmed = arg.Trim();

        bool quoted = trimmed.Length >= 2
            && ((trimmed[0] == '\'' && trimmed[^1] == '\'') || (trimmed[0] == '"' && trimmed[^1] == '"'));

        if (!quoted
            && trimmed.Length > 1
            && (trimmed[^1] == 'L' || trimmed[^1] == 'l')
            && long.TryParse(trimmed[..^1], out _))
            return trimmed[..^1];

        return StripQuotes(trimmed);
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool escape = false;

        foreach (char c in s)
        {
            if (escape)
            {
                sb.Append(c);
                escape = false;
            }
            else if (c == '\\')
                escape = true;
            else
                sb.Append(c);
        }

        if (escape)
            sb.Append('\\');

        return sb.ToString();
    }
}

///<summary>
///Curated Gremlin example queries shown as clickable buttons in the "Examples" tab.
///Clicking one pastes its <see cref="QueryExample.Query"/> into the Query editor.
///Tuned for a plain TinkerGraph and the assembly-tree sample data
///(see <see cref="TableGraphLoader"/>) — no Cosmos partition keys required.
///</summary>
public static class GremlinExamples
{
    ///<summary>
    ///One-shot loader that adds a table-manufacturing assembly tree (13 vertices, 15 edges)
    ///to the existing graph: a 2x4 is cut into 4 legs, the legs + table top + 4 screws are
    ///joined into an unpainted table, and paint + the unpainted table compose the finished
    ///table (the root). Single-line, semicolon-separated so it runs as one request.
    ///</summary>
    public const string TableGraphLoader =
        "def lumber=g.addV('Material').property('name','2x4 Lumber').property('description','2x4 pine board').next(); " +
        "def leg1=g.addV('Component').property('name','Leg 1').property('description','Table leg cut from 2x4').next(); " +
        "def leg2=g.addV('Component').property('name','Leg 2').property('description','Table leg cut from 2x4').next(); " +
        "def leg3=g.addV('Component').property('name','Leg 3').property('description','Table leg cut from 2x4').next(); " +
        "def leg4=g.addV('Component').property('name','Leg 4').property('description','Table leg cut from 2x4').next(); " +
        "def top=g.addV('Component').property('name','Table Top').property('description','Flat table surface').next(); " +
        "def screw1=g.addV('Component').property('name','Screw 1').property('description','Wood screw').next(); " +
        "def screw2=g.addV('Component').property('name','Screw 2').property('description','Wood screw').next(); " +
        "def screw3=g.addV('Component').property('name','Screw 3').property('description','Wood screw').next(); " +
        "def screw4=g.addV('Component').property('name','Screw 4').property('description','Wood screw').next(); " +
        "def paint=g.addV('Material').property('name','Paint').property('description','Finish coat').next(); " +
        "def unpainted=g.addV('Assembly').property('name','Unpainted Table').property('description','Assembled but unpainted table').next(); " +
        "def finished=g.addV('Product').property('name','Finished Table').property('description','Completed painted table').next(); " +
        "g.addE('cutInto').from(lumber).to(leg1).iterate(); " +
        "g.addE('cutInto').from(lumber).to(leg2).iterate(); " +
        "g.addE('cutInto').from(lumber).to(leg3).iterate(); " +
        "g.addE('cutInto').from(lumber).to(leg4).iterate(); " +
        "g.addE('composes').from(leg1).to(unpainted).iterate(); " +
        "g.addE('composes').from(leg2).to(unpainted).iterate(); " +
        "g.addE('composes').from(leg3).to(unpainted).iterate(); " +
        "g.addE('composes').from(leg4).to(unpainted).iterate(); " +
        "g.addE('composes').from(top).to(unpainted).iterate(); " +
        "g.addE('composes').from(screw1).to(unpainted).iterate(); " +
        "g.addE('composes').from(screw2).to(unpainted).iterate(); " +
        "g.addE('composes').from(screw3).to(unpainted).iterate(); " +
        "g.addE('composes').from(screw4).to(unpainted).iterate(); " +
        "g.addE('composes').from(unpainted).to(finished).iterate(); " +
        "g.addE('composes').from(paint).to(finished).iterate(); " +
        "g.V().count()";

    ///<summary>
    ///A social network: ten people in four cities, joined by how they actually know one another —
    ///friends, colleagues, a married couple, and who mentors whom (10 vertices, 19 edges).
    ///
    ///People only. This used to be TinkerPop's "modern" graph, which mixes four people with two pieces
    ///of software called lop and ripple; as a first look at a graph it asks you to work out what a
    ///"software" is doing in a social network before you can read anything else.
    ///
    ///Denser than one edge per person on purpose: each city is a little cluster and a handful of ties
    ///cross between them, so the force layouts have something to lay out and a traversal has more than
    ///one path to find. Added to the existing graph.
    ///</summary>
    public const string ModernGraphLoader =
        "def alice=g.addV('person').property('name','Alice').property('age',34).property('city','Seattle').next(); " +
        "def ben=g.addV('person').property('name','Ben').property('age',41).property('city','Seattle').next(); " +
        "def grace=g.addV('person').property('name','Grace').property('age',26).property('city','Seattle').next(); " +
        "def carla=g.addV('person').property('name','Carla').property('age',29).property('city','Portland').next(); " +
        "def daniel=g.addV('person').property('name','Daniel').property('age',37).property('city','Portland').next(); " +
        "def jonas=g.addV('person').property('name','Jonas').property('age',47).property('city','Portland').next(); " +
        "def elena=g.addV('person').property('name','Elena').property('age',45).property('city','San Francisco').next(); " +
        "def frank=g.addV('person').property('name','Frank').property('age',52).property('city','San Francisco').next(); " +
        "def hugo=g.addV('person').property('name','Hugo').property('age',31).property('city','Denver').next(); " +
        "def iris=g.addV('person').property('name','Iris').property('age',38).property('city','Denver').next(); " +
        "g.addE('knows').from(alice).to(ben).property('since',2015).iterate(); " +
        "g.addE('knows').from(alice).to(grace).property('since',2019).iterate(); " +
        "g.addE('knows').from(ben).to(grace).property('since',2019).iterate(); " +
        "g.addE('knows').from(carla).to(daniel).property('since',2016).iterate(); " +
        "g.addE('knows').from(daniel).to(jonas).property('since',2012).iterate(); " +
        "g.addE('knows').from(carla).to(jonas).property('since',2021).iterate(); " +
        "g.addE('knows').from(hugo).to(iris).property('since',2017).iterate(); " +
        "g.addE('knows').from(frank).to(jonas).property('since',2008).iterate(); " +
        "g.addE('knows').from(grace).to(hugo).property('since',2022).iterate(); " +
        "g.addE('knows').from(daniel).to(elena).property('since',2014).iterate(); " +
        "g.addE('knows').from(iris).to(alice).property('since',2013).iterate(); " +
        "g.addE('knows').from(jonas).to(ben).property('since',2011).iterate(); " +
        "g.addE('worksWith').from(alice).to(carla).property('at','Northwind Studio').iterate(); " +
        "g.addE('worksWith').from(elena).to(iris).property('at','Cascade Labs').iterate(); " +
        "g.addE('marriedTo').from(elena).to(frank).property('since',2006).iterate(); " +
        "g.addE('mentors').from(ben).to(grace).property('field','engineering').iterate(); " +
        "g.addE('mentors').from(jonas).to(carla).property('field','architecture').iterate(); " +
        "g.addE('mentors').from(iris).to(hugo).property('field','photography').iterate(); " +
        "g.addE('mentors').from(frank).to(daniel).property('field','law').iterate(); " +
        "g.V().count()";

    ///<summary>
    ///A small flight-route network between west-coast cities (5 vertices, 6 edges).
    ///Cyclic, so it shows off the 2D/3D force layouts. Added to the existing graph.
    ///</summary>
    public const string FlightRoutesLoader =
        "def sea=g.addV('City').property('name','Seattle').property('code','SEA').next(); " +
        "def pdx=g.addV('City').property('name','Portland').property('code','PDX').next(); " +
        "def sfo=g.addV('City').property('name','San Francisco').property('code','SFO').next(); " +
        "def lax=g.addV('City').property('name','Los Angeles').property('code','LAX').next(); " +
        "def den=g.addV('City').property('name','Denver').property('code','DEN').next(); " +
        "g.addE('route').from(sea).to(pdx).property('miles',130).iterate(); " +
        "g.addE('route').from(pdx).to(sfo).property('miles',535).iterate(); " +
        "g.addE('route').from(sfo).to(lax).property('miles',337).iterate(); " +
        "g.addE('route').from(sea).to(den).property('miles',1024).iterate(); " +
        "g.addE('route').from(den).to(lax).property('miles',862).iterate(); " +
        "g.addE('route').from(sfo).to(sea).property('miles',679).iterate(); " +
        "g.V().count()";

    ///<summary>
    ///Three mechanical parts, each carrying a linked image (gdbvImage) and 3D model (gdbvModel), both set
    ///to show (gdbvShow), joined to a central mount — the image draws in the 2D view, the .obj model in 3D.
    ///Added to the graph. The image / .obj files are hosted externally, so the viewer needs internet to load them.
    ///</summary>
    public const string ThreeDObjectsLoader =
        "def screw=g.addV('Component').property('name','Screw').property('quantity',3).property('gdbvImage','https://eecs.blog/BlazorApps/GraphDBExampleFiles/screw.png').property('gdbvModel','https://eecs.blog/BlazorApps/GraphDBExampleFiles/screw.obj').property('gdbvShow','gdbvImage,gdbvModel').property('exampleType','3d_example_1').next(); " +
        "def gear=g.addV('Component').property('name','Gear').property('quantity',3).property('gdbvImage','https://eecs.blog/BlazorApps/GraphDBExampleFiles/gear.jpg').property('gdbvModel','https://eecs.blog/BlazorApps/GraphDBExampleFiles/gear.obj').property('gdbvShow','gdbvImage,gdbvModel').property('exampleType','3d_example_1').next(); " +
        "def mount=g.addV('Component').property('name','Mount').property('quantity',1).property('gdbvImage','https://eecs.blog/BlazorApps/GraphDBExampleFiles/mount.jpg').property('gdbvModel','https://eecs.blog/BlazorApps/GraphDBExampleFiles/mount.obj').property('gdbvShow','gdbvImage,gdbvModel').property('exampleType','3d_example_1').next(); " +
        "g.addE('edge label').from(screw).to(mount).iterate(); " +
        "g.addE('edge label').from(gear).to(mount).iterate(); " +
        "g.V().count()";

    ///<summary>All example groups, in display order.</summary>
    public static IReadOnlyList<QueryExampleGroup> Groups { get; } = new List<QueryExampleGroup>
    {
        new("Inspect", new QueryExample[]
        {
            new("Count vertices",  "g.V().count()"),
            new("Count edges",     "g.E().count()"),
            new("All vertices",    "g.V()"),
            new("Vertices + props","g.V().valueMap(true)"),
            new("All edges",       "g.E()"),
            new("Distinct labels", "g.V().label().dedup()"),
        }),

        new("Visualize", new QueryExample[]
        {
            new("Full graph",       "g.V().as('v').bothE().as('e').otherV().as('o').select('v','e','o')"),
            new("First 25 vertices","g.V().limit(25)"),
        }),

        new("Mutate", new QueryExample[]
        {
            new("Add Component", "g.addV('Component').property('name','New Component').property('description','')"),
            new("Drop ALL data", "g.V().drop()", Destructive: true),
        }),

        new("Sample graphs", new QueryExample[]
        {
            new("Table assembly", TableGraphLoader),
            new("Social network",     ModernGraphLoader),
            new("Flight routes",      FlightRoutesLoader),
            new("3D Objects",         ThreeDObjectsLoader),
        }),
    };
}
