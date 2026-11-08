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
    //The writing clauses, and the commands that change the database itself: users, roles, privileges, databases,
    //transactions. A read-only query contains none of them. FOREACH is not listed because what it does is decided
    //by what is written inside it, and a write there is caught by these anyway; a CALL is judged by the procedure
    //it names.
    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "MERGE", "SET", "DELETE", "DETACH", "REMOVE", "DROP", "LOAD",
        "ALTER", "GRANT", "DENY", "REVOKE", "RENAME", "START", "STOP", "TERMINATE"
    };

    //The procedures known to read and nothing more, on Neo4j and on Memgraph: the schema and the server describing
    //themselves, and full-text search. A name ending in a dot stands for every procedure under it. Any other is
    //taken for a write, since a guard cannot see into a procedure and a writing one need not say so in its name:
    //db.createLabel does not, and apoc.periodic.iterate carries its writes in strings.
    private static readonly string[] ReadingProcedures =
    [
        "db.labels", "db.relationshiptypes", "db.propertykeys", "db.schema.", "db.indexes", "db.constraints",
        "db.info", "db.ping", "db.index.fulltext.querynodes", "db.index.fulltext.queryrelationships",
        "dbms.components", "dbms.procedures", "dbms.functions", "apoc.meta.", "apoc.help",
        "mg.procedures", "mg.functions", "schema.node_type_properties", "schema.rel_type_properties"
    ];

    ///<summary>
    ///True when the query contains a writing clause, or calls a procedure not known to read. A word counts only
    ///where a clause can stand: not inside a string, a backticked identifier or a comment, and not as a property
    ///(after a dot), a label or relationship type (after a colon), a parameter (after a dollar sign), a map key
    ///(before a colon) or part of a longer name. So a node literally named 'DELETE', a property <c>n.set</c>, a
    ///label <c>:Load</c> or a key <c>{remove: 1}</c> doesn't read as a mutation. Until 2026-10-03 every one of
    ///those but the first did, and a CALL of <c>db.createLabel</c> did not.
    ///</summary>
    public static bool IsMutating(string query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        var tokens = Tokens(query);

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!IsClauseWord(tokens, i))
                continue;

            if (WriteKeywords.Contains(tokens[i].Text))
                return true;

            if (string.Equals(tokens[i].Text, "CALL", StringComparison.OrdinalIgnoreCase) && CallsAWrite(tokens, i + 1))
                return true;
        }

        return false;
    }

    //Whether the word at i stands where a clause can, rather than naming a property, a label or relationship type,
    //a parameter or a map key.
    private static bool IsClauseWord(List<Token> tokens, int i)
    {
        if (tokens[i].Kind != TokenKind.Word)
            return false;

        if (i > 0 && tokens[i - 1].Kind == TokenKind.Symbol && tokens[i - 1].Text is "." or ":" or "$")
            return false;

        if (i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Symbol && tokens[i + 1].Text == ":")
            return false;

        return true;
    }

    //Whether the CALL whose procedure name starts at the given token calls one not known to read. A CALL that opens
    //a subquery names no procedure, and what is inside it is read like the rest of the query.
    private static bool CallsAWrite(List<Token> tokens, int start)
    {
        if (start >= tokens.Count)
            return false;

        if (tokens[start].Kind == TokenKind.Symbol && (tokens[start].Text == "{" || tokens[start].Text == "("))
            return false;

        //A backticked name is not looked into: unknown, so taken for a write.
        if (tokens[start].Kind != TokenKind.Word)
            return true;

        var name = new StringBuilder(tokens[start].Text);

        for (int i = start + 1; i + 1 < tokens.Count && tokens[i].Text == "." && tokens[i + 1].Kind == TokenKind.Word; i += 2)
            name.Append('.').Append(tokens[i + 1].Text);

        var procedure = name.ToString().ToLowerInvariant();

        return !ReadingProcedures.Any(known => procedure == known || (known.EndsWith('.') && procedure.StartsWith(known, StringComparison.Ordinal)));
    }

    private enum TokenKind
    {
        Word,
        Quoted,
        Symbol
    }

    //One piece of a query: a word, a quoted string or backticked identifier (whose text is never read, so nothing
    //inside one can look like a clause), or one character of punctuation.
    private readonly record struct Token(TokenKind Kind, string Text);

    //The query as tokens, with its comments and spaces left out. A word is letters, digits and underscores, so
    //load_factor is one name rather than LOAD and something after it.
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
            else if (c == '\'' || c == '"' || c == '`')
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

    //Just past the string or backticked identifier opening at i. A backslash escapes inside a string; a backtick
    //is escaped by doubling it.
    private static int PastQuoted(string query, int i)
    {
        char quote = query[i];
        i++;

        while (i < query.Length)
            if (quote != '`' && query[i] == '\\')
                i += 2;
            else if (quote == '`' && query[i] == '`' && i + 1 < query.Length && query[i + 1] == '`')
                i += 2;
            else if (query[i] == quote)
                return i + 1;
            else
                i++;

        return i;
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
        return StagedLiteral.Unescape(value);
    }

    //Reverses the doubling CypherQueryBuilder.QuoteIdentifier applies inside backticks.
    private static string Unquote(string identifier)
    {
        return (identifier ?? string.Empty).Replace("``", "`");
    }
}
