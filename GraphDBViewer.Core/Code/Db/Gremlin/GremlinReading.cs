using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///Whether a Gremlin query only reads, judged by Gremlin's own grammar rather than by looking for the words of a write.
///
///A Gremlin server runs a query sent as text as a Groovy script, and Groovy is a whole language. Until 2026-10-23 the
///check looked for a mutating step's name outside quoted text and knew only Gremlin's two quotes, so a write behind
///anything else Groovy reads as text or as nothing passed as a read: <c>g.V()/*'*/.drop()</c>, a triple-quoted or a
///slashy string, a <c>${…}</c> that Groovy runs inside a double-quoted one. And looking for the words of a write cannot
///see one that has none: <c>g.getClass()</c> hands over Java's reflection, and from there the server's processes.
///
///So a read is what this reads <strong>whole</strong> as one traversal in Gremlin's grammar, the one TinkerPop's
///gremlin-language draws: <c>g</c>, its configuration and its start step, then steps, each a name Gremlin's traversal
///has and a reading one, and at most one terminal step at the end; arguments that are literals, lists and maps of
///them, anonymous traversals, predicates and enum values. Everything else is refused with its reason: a character
///Gremlin's grammar has no use for, a name that is not one of its steps, a step that writes, a graph computer. What
///passes is read by Groovy the way it is read here, because each construct the two could read differently fails here
///first, and a method of anything but a traversal, a predicate or an enum is never reached.
///
///Refusing a little that is safe is the price of the rule being one: a closure that only reads is refused with the
///closure that removes, and a step of one vendor's that is not TinkerPop's is refused until it is named here.
///</summary>
public static class GremlinReading
{
    ///<summary>The reason given for a step that writes, which a caller may follow with where to make the change instead.</summary>
    public const string Writes = "That query changes the graph, which is not allowed here: only a read is run.";

    private enum Kind
    {
        Name,
        Number,
        Text,
        Mark,
        End
    }

    private readonly record struct Token(Kind Kind, string Text, int At);

    ///<summary>The steps that write, by any receiver: the traversal's, an element's <c>remove()</c>, the graph's own <c>addVertex</c>, and <c>io()</c>.</summary>
    private static readonly HashSet<string> Writing = new(System.StringComparer.Ordinal)
    {
        "addV", "addE", "drop", "property", "mergeV", "mergeE", "remove", "addVertex", "addEdge", "io"
    };

    ///<summary>Names refused for a reason of their own, said whatever the name is called on.</summary>
    private static readonly Dictionary<string, string> Refused = new(System.StringComparer.Ordinal)
    {
        ["withComputer"] = Computer,
        ["pageRank"] = Computer,
        ["peerPressure"] = Computer,
        ["connectedComponent"] = Computer,
        ["shortestPath"] = Computer,
        ["program"] = Computer,
        ["withStrategies"] = Strategies,
        ["withoutStrategies"] = Strategies,
        ["call"] = "call() runs a service of the server's, which may write, so it is not run as a read."
    };

    private const string Computer = "A graph computer may write what it computes into the graph, as JanusGraph's does, so withComputer() and its algorithms are not run as a read.";

    private const string Strategies = "A traversal's strategies are the server's to set, so withStrategies() and withoutStrategies() are not run as a read.";

    ///<summary>What may come between <c>g</c> and the start step.</summary>
    private static readonly HashSet<string> Configuring = new(System.StringComparer.Ordinal)
    {
        "with", "withBulk", "withPath", "withSack", "withSideEffect"
    };

    ///<summary>The steps a traversal may start with, off <c>g</c> or off <c>__</c>.</summary>
    private static readonly HashSet<string> Starting = new(System.StringComparer.Ordinal)
    {
        "V", "E", "inject", "union", "start"
    };

    ///<summary>
    ///TinkerPop's reading steps, the modulators among them; <c>subgraph()</c> collects edges into a side-effect graph in
    ///the traverser and writes nothing. And <c>datetime()</c>, gremlin-language's function for a date literal, which is
    ///read here as a bare call like a step is.
    ///</summary>
    private static readonly HashSet<string> Steps = new(System.StringComparer.Ordinal)
    {
        "V", "E", "aggregate", "all", "and", "any", "as", "asDate", "asString", "barrier", "both", "bothE", "bothV",
        "branch", "by", "cap", "choose", "coalesce", "coin", "combine", "concat", "conjoin", "constant", "count",
        "cyclicPath", "dateAdd", "dateDiff", "datetime", "dedup", "difference", "disjunct", "element", "elementMap",
        "emit", "fail", "filter", "flatMap", "fold", "format", "from", "group", "groupCount", "has", "hasId", "hasKey",
        "hasLabel", "hasNot", "hasValue", "id", "identity", "in", "inE", "inV", "index", "inject", "intersect", "is",
        "key", "label", "length", "limit", "local", "loops", "lTrim", "map", "match", "math", "max", "mean", "merge",
        "min", "none", "not", "option", "optional", "or", "order", "otherV", "out", "outE", "outV", "path", "product",
        "profile", "project", "properties", "propertyMap", "range", "repeat", "replace", "reverse", "rTrim", "sack",
        "sample", "select", "sideEffect", "simplePath", "skip", "split", "store", "subgraph", "substring", "sum", "tail",
        "timeLimit", "times", "to", "toLower", "toUpper", "tree", "trim", "unfold", "union", "until", "value", "valueMap",
        "values", "where", "with"
    };

    ///<summary>What may be called on what a bare call answers: a step, or <c>negate()</c> when it was a predicate.</summary>
    private static readonly HashSet<string> Following = new(Steps, System.StringComparer.Ordinal)
    {
        "negate"
    };

    ///<summary>The steps that end a traversal and answer it: only last, since what they answer is no longer a traversal.</summary>
    private static readonly HashSet<string> Terminals = new(System.StringComparer.Ordinal)
    {
        "toList", "toSet", "toBulkSet", "next", "tryNext", "hasNext", "iterate", "explain"
    };

    ///<summary>The predicates, as <c>P</c>, <c>TextP</c> and JanusGraph's <c>Text</c> and <c>Geo</c> name them, and bare.</summary>
    private static readonly Dictionary<string, HashSet<string>> Predicates = new(System.StringComparer.Ordinal)
    {
        ["P"] = new(System.StringComparer.Ordinal)
        {
            "eq", "neq", "lt", "lte", "gt", "gte", "inside", "outside", "between", "within", "without", "not"
        },
        ["TextP"] = new(System.StringComparer.Ordinal)
        {
            "containing", "notContaining", "startingWith", "notStartingWith", "endingWith", "notEndingWith", "regex",
            "notRegex"
        },
        ["Text"] = new(System.StringComparer.Ordinal)
        {
            "textContains", "textNotContains", "textContainsPrefix", "textNotContainsPrefix", "textContainsRegex",
            "textNotContainsRegex", "textContainsFuzzy", "textNotContainsFuzzy", "textContainsPhrase",
            "textNotContainsPhrase", "textPrefix", "textNotPrefix", "textRegex", "textNotRegex", "textFuzzy", "textNotFuzzy"
        },
        ["Geo"] = new(System.StringComparer.Ordinal)
        {
            "geoIntersect", "geoWithin", "geoDisjoint", "geoContains"
        },
        ["Geoshape"] = new(System.StringComparer.Ordinal)
        {
            "point", "circle", "box"
        }
    };

    ///<summary>What a predicate may be joined with.</summary>
    private static readonly HashSet<string> Joining = new(System.StringComparer.Ordinal)
    {
        "and", "or", "negate"
    };

    ///<summary>The enums' values, by the enum that names them.</summary>
    private static readonly Dictionary<string, HashSet<string>> Enums = new(System.StringComparer.Ordinal)
    {
        ["T"] = new(System.StringComparer.Ordinal) { "id", "label", "key", "value" },
        ["Order"] = new(System.StringComparer.Ordinal) { "asc", "desc", "shuffle", "incr", "decr" },
        ["Scope"] = new(System.StringComparer.Ordinal) { "local", "global" },
        ["Column"] = new(System.StringComparer.Ordinal) { "keys", "values" },
        ["Direction"] = new(System.StringComparer.Ordinal) { "OUT", "IN", "BOTH", "from", "to" },
        ["Pop"] = new(System.StringComparer.Ordinal) { "first", "last", "all", "mixed" },
        ["Cardinality"] = new(System.StringComparer.Ordinal) { "single", "list", "set" },
        ["Operator"] = new(System.StringComparer.Ordinal)
        {
            "sum", "minus", "mult", "div", "min", "max", "assign", "and", "or", "addAll", "sumLong"
        },
        ["Pick"] = new(System.StringComparer.Ordinal) { "any", "none", "unproductive" },
        ["Merge"] = new(System.StringComparer.Ordinal) { "onCreate", "onMatch", "outV", "inV" },
        ["DT"] = new(System.StringComparer.Ordinal) { "second", "minute", "hour", "day" },
        ["Barrier"] = new(System.StringComparer.Ordinal) { "normSack" },
        ["WithOptions"] = new(System.StringComparer.Ordinal)
        {
            "tokens", "none", "ids", "labels", "keys", "values", "all", "indexer", "list", "map"
        }
    };

    ///<summary>The enum values Gremlin's Groovy imports bare, as in <c>by(desc)</c> or <c>select(keys)</c>.</summary>
    private static readonly HashSet<string> Bare = new(System.StringComparer.Ordinal)
    {
        "id", "label", "key", "value", "asc", "desc", "shuffle", "incr", "decr", "local", "global", "keys", "values",
        "OUT", "IN", "BOTH", "first", "last", "all", "mixed", "single", "list", "set", "sum", "minus", "mult", "div",
        "min", "max", "assign", "addAll", "sumLong", "any", "none"
    };

    ///<summary>Why this query is not a read, or <c>null</c> when it is one.</summary>
    public static string Refusal(string query)
    {
        var text = (query ?? "").Trim();

        if (text.Length == 0)
            return "There is no query to run.";

        //A comment before the traversal is passed over, as Lex passes every comment; the traversal still starts at g.
        var traversal = text.Substring(LeadingCommentsEnd(text));

        if (traversal.Length == 0)
            return "There is no query to run.";

        if (!StartsAtG(traversal))
            return "A read has to be one traversal starting at g, in Gremlin's own grammar: anything else is a Groovy script.";

        var tokens = new List<Token>();
        var refusal = Lex(text, tokens);

        if (refusal != null)
            return refusal;

        //Before the grammar, so a write is called one wherever it stands, behind a terminal step or in an argument.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Name || !IsMark(tokens[i + 1], "("))
                continue;

            if (Writing.Contains(tokens[i].Text))
                return Writes;

            if (Refused.TryGetValue(tokens[i].Text, out var reason))
                return reason;
        }

        return new Reader(tokens).Query();
    }

    ///<summary>Whether the query is a read. The negative of <see cref="Refusal"/>, for a caller that only wants the answer.</summary>
    public static bool Reads(string query)
    {
        return Refusal(query) == null;
    }

    ///<summary>
    ///Where the text's leading comments end, with the space around them: a <c>//</c> comment at its line's end, a
    ///<c>/*</c> comment at its close. One that never closes is left where it stands, for Lex to refuse.
    ///</summary>
    private static int LeadingCommentsEnd(string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            if (IsSpace(text[i]))
                i++;
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                    i++;
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);

                if (close < 0)
                    return i;

                i = close + 2;
            }
            else
                return i;
        }

        return i;
    }

    ///<summary>
    ///Whether the traversal begins at <c>g</c>: the bare name, followed by a dot and nothing else. <c>graph.traversal()</c>
    ///and <c>gremlin.something</c> both fail it, which is the point: the identifier has to be exactly <c>g</c>.
    ///</summary>
    private static bool StartsAtG(string text)
    {
        if (text[0] != 'g')
            return false;

        var i = 1;

        while (i < text.Length && IsSpace(text[i]))
            i++;

        return i < text.Length && text[i] == '.';
    }

    //── Words ───────────────────────────────────────────────────────────

    ///<summary>
    ///The query's tokens, or the reason one could not be read. Only what Gremlin's grammar has: names, numbers, strings
    ///in one or the other quote, and <c>. ( ) , [ ] : -</c>. A comment is passed over as Groovy passes it, <c>//</c> to
    ///the line's end and <c>/*</c> to its close, and read for nothing (2026-11-07; the debugger had refused a query that
    ///opened with one). The rest is refused here, each where Groovy would read it as something this would not.
    ///</summary>
    private static string Lex(string text, List<Token> tokens)
    {
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (IsSpace(c))
                i++;
            else if (IsNameStart(c))
            {
                var start = i;

                while (i < text.Length && IsNamePart(text[i]))
                    i++;

                tokens.Add(new Token(Kind.Name, text.Substring(start, i - start), start));
            }
            else if (IsDigit(c))
            {
                var start = i;

                i = NumberEnd(text, i);
                tokens.Add(new Token(Kind.Number, text.Substring(start, i - start), start));
            }
            else if (c == '\'' || c == '"')
            {
                var start = i;
                var refusal = TextEnd(text, ref i);

                if (refusal != null)
                    return refusal;

                tokens.Add(new Token(Kind.Text, text.Substring(start, i - start), start));
            }
            else if (c == ';')
                return "One traversal at a time: a ; would start a second statement.";
            else if (c == '{' || c == '}')
                return "A closure is not read-only whatever it looks like: it can change what it is handed. Use the traversal steps instead.";
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                //A line comment, Groovy's: passed over to the line's end, as Groovy passes it, and read for nothing.
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                    i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                //A block comment, to its close; one that never closes is no comment Groovy would pass over either.
                var close = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);

                if (close < 0)
                    return "A /* comment never closes.";

                i = close + 2;
            }
            else if (c == '/')
                return "Gremlin's grammar has no / outside a string. In Groovy it starts a slashy string, which could hide a write.";
            else if (".(),[]:-".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(Kind.Mark, c.ToString(), i));
                i++;
            }
            else
                return $"{Shown(c)} at character {i + 1} is not part of Gremlin's grammar.";
        }

        tokens.Add(new Token(Kind.End, "", text.Length));

        return null;
    }

    ///<summary>
    ///Moves past a string opening at <paramref name="i"/>, or says why it is not one. A <c>$</c> in a double-quoted string
    ///is Groovy's: <c>"${…}"</c> runs what it holds. A <c>\u</c> escape is refused because a reader that turns it into a
    ///character before the string is read would see the string end there. A triple quote opens Groovy's multi-line
    ///string, which this would read as an empty string and another.
    ///</summary>
    private static string TextEnd(string text, ref int i)
    {
        var quote = text[i];

        if (i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote)
            return "A triple-quoted string is Groovy's, not Gremlin's: write the value in single quotes.";

        i++;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == quote)
            {
                i++;

                return null;
            }
            else if (c == '\\')
            {
                if (i + 1 < text.Length && text[i + 1] == 'u')
                    return "A \\u escape is not read here, since Groovy may read it as the character it names, a quote among them: type the character itself.";

                if (i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
                    return "A string has to close on the line it opens on.";

                i += 2;
            }
            else if (c == '$' && quote == '"')
                return "Groovy runs what a $ opens inside a double-quoted string, so a read writes the value in single quotes.";
            else if (c == '\n' || c == '\r')
                return "A string has to close on the line it opens on.";
            else
                i++;
        }

        return "A string is not closed.";
    }

    ///<summary>Where a number starting at <paramref name="i"/> ends: digits, a fraction, an exponent and Groovy's type suffix.</summary>
    private static int NumberEnd(string text, int i)
    {
        while (i < text.Length && IsDigit(text[i]))
            i++;

        if (i + 1 < text.Length && text[i] == '.' && IsDigit(text[i + 1]))
        {
            i++;

            while (i < text.Length && IsDigit(text[i]))
                i++;
        }

        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var j = i + 1;

            if (j < text.Length && (text[j] == '+' || text[j] == '-'))
                j++;

            if (j < text.Length && IsDigit(text[j]))
            {
                i = j;

                while (i < text.Length && IsDigit(text[i]))
                    i++;
            }
        }

        if (i < text.Length && "lLdDfFgGiI".IndexOf(text[i]) >= 0)
            i++;

        return i;
    }

    //ASCII only, here and in names: a character Groovy might read as part of a name or as nothing is refused instead.
    private static bool IsSpace(char c)
    {
        return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f';
    }

    private static bool IsDigit(char c)
    {
        return c >= '0' && c <= '9';
    }

    private static bool IsNameStart(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    }

    private static bool IsNamePart(char c)
    {
        return IsNameStart(c) || IsDigit(c);
    }

    private static bool IsMark(Token token, string mark)
    {
        return token.Kind == Kind.Mark && token.Text == mark;
    }

    private static string Shown(char c)
    {
        if (c < ' ' || c > '~')
            return $"The character U+{(int)c:X4}";

        return $"'{c}'";
    }

    //── Grammar ─────────────────────────────────────────────────────────

    ///<summary>One pass over the tokens, in the grammar's order: <c>g</c>, configuration, the start, steps, a terminal.</summary>
    private sealed class Reader
    {
        private readonly List<Token> _tokens;
        private int _at;

        public Reader(List<Token> tokens)
        {
            _tokens = tokens;
        }

        private Token Next => _tokens[_at];

        private Token After => _tokens[System.Math.Min(_at + 1, _tokens.Count - 1)];

        public string Query()
        {
            //g, which StartsAtG has already seen.
            _at++;

            var started = false;

            while (IsMark(Next, "."))
            {
                _at++;

                if (Next.Kind != Kind.Name)
                    return Expected("a step's name after the dot");

                var step = Next;

                if (!started && Configuring.Contains(step.Text))
                {
                }
                else if (!started && Starting.Contains(step.Text))
                    started = true;
                else if (started && Terminals.Contains(step.Text))
                {
                    _at++;

                    var answered = Arguments(step);

                    if (answered != null)
                        return answered;

                    if (Next.Kind != Kind.End)
                        return $"Nothing can follow {step.Text}(): what it answers is no longer a traversal.";

                    return null;
                }
                else if (started && Steps.Contains(step.Text))
                {
                }
                else if (!started && (Steps.Contains(step.Text) || Terminals.Contains(step.Text)))
                    return $"A traversal starts at g with V(), E() or inject(), and {step.Text}() comes before any of them.";
                else
                    return NotAStep(step);

                _at++;

                var refusal = Arguments(step);

                if (refusal != null)
                    return refusal;
            }

            if (Next.Kind != Kind.End)
                return Unexpected(Next);

            if (!started)
                return "A traversal starts at g with V(), E() or inject(), and this one has none of them.";

            return null;
        }

        ///<summary>A step's brackets and what is between them.</summary>
        private string Arguments(Token step)
        {
            if (!IsMark(Next, "("))
                return $"{step.Text} needs its brackets: as a name alone, Groovy reads it as a property of whatever is before it.";

            _at++;

            if (IsMark(Next, ")"))
            {
                _at++;

                return null;
            }

            while (true)
            {
                var refusal = Argument();

                if (refusal != null)
                    return refusal;

                if (IsMark(Next, ","))
                    _at++;
                else if (IsMark(Next, ")"))
                {
                    _at++;

                    return null;
                }
                else
                    return Unexpected(Next);
            }
        }

        ///<summary>One argument: a literal, a list or map, an anonymous traversal, a predicate or an enum's value.</summary>
        private string Argument()
        {
            var token = Next;

            if (token.Kind == Kind.Text || token.Kind == Kind.Number)
            {
                _at++;

                return null;
            }
            else if (IsMark(token, "-"))
            {
                _at++;

                if (Next.Kind != Kind.Number)
                    return Expected("a number after the minus");

                _at++;

                return null;
            }
            else if (IsMark(token, "["))
                return Collection();
            else if (token.Kind != Kind.Name)
                return Unexpected(token);

            if (token.Text == "true" || token.Text == "false" || token.Text == "null")
            {
                _at++;

                return null;
            }
            else if (token.Text == "__")
            {
                _at++;

                if (!IsMark(Next, "."))
                    return Expected("a step after __");

                return Chain(Steps, true);
            }
            else if (Predicates.TryGetValue(token.Text, out var named))
            {
                _at++;

                if (!IsMark(Next, ".") || After.Kind != Kind.Name || !named.Contains(After.Text))
                    return $"After {token.Text}. comes one of its predicates, and nothing else of it is read here.";

                _at++;

                var predicate = Next;

                _at++;

                var refusal = Arguments(predicate);

                if (refusal != null)
                    return refusal;

                return Chain(Joining, false);
            }
            else if (Enums.TryGetValue(token.Text, out var values))
            {
                _at++;

                if (!IsMark(Next, ".") || After.Kind != Kind.Name || !values.Contains(After.Text))
                    return $"After {token.Text}. comes one of its values, and nothing else of it is read here.";

                _at += 2;

                return null;
            }
            else if (IsMark(After, "("))
            {
                if (!Steps.Contains(token.Text) && !IsPredicate(token.Text))
                    return NotAStep(token);

                _at++;

                var refusal = Arguments(token);

                if (refusal != null)
                    return refusal;

                return Chain(Following, false);
            }
            else if (Bare.Contains(token.Text))
            {
                _at++;

                return null;
            }

            return $"{token.Text} is not a value Gremlin's grammar has: a name alone is a Groovy variable.";
        }

        ///<summary>
        ///The calls joined on with dots, each a name of <paramref name="names"/>: the steps, or a predicate's joins. The
        ///first after <c>__</c> may be a start step as well. So what each is called on is a traversal or a predicate.
        ///</summary>
        private string Chain(HashSet<string> names, bool anonymous)
        {
            var first = anonymous;

            while (IsMark(Next, "."))
            {
                _at++;

                var step = Next;

                if (step.Kind != Kind.Name)
                    return Expected("a step's name after the dot");

                if (!names.Contains(step.Text) && !(first && Starting.Contains(step.Text)))
                    return NotAStep(step);

                _at++;

                var refusal = Arguments(step);

                if (refusal != null)
                    return refusal;

                first = false;
            }

            return null;
        }

        ///<summary>A list, <c>[1, 2]</c>, or a map, <c>['a': 1]</c> or <c>[(T.label): 'x']</c>; <c>[]</c> and <c>[:]</c> empty.</summary>
        private string Collection()
        {
            _at++;

            if (IsMark(Next, "]"))
            {
                _at++;

                return null;
            }

            if (IsMark(Next, ":") && IsMark(After, "]"))
            {
                _at += 2;

                return null;
            }

            var map = IsMark(Next, "(") || (Next.Kind != Kind.Mark && Next.Kind != Kind.End && IsMark(After, ":"));

            while (true)
            {
                string refusal;

                if (map)
                    refusal = Entry();
                else
                    refusal = Argument();

                if (refusal != null)
                    return refusal;

                if (IsMark(Next, ","))
                {
                    _at++;

                    if (IsMark(Next, "]"))
                    {
                        _at++;

                        return null;
                    }
                }
                else if (IsMark(Next, "]"))
                {
                    _at++;

                    return null;
                }
                else
                    return Unexpected(Next);
            }
        }

        ///<summary>
        ///One of a map's entries. A bare name as the key is the text of the name, as Groovy reads it, and not a variable;
        ///a key in brackets is an argument, as <c>(T.label)</c>.
        ///</summary>
        private string Entry()
        {
            if (IsMark(Next, "("))
            {
                _at++;

                var refusal = Argument();

                if (refusal != null)
                    return refusal;

                if (!IsMark(Next, ")"))
                    return Expected("a ) after the key");

                _at++;
            }
            else if (Next.Kind == Kind.Text || Next.Kind == Kind.Number || Next.Kind == Kind.Name)
                _at++;
            else
                return Unexpected(Next);

            if (!IsMark(Next, ":"))
                return Expected("a : after the key");

            _at++;

            return Argument();
        }

        private static bool IsPredicate(string name)
        {
            foreach (var named in Predicates.Values)
                if (named.Contains(name))
                    return true;

            return false;
        }

        ///<summary>
        ///Why a name called as a step is refused. An enum's value called as one, as in <c>values().desc()</c>, is told
        ///where it goes instead (2026-11-06): a model wrote that three times running, refused each time with nothing to
        ///go on, and gave up on a question <c>order().by(values, desc)</c> answers.
        ///</summary>
        private static string NotAStep(Token token)
        {
            var owner = EnumOf(token.Text);

            if (owner == "Order")
                return $"{token.Text} is not a step but one of Order's values, so it goes in as an argument: order().by('name', {token.Text}), or order(local).by(values, {token.Text}) to sort a map by its values.";
            else if (owner != null)
                return $"{token.Text} is not a step but one of {owner}'s values, so it goes in as an argument, as in by({token.Text}) or {owner}.{token.Text}.";

            return $"{token.Text} is not one of Gremlin's reading steps, and only those are run here: whatever else it is, it is Groovy's.";
        }

        ///<summary>The enum a value Gremlin's Groovy imports bare belongs to, or <c>null</c> for any other name.</summary>
        private static string? EnumOf(string name)
        {
            if (!Bare.Contains(name))
                return null;

            foreach (var (owner, values) in Enums)
                if (values.Contains(name))
                    return owner;

            return null;
        }

        private string Expected(string what)
        {
            return $"The query does not read as one Gremlin traversal: expected {what} at character {Next.At + 1}.";
        }

        private string Unexpected(Token token)
        {
            if (token.Kind == Kind.End)
                return "The query does not read as one Gremlin traversal: it ends before a bracket is closed.";

            return $"The query does not read as one Gremlin traversal: {Quoted(token)} at character {token.At + 1} is not where it can be.";
        }

        private string Quoted(Token token)
        {
            if (token.Kind == Kind.Text)
                return "the string";

            return $"'{token.Text}'";
        }
    }
}
