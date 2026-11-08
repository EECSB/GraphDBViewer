using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///Parses a human-written graph description (Graphviz DOT or Mermaid flowchart) into a
///simple node/edge model, then converts it to the render JSON the viewer understands
///and to the connected engine's own write queries, through its builder (<see cref="ToQueries"/>).
///Conventions: a DOT "type=" attribute becomes the vertex label; a DOT/Mermaid display label
///becomes the "name" property.
///</summary>
public static class GraphImport
{
    public class Node
    {
        public string Id { get; set; }
        public string Label { get; set; } = "vertex";
        public Dictionary<string, string> Properties { get; } = new();
    }

    public class Edge
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Label { get; set; } = "edge";
        public Dictionary<string, string> Properties { get; } = new();
    }

    public class Graph
    {
        public List<Node> Nodes { get; } = new();
        public List<Edge> Edges { get; } = new();
        private readonly Dictionary<string, Node> _byId = new();

        public Node GetOrAdd(string id)
        {
            if (_byId.TryGetValue(id, out var existing))
                return existing;

            var node = new Node { Id = id };
            _byId[id] = node;
            Nodes.Add(node);
            return node;
        }

        ///<summary>
        ///A deep copy, so a caller can apply a reversible change — a preview the user is toggling on and
        ///off — without destroying what it was derived from.
        ///</summary>
        public Graph Clone()
        {
            var copy = new Graph();

            foreach (var node in Nodes)
            {
                var cloned = copy.GetOrAdd(node.Id);
                cloned.Label = node.Label;

                foreach (var pair in node.Properties)
                    cloned.Properties[pair.Key] = pair.Value;
            }

            foreach (var edge in Edges)
            {
                var cloned = new Edge { Source = edge.Source, Label = edge.Label, Target = edge.Target };

                foreach (var pair in edge.Properties)
                    cloned.Properties[pair.Key] = pair.Value;

                copy.Edges.Add(cloned);
            }

            return copy;
        }
    }

    ///<summary>True when the text looks like DOT or Mermaid (and not JSON), so callers can route it here.</summary>
    public static bool LooksLikeGraphText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var t = text.TrimStart();
        if (t.StartsWith("{") || t.StartsWith("["))
            return false;

        if (Regex.IsMatch(t, @"^(strict\s+)?(di)?graph\b", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(t, @"^flowchart\b", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    ///<summary>
    ///True when the text looks like exported addV/addE Gremlin (the app's generated queries), so it can be
    ///re-imported and rendered as a drawing without a database. Parsing is done by GremlinEditParser.
    ///</summary>
    public static bool LooksLikeGremlin(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains(".addV(") || text.Contains(".addE(");
    }

    ///<summary>
    ///True when the text looks like exported Cypher (the app's generated CREATE / MATCH statements). The
    ///leading-clause-then-parenthesis shape is what separates it from DOT, Mermaid and JSON, all of which
    ///are ruled out before this is asked.
    ///</summary>
    public static bool LooksLikeCypher(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"\b(CREATE|MATCH|MERGE)\s*\(", RegexOptions.IgnoreCase);
    }

    ///<summary>
    ///True when the text is already a script of graph-writing queries, in either language — so it can be
    ///re-imported as a drawing rather than parsed as data. Which one it is doesn't matter here: the staged
    ///buffer is previewed by the connection's own edit parser.
    ///</summary>
    public static bool LooksLikeQueryScript(string text)
    {
        return LooksLikeGremlin(text) || LooksLikeCypher(text);
    }

    ///<summary>Parses DOT or Mermaid text, or returns null when nothing usable is found.</summary>
    public static Graph Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.TrimStart();

        Graph g;
        if (Regex.IsMatch(t, @"^(flowchart|graph)\s+(TB|TD|BT|RL|LR)\b", RegexOptions.IgnoreCase))
            g = ParseMermaid(text);
        else if (Regex.IsMatch(t, @"^flowchart\b", RegexOptions.IgnoreCase))
            g = ParseMermaid(text);
        else if (Regex.IsMatch(t, @"^(strict\s+)?(di)?graph\b", RegexOptions.IgnoreCase))
            g = ParseDot(text);
        else
            return null;

        if (g.Nodes.Count == 0 && g.Edges.Count == 0)
            return null;

        return g;
    }

    #region Render / Gremlin output

    ///<summary>Builds a plain JSON array of vertices/edges that GraphDataConverter can render.</summary>
    public static string ToRenderJson(Graph g)
    {
        var elements = new List<object>();

        foreach (var n in g.Nodes)
            elements.Add(new { id = n.Id, label = n.Label, properties = n.Properties });

        foreach (var e in g.Edges)
            elements.Add(new { id = $"{e.Source}->{e.Target}:{e.Label}", label = e.Label, outV = e.Source, inV = e.Target, properties = e.Properties });

        return JsonSerializer.Serialize(elements);
    }

    ///<summary>
    ///Builds an add-nodes-then-add-edges script (one statement per line) in the connected database's own
    ///language; nodes come first so the edges can resolve their endpoints. Pass the provider's builder —
    ///the default is Gremlin, which is what an import produced before other engines could stage edits.
    ///</summary>
    public static string ToQueries(Graph g, IGraphQueryBuilder queryBuilder = null)
    {
        var qb = queryBuilder ?? GremlinQueryBuilderAdapter.Instance;
        var lines = new List<string>();

        foreach (var n in g.Nodes)
            lines.Add(qb.AddVertexWithProperties(n.Label, n.Id, n.Properties));

        foreach (var e in g.Edges)
            lines.Add(qb.AddEdgeWithProperties(e.Source, e.Label, e.Target, e.Properties));

        return string.Join("\n", lines);
    }

    ///<summary>Like <see cref="ToQueries"/> but from pasted GraphSON/JSON; edges with a missing endpoint are skipped.</summary>
    public static string QueriesFromJson(JsonElement data, IGraphQueryBuilder queryBuilder = null)
    {
        var qb = queryBuilder ?? GremlinQueryBuilderAdapter.Instance;
        var table = GraphDataConverter.ToTable(data);
        var lines = new List<string>();

        foreach (var n in table.Nodes)
            lines.Add(qb.AddVertexWithProperties(n.Label, n.Id, n.Properties));

        foreach (var e in table.Edges)
        {
            if (string.IsNullOrEmpty(e.Source) || string.IsNullOrEmpty(e.Target))
                continue;

            lines.Add(qb.AddEdgeWithProperties(e.Source, e.Label, e.Target, e.Properties));
        }

        return string.Join("\n", lines);
    }

    #endregion

    #region DOT

    //Comments, statement ends and the edge operator are looked for outside double-quoted strings only (2026-11-07):
    //the export quotes every name, and a name holding a #, a //, a ; or a -> is a name, not a comment, two
    //statements or an edge.
    private static Graph ParseDot(string text)
    {
        var g = new Graph();

        text = StripDotComments(text);

        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');

        string body;
        if (open >= 0 && close > open)
            body = text.Substring(open + 1, close - open - 1);
        else
            body = text;

        foreach (var raw in SplitDotStatements(body))
        {
            var stmt = raw.Trim();
            if (stmt.Length == 0)
                continue;

            var lower = stmt.ToLowerInvariant();
            if (lower == "node" || lower.StartsWith("node ") || lower.StartsWith("node[") || lower.StartsWith("node ["))
                continue;

            if (lower == "edge" || lower.StartsWith("edge ") || lower.StartsWith("edge[") || lower.StartsWith("edge ["))
                continue;

            if (lower.StartsWith("graph ") || lower.StartsWith("graph[") || lower.StartsWith("graph ["))
                continue;

            bool isEdge = DotEdgeOperator(stmt, 0) >= 0;

            //Bare graph attribute such as rankdir=LR (no edge operator, no node brackets).
            if (!isEdge && stmt.Contains("=") && !stmt.Contains("["))
                continue;

            if (isEdge)
                ParseDotEdge(g, stmt);
            else
                ParseDotNode(g, stmt);
        }

        return g;
    }

    //Where the double-quoted string opening at i ends: the index after its closing quote, or the text's end when it
    //never closes. A backslash escapes the character after it, as DotQuote writes \" and \\.
    private static int DotStringEnd(string text, int i)
    {
        i++;

        while (i < text.Length)
        {
            if (text[i] == '\\')
                i += 2;
            else if (text[i] == '"')
                return i + 1;
            else
                i++;
        }

        return text.Length;
    }

    //A /* */ comment, or a // or # one to the end of its line, each replaced by a space; what is in a string stays.
    private static string StripDotComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            int end;

            if (c == '"')
            {
                end = DotStringEnd(text, i);
                sb.Append(text, i, end - i);
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (end < 0)
                    i = text.Length;
                else
                    i = end + 2;

                sb.Append(' ');
            }
            else if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                end = text.IndexOf('\n', i);

                if (end < 0)
                    i = text.Length;
                else
                    i = end;

                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    //The statements, ended by a ; or a line break outside a string.
    private static List<string> SplitDotStatements(string body)
    {
        var statements = new List<string>();
        int start = 0;
        int i = 0;

        while (i < body.Length)
        {
            char c = body[i];

            if (c == '"')
                i = DotStringEnd(body, i);
            else if (c == ';' || c == '\n' || c == '\r')
            {
                statements.Add(body.Substring(start, i - start));
                i++;
                start = i;
            }
            else
                i++;
        }

        statements.Add(body.Substring(start));

        return statements;
    }

    //The index of the first -> or -- at or after from that is outside a string, or -1 when there is none.
    private static int DotEdgeOperator(string text, int from)
    {
        int i = from;

        while (i < text.Length)
        {
            if (text[i] == '"')
                i = DotStringEnd(text, i);
            else if (text[i] == '-' && i + 1 < text.Length && (text[i + 1] == '>' || text[i + 1] == '-'))
                return i;
            else
                i++;
        }

        return -1;
    }

    private static void ParseDotEdge(Graph g, string stmt)
    {
        string attrs = null;
        string chain = stmt;

        var m = Regex.Match(stmt, @"\[(.*)\]\s*$", RegexOptions.Singleline);
        if (m.Success)
        {
            attrs = m.Groups[1].Value;
            chain = stmt.Substring(0, m.Index);
        }

        var ids = new List<string>();
        int from = 0;
        int at = DotEdgeOperator(chain, 0);

        while (at >= 0)
        {
            ids.Add(Unquote(chain.Substring(from, at - from)));
            from = at + 2;
            at = DotEdgeOperator(chain, from);
        }

        ids.Add(Unquote(chain.Substring(from)));
        ids.RemoveAll(string.IsNullOrEmpty);

        var props = ParseDotAttrs(attrs);

        string edgeLabel = "edge";
        if (props.TryGetValue("label", out var lbl))
        {
            edgeLabel = lbl;
            props.Remove("label");
        }

        for (int i = 0; i + 1 < ids.Count; i++)
        {
            g.GetOrAdd(ids[i]);
            g.GetOrAdd(ids[i + 1]);

            var edge = new Edge { Source = ids[i], Target = ids[i + 1], Label = edgeLabel };
            foreach (var kv in props)
                edge.Properties[kv.Key] = kv.Value;

            g.Edges.Add(edge);
        }
    }

    private static void ParseDotNode(Graph g, string stmt)
    {
        string idPart = stmt;
        string attrs = null;

        var m = Regex.Match(stmt, @"^(.*?)\s*\[(.*)\]\s*$", RegexOptions.Singleline);
        if (m.Success)
        {
            idPart = m.Groups[1].Value.Trim();
            attrs = m.Groups[2].Value;
        }

        var id = Unquote(idPart.Trim());
        if (string.IsNullOrEmpty(id))
            return;

        var node = g.GetOrAdd(id);
        ApplyNodeAttrs(node, ParseDotAttrs(attrs));
    }

    private static void ApplyNodeAttrs(Node node, Dictionary<string, string> attrs)
    {
        foreach (var kv in attrs)
            if (kv.Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                node.Label = kv.Value;
            else if (kv.Key.Equals("label", StringComparison.OrdinalIgnoreCase))
            {
                if (!node.Properties.ContainsKey("name"))
                    node.Properties["name"] = kv.Value;
            }
            else
                node.Properties[kv.Key] = kv.Value;
    }

    private static Dictionary<string, string> ParseDotAttrs(string attrs)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(attrs))
            return d;

        foreach (Match m in Regex.Matches(attrs, @"([A-Za-z_][\w]*)\s*=\s*(""(?:[^""\\]|\\.)*""|[^\s,;\]]+)"))
            d[m.Groups[1].Value] = Unquote(m.Groups[2].Value);

        return d;
    }

    #endregion

    #region Mermaid

    private static readonly Regex MermaidEdge = new Regex(
        @"^(?<left>[A-Za-z0-9_]+(?:[\[\(\{][^\]\)\}]*[\]\)\}])?)\s*" +
        @"(?:" +
            @"(?<l1>-\.->|-{2,}>|={2,}>|-{2,}|={2,}|--[xo]|[xo]--)\s*(?:\|(?<lbl1>[^|]*)\|)?" +
            @"|" +
            @"(?<dash>-{2,}|={2,})\s*(?<lbl2>[^->=|][^>]*?)\s*(?<l2>-{2,}>|={2,}>|-{2,}|={2,})" +
        @")\s*" +
        @"(?<right>[A-Za-z0-9_]+(?:[\[\(\{][^\]\)\}]*[\]\)\}])?)\s*$");

    private static Graph ParseMermaid(string text)
    {
        var g = new Graph();

        text = Regex.Replace(text, @"%%[^\n]*", " ");
        var lines = text.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (Regex.IsMatch(line, @"^(flowchart|graph)\b", RegexOptions.IgnoreCase))
                continue;

            if (Regex.IsMatch(line, @"^(subgraph|end|classDef|class|style|linkStyle|click|direction)\b", RegexOptions.IgnoreCase))
                continue;

            if (TryParseMermaidEdge(g, line))
                continue;

            TryParseMermaidNode(g, line);
        }

        return g;
    }

    private static bool TryParseMermaidEdge(Graph g, string line)
    {
        var m = MermaidEdge.Match(line);
        if (!m.Success)
            return false;

        var left = ParseMermaidNodeToken(m.Groups["left"].Value);
        var right = ParseMermaidNodeToken(m.Groups["right"].Value);
        if (string.IsNullOrEmpty(left.Id) || string.IsNullOrEmpty(right.Id))
            return false;

        AddMermaidNode(g, left);
        AddMermaidNode(g, right);

        string label = "";
        if (m.Groups["lbl1"].Success && m.Groups["lbl1"].Value.Trim().Length > 0)
            label = m.Groups["lbl1"].Value.Trim();
        else if (m.Groups["lbl2"].Success && m.Groups["lbl2"].Value.Trim().Length > 0)
            label = m.Groups["lbl2"].Value.Trim();

        if (label.Length == 0)
            label = "edge";

        g.Edges.Add(new Edge { Source = left.Id, Target = right.Id, Label = label });
        return true;
    }

    private static void TryParseMermaidNode(Graph g, string line)
    {
        var node = ParseMermaidNodeToken(line);
        if (string.IsNullOrEmpty(node.Id))
            return;

        AddMermaidNode(g, node);
    }

    private static void AddMermaidNode(Graph g, (string Id, string Text) parsed)
    {
        var node = g.GetOrAdd(parsed.Id);
        if (!string.IsNullOrEmpty(parsed.Text) && !node.Properties.ContainsKey("name"))
            node.Properties["name"] = parsed.Text;
    }

    private static (string Id, string Text) ParseMermaidNodeToken(string token)
    {
        var m = Regex.Match(token.Trim(), @"^([A-Za-z0-9_]+)\s*(?:[\[\(\{]{1,2}\s*(.*?)\s*[\]\)\}]{1,2})?$");
        if (!m.Success)
            return (token.Trim(), null);

        string text = null;
        if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
            text = Unquote(m.Groups[2].Value);

        return (m.Groups[1].Value, text);
    }

    #endregion

    //A double-quoted value without its quotes, the escapes DotQuote writes read back in one pass: \" a quote, \\ a
    //backslash and \n a line break, so \\n is a backslash and an n rather than a line break. Any other backslash stays.
    private static string Unquote(string value)
    {
        if (value == null)
            return null;

        var v = value.Trim();
        if (v.Length < 2 || v[0] != '"' || v[v.Length - 1] != '"')
            return v;

        var sb = new StringBuilder(v.Length);
        int end = v.Length - 1;
        int i = 1;

        while (i < end)
        {
            char c = v[i];

            if (c != '\\' || i + 1 >= end)
            {
                sb.Append(c);
                i++;
            }
            else if (v[i + 1] == 'n')
            {
                sb.Append('\n');
                i += 2;
            }
            else if (v[i + 1] == '"' || v[i + 1] == '\\')
            {
                sb.Append(v[i + 1]);
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
