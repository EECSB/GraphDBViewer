using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads AQL back: whether a statement would change the graph, and what the staged "Generated" buffer
///amounts to. The AQL counterpart of <see cref="GremlinStepParser.IsMutating"/> and
///<see cref="GremlinEditParser"/>.
///</summary>
public static class AqlStatementParser
{
    //AQL's data-modification operations. A read-only query contains none of them.
    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "REPLACE", "REMOVE", "UPSERT"
    };

    ///<summary>
    ///True when the query contains a data-modification operation outside a string, a backticked identifier
    ///or a comment — so a document literally named 'REMOVE', or an attribute <c>`update`</c>, doesn't read
    ///as a mutation.
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
                    if (quote != '`' && query[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (query[i] == quote)
                        break;

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

    private const string Literal = @"'((?:[^'\\]|\\.)*)'";
    private const string Identifier = "`([^`]*)`";

    private static readonly Regex AddVertexRegex = new(
        $@"^INSERT\s*\{{(?<fields>.*)\}}\s*INTO\s+{Identifier}\s*RETURN\s+NEW$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RemoveRegex = new(
        $@"^REMOVE\s+{Literal}\s+IN\s+{Identifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetPropertyRegex = new(
        $@"^UPDATE\s+{Literal}\s+WITH\s*\{{\s*`(?<key>[^`]*)`\s*:\s*'(?<value>(?:[^'\\]|\\.)*)'\s*\}}\s*IN\s+{Identifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropPropertyRegex = new(
        $@"^UPDATE\s+{Literal}\s+WITH\s*\{{\s*`(?<key>[^`]*)`\s*:\s*null\s*\}}\s*IN\s+{Identifier}\s*OPTIONS.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //One `key`: 'value' pair inside an object literal, plus the bare _key / _from / _to AQL attributes.
    private static readonly Regex FieldRegex = new(
        $@"(?:{Identifier}|(?<bare>_key|_from|_to|[A-Za-z_]\w*))\s*:\s*{Literal}",
        RegexOptions.Compiled);

    ///<summary>
    ///Parses the query shapes <see cref="AqlQueryBuilder"/> emits into <see cref="GraphEdit"/>s, so the
    ///canvas can preview uncommitted changes. Best-effort and line-oriented like the other parsers: an
    ///unrecognized statement is skipped, stays staged, and commits normally.
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
        var insert = AddVertexRegex.Match(line);
        if (insert.Success)
        {
            var collection = Unquote(insert.Groups[1].Value);
            var fields = ParseFields(insert.Groups["fields"].Value);

            //An INSERT carrying _from / _to is an edge; anything else is a document.
            if (fields.TryGetValue("_from", out var from) && fields.TryGetValue("_to", out var to))
            {
                fields.Remove("_from");
                fields.Remove("_to");

                edits.Add(new GraphEdit
                {
                    Kind = GraphEditKind.AddEdge,
                    Type = "edge",
                    Id = StagedIds.ForEdge(lineIndex),
                    Label = collection,
                    Source = from,
                    Target = to,
                    Properties = fields
                });

                return;
            }

            var edit = new GraphEdit
            {
                Kind = GraphEditKind.AddNode,
                Type = "node",
                Id = StagedIds.ForVertex(lineIndex),
                Label = collection,
                Properties = fields
            };

            //A document created with an explicit key already knows the id it will have, so the preview can
            //use it — which is what lets an imported edge line up with the node it references.
            if (fields.TryGetValue("_key", out var key) && !string.IsNullOrEmpty(key))
            {
                edit.Id = collection + "/" + key;
                edit.Properties.Remove("_key");
            }

            edits.Add(edit);
            return;
        }

        var remove = RemoveRegex.Match(line);
        if (remove.Success)
        {
            var id = Unquote(remove.Groups[2].Value) + "/" + Unescape(remove.Groups[1].Value);

            //A REMOVE names a collection, not a kind, so which it is has to come from the graph itself —
            //RemoveNode also clears incident edges, which would be wrong for an edge.
            edits.Add(new GraphEdit { Kind = GraphEditKind.RemoveNode, Type = "node", Id = id });
            return;
        }

        //Ordered: the drop shape is an UPDATE too, and its trailing OPTIONS is what distinguishes it.
        var drop = DropPropertyRegex.Match(line);
        if (drop.Success)
        {
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.DropProperty,
                Type = "node",
                Id = Unquote(drop.Groups[2].Value) + "/" + Unescape(drop.Groups[1].Value),
                Key = drop.Groups["key"].Value
            });

            return;
        }

        var set = SetPropertyRegex.Match(line);
        if (set.Success)
        {
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.SetProperty,
                Type = "node",
                Id = Unquote(set.Groups[2].Value) + "/" + Unescape(set.Groups[1].Value),
                Key = set.Groups["key"].Value,
                Value = Unescape(set.Groups["value"].Value)
            });
        }
    }

    private static Dictionary<string, string> ParseFields(string fields)
    {
        var parsed = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(fields))
            return parsed;

        foreach (Match match in FieldRegex.Matches(fields))
        {
            string key;
            if (match.Groups["bare"].Success)
                key = match.Groups["bare"].Value;
            else
                key = Unquote(match.Groups[1].Value);

            parsed[key] = Unescape(match.Groups[2].Value);
        }

        return parsed;
    }

    //Reverses AqlQueryBuilder.Escape.
    private static string Unescape(string value)
    {
        return (value ?? string.Empty).Replace("\\'", "'").Replace("\\\\", "\\");
    }

    private static string Unquote(string identifier)
    {
        return identifier ?? string.Empty;
    }
}
