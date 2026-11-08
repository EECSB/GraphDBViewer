using System;
using System.Collections.Generic;
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
    ///True when the query contains a data-modification operation where one can stand: not inside a string, a quoted
    ///name or a comment, and not as an attribute (after a dot), a bind parameter (after an at sign), an object's key
    ///(before a colon) or part of a longer name. So a document literally named 'REMOVE', an attribute
    ///<c>`update`</c> or a call of <c>REGEX_REPLACE</c> doesn't read as a mutation; until 2026-10-03 AQL's own
    ///REGEX_REPLACE, REMOVE_VALUE and REPLACE_NTH did.
    ///</summary>
    public static bool IsMutating(string query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        var tokens = Tokens(query);

        for (int i = 0; i < tokens.Count; i++)
            if (IsOperationWord(tokens, i) && WriteKeywords.Contains(tokens[i].Text))
                return true;

        return false;
    }

    //Whether the word at i stands where an operation can, rather than naming an attribute, a bind parameter or an
    //object's key.
    private static bool IsOperationWord(List<Token> tokens, int i)
    {
        if (tokens[i].Kind != TokenKind.Word)
            return false;

        if (i > 0 && tokens[i - 1].Kind == TokenKind.Symbol && tokens[i - 1].Text is "." or "@")
            return false;

        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Symbol && tokens[i + 1].Text == ":")
            return false;

        return true;
    }

    private enum TokenKind
    {
        Word,
        Quoted,
        Symbol
    }

    //One piece of a query: a word, a string or quoted name (whose text is never read, so nothing inside one can
    //look like an operation), or one character of punctuation.
    private readonly record struct Token(TokenKind Kind, string Text);

    //The query as tokens, with its comments and spaces left out. A word is letters, digits and underscores, so
    //REGEX_REPLACE is one name rather than REPLACE and something before it.
    private static List<Token> Tokens(string query)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < query.Length)
        {
            char c = query[i];

            if (char.IsWhiteSpace(c))
                i++;
            else if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
                i = PastLine(query, i);
            else if (c == '/' && i + 1 < query.Length && query[i + 1] == '*')
                i = PastBlockComment(query, i);
            else if (c == '\'' || c == '"' || c == '`' || c == '´')
            {
                i = PastQuoted(query, i);
                tokens.Add(new Token(TokenKind.Quoted, ""));
            }
            else if (IsWordChar(c))
            {
                int start = i;

                while (i < query.Length && IsWordChar(query[i]))
                    i++;

                tokens.Add(new Token(TokenKind.Word, query.Substring(start, i - start)));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    private static int PastLine(string query, int i)
    {
        while (i < query.Length && query[i] != '\n')
            i++;

        return i;
    }

    private static int PastBlockComment(string query, int i)
    {
        i += 2;

        while (i + 1 < query.Length && !(query[i] == '*' && query[i + 1] == '/'))
            i++;

        return i + 2;
    }

    //Just past the string or quoted name opening at i: AQL quotes a name in backticks or forward ticks, and a
    //backslash escapes inside a string.
    private static int PastQuoted(string query, int i)
    {
        char quote = query[i];
        i++;

        while (i < query.Length)
            if ((quote == '\'' || quote == '"') && query[i] == '\\')
                i += 2;
            else if (query[i] == quote)
                return i + 1;
            else
                i++;

        return i;
    }

    //── Staged-edit parsing ─────────────────────────────────────────────

    private const string Literal = @"'((?:[^'\\]|\\.)*)'";
    //A backslash escapes inside the backticks too, as AqlQueryBuilder.QuoteIdentifier writes a name.
    private const string Identifier = @"`((?:[^`\\]|\\.)*)`";

    private static readonly Regex AddVertexRegex = new(
        $@"^INSERT\s*\{{(?<fields>.*)\}}\s*INTO\s+{Identifier}\s*RETURN\s+NEW$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RemoveRegex = new(
        $@"^REMOVE\s+{Literal}\s+IN\s+{Identifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetPropertyRegex = new(
        $@"^UPDATE\s+{Literal}\s+WITH\s*\{{\s*`(?<key>(?:[^`\\]|\\.)*)`\s*:\s*'(?<value>(?:[^'\\]|\\.)*)'\s*\}}\s*IN\s+{Identifier}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DropPropertyRegex = new(
        $@"^UPDATE\s+{Literal}\s+WITH\s*\{{\s*`(?<key>(?:[^`\\]|\\.)*)`\s*:\s*null\s*\}}\s*IN\s+{Identifier}\s*OPTIONS.*$",
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
                Key = Unquote(drop.Groups["key"].Value)
            });

            return;
        }

        var set = SetPropertyRegex.Match(line);
        if (set.Success)
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.SetProperty,
                Type = "node",
                Id = Unquote(set.Groups[2].Value) + "/" + Unescape(set.Groups[1].Value),
                Key = Unquote(set.Groups["key"].Value),
                Value = Unescape(set.Groups["value"].Value)
            });
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
        return StagedLiteral.Unescape(value);
    }

    //Reverses the escaping AqlQueryBuilder.QuoteIdentifier applies inside backticks, which is a literal's.
    private static string Unquote(string identifier)
    {
        return StagedLiteral.Unescape(identifier);
    }
}
