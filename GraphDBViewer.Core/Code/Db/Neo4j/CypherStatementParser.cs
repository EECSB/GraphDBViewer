using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads Cypher back: whether a statement would change the graph, and what the staged "Generated" buffer
///amounts to. The Cypher counterpart of <see cref="GremlinStepParser.IsMutating"/> and
///<see cref="GremlinEditParser"/>.
///</summary>
public static class CypherStatementParser
{
    //The writing clauses. A read-only query contains none of them; FOREACH and CALL are not listed because
    //what they do is decided by what is written inside them, and a write there is caught by these anyway.
    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "MERGE", "SET", "DELETE", "DETACH", "REMOVE", "DROP", "LOAD"
    };

    ///<summary>
    ///True when the query contains a writing clause outside a string, a backticked identifier or a comment
    ///— so a node literally named 'DELETE' or a property key <c>`set`</c> doesn't read as a mutation.
    ///</summary>
    public static bool IsMutating(string query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        foreach (var word in Words(query))
            if (WriteKeywords.Contains(word))
                return true;

        return false;
    }

    //Walks the query, skipping string literals, backticked identifiers and comments, and yields the bare
    //words in between.
    private static IEnumerable<string> Words(string query)
    {
        var word = new StringBuilder();

        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];

            //Line comment.
            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                while (i < query.Length && query[i] != '\n')
                    i++;

                continue;
            }

            //Block comment.
            if (c == '/' && i + 1 < query.Length && query[i + 1] == '*')
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                i += 2;
                while (i + 1 < query.Length && !(query[i] == '*' && query[i + 1] == '/'))
                    i++;

                i++;
                continue;
            }

            //A quoted string or a backticked identifier — neither can hold a clause.
            if (c == '\'' || c == '"' || c == '`')
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                char quote = c;
                i++;

                while (i < query.Length)
                {
                    //Backticks escape by doubling rather than with a backslash.
                    if (quote != '`' && query[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (query[i] == quote)
                    {
                        if (quote == '`' && i + 1 < query.Length && query[i + 1] == '`')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                continue;
            }

            if (char.IsLetter(c))
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
            yield return word.ToString();
    }

    //── Staged-edit parsing ─────────────────────────────────────────────

    //A single-quoted Cypher literal, backslash-escaped — the form CypherQueryBuilder.Escape produces.
    private const string Literal = @"'((?:[^'\\]|\\.)*)'";

    //A backticked identifier, where an inner backtick is doubled.
    private const string Identifier = "`((?:[^`]|``)*)`";

    private static readonly Regex AddNodeRegex = new(
        $@"^CREATE\s*\(\s*n\s*:\s*{LabelIdentifier}\s*(?:\{{(?<props>.*)\}}\s*)?\)\s*RETURN\s+n$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //Everything below names its captures: the shapes share sub-patterns, so positional groups would shift
    //under any edit to one of them.
    private const string IdLiteral = "'(?<id>(?:[^'\\\\]|\\\\.)*)'";
    private const string ValueLiteral = "'(?<value>(?:[^'\\\\]|\\\\.)*)'";
    private const string KeyIdentifier = "`(?<key>(?:[^`]|``)*)`";
    private const string LabelIdentifier = "`(?<label>(?:[^`]|``)*)`";

    //A node is addressed by element id or by import id (CypherQueryBuilder.NodeIdPredicate) — both name
    //the same node, so only the element-id literal is captured.
    private static string NodePredicate(string variable, string captureName)
    {
        return $@"\(\s*elementId\({variable}\)\s*=\s*'(?<{captureName}>(?:[^'\\]|\\.)*)'\s+OR\s+{variable}\.`(?:[^`]|``)*`\s*=\s*'(?:[^'\\]|\\.)*'\s*\)";
    }

    private static readonly Regex AddEdgeRegex = new(
        $@"^MATCH\s*\(a\)\s*,\s*\(b\)\s+WHERE\s+{NodePredicate("a", "source")}\s+AND\s+{NodePredicate("b", "target")}\s+CREATE\s*\(a\)-\[\s*r\s*:\s*{LabelIdentifier}\s*(?:\{{(?<props>.*)\}}\s*)?\]->\(b\)\s*RETURN\s+r$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropNodeRegex = new(
        $@"^MATCH\s*\(n\)\s+WHERE\s+{NodePredicate("n", "id")}\s+DETACH\s+DELETE\s+n$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropEdgeRegex = new(
        $@"^MATCH\s*\(\)-\[\s*r\s*\]-\(\)\s+WHERE\s+elementId\(r\)\s*=\s*{IdLiteral}\s+DELETE\s+r$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetNodePropertyRegex = new(
        $@"^MATCH\s*\(n\)\s+WHERE\s+{NodePredicate("n", "id")}\s+SET\s+n\.{KeyIdentifier}\s*=\s*{ValueLiteral}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetEdgePropertyRegex = new(
        $@"^MATCH\s*\(\)-\[\s*r\s*\]-\(\)\s+WHERE\s+elementId\(r\)\s*=\s*{IdLiteral}\s+SET\s+r\.{KeyIdentifier}\s*=\s*{ValueLiteral}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropNodePropertyRegex = new(
        $@"^MATCH\s*\(n\)\s+WHERE\s+{NodePredicate("n", "id")}\s+REMOVE\s+n\.{KeyIdentifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropEdgePropertyRegex = new(
        $@"^MATCH\s*\(\)-\[\s*r\s*\]-\(\)\s+WHERE\s+elementId\(r\)\s*=\s*{IdLiteral}\s+REMOVE\s+r\.{KeyIdentifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //One `key`: 'value' pair inside a map literal.
    private static readonly Regex PropertyPairRegex = new(
        $@"(?:{Identifier}|(?<bare>[A-Za-z_][A-Za-z0-9_]*))\s*:\s*{Literal}",
        RegexOptions.Compiled);

    ///<summary>
    ///Parses the query shapes <see cref="CypherQueryBuilder"/> emits into <see cref="GraphEdit"/>s, so the
    ///canvas can preview uncommitted changes. Best-effort and line-oriented, exactly like the Gremlin
    ///parser: an unrecognized statement is skipped, stays staged, and commits normally.
    ///</summary>
    public static List<GraphEdit> Parse(string buffer)
    {
        var edits = new List<GraphEdit>();

        if (string.IsNullOrWhiteSpace(buffer))
            return edits;

        var lines = buffer.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
                continue;

            try
            {
                ParseStatement(line, i, edits);
            }
            catch { }
        }

        return edits;
    }

    private static void ParseStatement(string line, int lineIndex, List<GraphEdit> edits)
    {
        var addNode = AddNodeRegex.Match(line);
        if (addNode.Success)
        {
            var edit = new GraphEdit
            {
                Kind = GraphEditKind.AddNode,
                Type = "node",
                //A not-yet-committed node has no database id, so it gets a temporary one, matching the
                //Gremlin parser's convention so the rest of the viewer treats them identically.
                Id = StagedIds.ForVertex(lineIndex),
                Label = Unquote(addNode.Groups["label"].Value),
                Properties = ParseProperties(addNode.Groups["props"].Value)
            };

            //An import carries the source id as a property; prefer it so the edges that reference it line up.
            if (edit.Properties.TryGetValue(CypherQueryBuilder.ImportIdKey, out var importId) && !string.IsNullOrEmpty(importId))
                edit.Id = importId;

            edits.Add(edit);
            return;
        }

        var addEdge = AddEdgeRegex.Match(line);
        if (addEdge.Success)
        {
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.AddEdge,
                Type = "edge",
                Id = StagedIds.ForEdge(lineIndex),
                Label = Unquote(addEdge.Groups["label"].Value),
                Source = Unescape(addEdge.Groups["source"].Value),
                Target = Unescape(addEdge.Groups["target"].Value),
                Properties = ParseProperties(addEdge.Groups["props"].Value)
            });

            return;
        }

        var dropNode = DropNodeRegex.Match(line);
        if (dropNode.Success)
        {
            edits.Add(new GraphEdit { Kind = GraphEditKind.RemoveNode, Type = "node", Id = Unescape(dropNode.Groups["id"].Value) });
            return;
        }

        var dropEdge = DropEdgeRegex.Match(line);
        if (dropEdge.Success)
        {
            edits.Add(new GraphEdit { Kind = GraphEditKind.RemoveEdge, Type = "edge", Id = Unescape(dropEdge.Groups["id"].Value) });
            return;
        }

        if (TryParseProperty(line, SetNodePropertyRegex, GraphEditKind.SetProperty, "node", edits))
            return;

        if (TryParseProperty(line, SetEdgePropertyRegex, GraphEditKind.SetProperty, "edge", edits))
            return;

        if (TryParseProperty(line, DropNodePropertyRegex, GraphEditKind.DropProperty, "node", edits))
            return;

        TryParseProperty(line, DropEdgePropertyRegex, GraphEditKind.DropProperty, "edge", edits);
    }

    //Node and relationship property edits differ only in how the element is bound, so one reader covers
    //both once the caller has picked the pattern.
    private static bool TryParseProperty(string line, Regex regex, GraphEditKind kind, string type, List<GraphEdit> edits)
    {
        var match = regex.Match(line);

        if (!match.Success)
            return false;

        var edit = new GraphEdit
        {
            Kind = kind,
            Type = type,
            Id = Unescape(match.Groups["id"].Value),
            Key = Unquote(match.Groups["key"].Value)
        };

        if (kind == GraphEditKind.SetProperty)
            edit.Value = Unescape(match.Groups["value"].Value);

        edits.Add(edit);

        return true;
    }

    private static Dictionary<string, string> ParseProperties(string map)
    {
        var properties = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(map))
            return properties;

        foreach (Match match in PropertyPairRegex.Matches(map))
        {
            string key;
            if (match.Groups["bare"].Success)
                key = match.Groups["bare"].Value;
            else
                key = Unquote(match.Groups[1].Value);

            properties[key] = Unescape(match.Groups[2].Value);
        }

        return properties;
    }

    //Reverses CypherQueryBuilder.Escape.
    private static string Unescape(string value)
    {
        return (value ?? string.Empty).Replace("\\'", "'").Replace("\\\\", "\\");
    }

    //Reverses the doubling CypherQueryBuilder.QuoteIdentifier applies inside backticks.
    private static string Unquote(string identifier)
    {
        return (identifier ?? string.Empty).Replace("``", "`");
    }
}
