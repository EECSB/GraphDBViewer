using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>
///The stand-in ids a staged element carries before it exists, and how they become real ones at commit.
///
///An <c>addV</c> sitting in the Generated buffer has no id: the database assigns one when it runs. So the
///edit parsers mint <c>__opt_v_&lt;index&gt;</c> for it — keyed to the statement that creates it — and the
///canvas draws the node under that. The moment somebody links or deletes that node on the canvas, the
///statement written for them names it by that stand-in, and a stand-in is not something a database can
///look up. TinkerGraph answers <c>Expected an id that is convertible to class java.lang.Long</c>, and the
///commit half-lands: the adds go in, everything referring to them fails.
///
///So they are resolved as the batch runs. Statements execute in order; when one creates an element, the
///id it came back with is recorded against the stand-in that names that statement, and every later
///statement is substituted before it is sent.
///
///Indices have to agree with the parser that minted them, which is why <see cref="Split"/> lives here
///rather than being re-derived at the call site: the Gremlin parser counts non-empty statements and
///splits on <c>;</c> too, while the others count raw lines. Either is fine; disagreeing is not.
///</summary>
public static class StagedIds
{
    public const string VertexPrefix = "__opt_v_";
    public const string EdgePrefix = "__opt_e_";

    ///<summary>The stand-in id for a vertex created by the statement at <paramref name="index"/>.</summary>
    public static string ForVertex(int index)
    {
        return VertexPrefix + index;
    }

    ///<summary>The stand-in id for an edge created by the statement at <paramref name="index"/>.</summary>
    public static string ForEdge(int index)
    {
        return EdgePrefix + index;
    }

    ///<summary>
    ///<paramref name="after"/>, a merge of <paramref name="before"/>, with every stand-in it names following the
    ///statement that mints it (2026-10-05). A merge takes property lines out and puts their new ones at the end, and a
    ///stand-in is its statement's position, so left as it was a link staged against a new vertex named another
    ///statement, or none, and was lost at commit. A merge moves adds but never makes or drops one, so the k-th add
    ///before is the k-th after; when the counts differ nothing is guessed and the buffer is answered as it is.
    ///</summary>
    public static string Renumbered(string before, string after, string language, Func<string, List<GraphEdit>> parse)
    {
        var was = Adds(Split(before, language), parse);
        var now = Adds(Split(after, language), parse);

        if (was.Count != now.Count)
            return after;

        var moved = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var k = 0; k < was.Count; k++)
            if (was[k] != now[k])
                moved[was[k]] = now[k];

        if (moved.Count == 0)
            return after;

        return Placeholder.Replace(after, match => Moved(moved, match.Value));
    }

    ///<summary>
    ///What a commit that did not finish leaves staged (2026-10-05): every statement that did not run, the stand-ins of adds
    ///that ran swapped for the ids they came back with, and the stand-ins of adds still staged renumbered to the places
    ///those adds now have. Until then the whole buffer stayed, and the next Commit ran again what had run, a vertex or an
    ///edge made twice. <paramref name="statements"/> are the buffer as <see cref="Split"/> walked it, and
    ///<paramref name="ran"/> the places of those that ran.
    ///</summary>
    public static string Remaining(List<string> statements, ISet<int> ran, IReadOnlyDictionary<string, ResolvedId> realIds)
    {
        var kept = new List<string>();
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < statements.Count; i++)
        {
            if (ran.Contains(i) || string.IsNullOrWhiteSpace(statements[i]))
                continue;

            moved[ForVertex(i)] = ForVertex(kept.Count);
            moved[ForEdge(i)] = ForEdge(kept.Count);
            kept.Add(Resolve(statements[i], realIds));
        }

        return string.Join("\n", kept.Select(statement => Placeholder.Replace(statement, match => Moved(moved, match.Value))));
    }

    ///<summary>A stand-in, its digits whole, so <c>__opt_v_1</c> is never read inside <c>__opt_v_10</c>.</summary>
    private static readonly Regex Placeholder = new(@"__opt_[ve]_\d+", RegexOptions.Compiled);

    private static string Moved(Dictionary<string, string> moved, string placeholder)
    {
        if (moved.TryGetValue(placeholder, out var to))
            return to;

        return placeholder;
    }

    ///<summary>The stand-in each statement that adds an element mints, in the order the statements come.</summary>
    private static List<string> Adds(List<string> statements, Func<string, List<GraphEdit>> parse)
    {
        var adds = new List<string>();

        for (var i = 0; i < statements.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(statements[i]))
                continue;

            List<GraphEdit> edits;

            try
            {
                edits = parse(statements[i]);
            }
            catch
            {
                continue;
            }

            if (edits.Any(edit => edit.Kind == GraphEditKind.AddNode))
                adds.Add(ForVertex(i));

            if (edits.Any(edit => edit.Kind == GraphEditKind.AddEdge))
                adds.Add(ForEdge(i));
        }

        return adds;
    }

    ///<summary>Whether an id is a stand-in rather than something the database knows about.</summary>
    public static bool IsPlaceholder(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        return id.StartsWith(VertexPrefix, StringComparison.Ordinal) || id.StartsWith(EdgePrefix, StringComparison.Ordinal);
    }

    ///<summary>Whether a statement still mentions one, which means it cannot be sent as it stands.</summary>
    public static bool MentionsPlaceholder(string statement)
    {
        if (string.IsNullOrEmpty(statement))
            return false;

        return statement.Contains(VertexPrefix, StringComparison.Ordinal) || statement.Contains(EdgePrefix, StringComparison.Ordinal);
    }

    ///<summary>
    ///The buffer's statements, indexed the way the parser for <paramref name="language"/> indexed them
    ///when it minted the stand-ins. Blank entries are kept for the languages that count raw lines — the
    ///caller skips them without renumbering, because renumbering is exactly what breaks the mapping.
    ///</summary>
    public static List<string> Split(string buffer, string language)
    {
        if (language == "gremlin" || language == null)
            return GremlinEditParser.SplitStatements(buffer ?? "");

        return new List<string>((buffer ?? "").Split('\n'));
    }

    ///<summary>
    ///Substitutes the stand-ins a statement mentions for the real ids already recorded.
    ///
    ///Quoted forms go first and are replaced whole, quotes included, because the real id may not want
    ///quotes at all: Gremlin is type-strict, so a Long id has to be written <c>123L</c> and a quoted
    ///<c>'123'</c> matches nothing.
    ///</summary>
    public static string Resolve(string statement, IReadOnlyDictionary<string, ResolvedId> realIds)
    {
        if (string.IsNullOrEmpty(statement) || realIds == null || realIds.Count == 0)
            return statement;

        foreach (var pair in realIds)
        {
            if (!statement.Contains(pair.Key, StringComparison.Ordinal))
                continue;

            statement = statement
                .Replace("'" + pair.Key + "'", pair.Value.Literal, StringComparison.Ordinal)
                .Replace("\"" + pair.Key + "\"", pair.Value.Literal, StringComparison.Ordinal);

            statement = ReplaceBare(statement, pair.Key, pair.Value.Id);
        }

        return statement;
    }

    ///<summary>
    ///The unquoted form, replaced only where the stand-in ends there. <c>__opt_v_1</c> is the first nine
    ///characters of <c>__opt_v_10</c>, and a plain Replace rewrote the tenth vertex's stand-in into the
    ///first vertex's id with a 0 on the end — an id that names nothing, so an edge staged to it was
    ///created for no vertex and nothing said so. Any batch creating eleven or more elements could lose
    ///edges that way. Found by the 2026-09-17 audit.
    ///</summary>
    private static string ReplaceBare(string statement, string key, string id)
    {
        var sb = new StringBuilder(statement.Length);
        var at = 0;

        for (var found = statement.IndexOf(key, StringComparison.Ordinal); found >= 0; found = statement.IndexOf(key, at, StringComparison.Ordinal))
        {
            var end = found + key.Length;

            sb.Append(statement, at, found - at);

            //A digit right after it means this is the beginning of a longer stand-in, which has its own turn.
            if (end < statement.Length && char.IsDigit(statement[end]))
                sb.Append(key);
            else
                sb.Append(id);

            at = end;
        }

        sb.Append(statement, at, statement.Length - at);

        return sb.ToString();
    }

    ///<summary>Records what the statement at <paramref name="index"/> created, under both stand-in shapes:
    ///only one of them can be referred to, and which one depends on what the statement was.</summary>
    public static void Record(Dictionary<string, ResolvedId> realIds, int index, string id, string idType, string language)
    {
        if (realIds == null || string.IsNullOrEmpty(id))
            return;

        var resolved = new ResolvedId(id, Literal(id, idType, language));

        realIds[ForVertex(index)] = resolved;
        realIds[ForEdge(index)] = resolved;
    }

    ///<summary>How the id has to be written to be found again in this language.</summary>
    private static string Literal(string id, string idType, string language)
    {
        if (language == "gremlin" || language == null)
            return GremlinQueryBuilder.FormatId(id, idType);

        //Bare only in a number's own form: digits with a leading zero are a string id here too.
        if (GremlinQueryBuilder.IsPlainNumber(id))
            return id;

        //Escaped the way every builder's Escape does, backslash first, so an id holding either survives.
        return "'" + id.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    ///<summary>
    ///The id of the element a statement just created, out of whatever the engine answered with.
    ///
    ///Deliberately shape-agnostic: it looks for the first thing carrying an id, unwrapping GraphSON on the
    ///way down. A statement that created nothing simply has none, which is the answer for a drop or a
    ///property edit and is not an error.
    ///</summary>
    public static bool TryReadCreatedId(GraphDbResult result, out string id, out string idType)
    {
        id = null;
        idType = null;

        if (result.IsError)
            return false;

        return TryReadId(result.Data, 0, out id, out idType);
    }

    private static bool TryReadId(JsonElement element, int depth, out string id, out string idType)
    {
        id = null;
        idType = null;

        //Deep enough to reach an id through a list and a typed wrapper or two, shallow enough that a
        //large or self-similar payload cannot turn this into a walk of the whole result.
        if (depth > 6)
            return false;

        JsonElement unwrapped;

        try
        {
            unwrapped = GraphDataConverter.UnwrapElement(element);
        }
        catch
        {
            return false;
        }

        if (unwrapped.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in unwrapped.EnumerateArray())
                if (TryReadId(item, depth + 1, out id, out idType))
                    return true;

            return false;
        }

        if (unwrapped.ValueKind != JsonValueKind.Object)
            return false;

        if (!unwrapped.TryGetProperty("id", out var idProp))
            return false;

        if (idProp.ValueKind != JsonValueKind.Object)
        {
            id = idProp.ToString();

            return !string.IsNullOrEmpty(id);
        }

        if (idProp.TryGetProperty("@type", out var typeEl))
            idType = typeEl.GetString();

        if (!idProp.TryGetProperty("@value", out _))
            return false;

        //A JanusGraph edge's RelationIdentifier as its relationId, as every other reader of an id has it.
        id = GraphDataConverter.IdText(idProp);

        return !string.IsNullOrEmpty(id);
    }
}

///<summary>A real id, in both the forms a caller needs: bare, and written the way the language wants it.</summary>
public readonly struct ResolvedId
{
    public ResolvedId(string id, string literal)
    {
        Id = id;
        Literal = literal;
    }

    ///<summary>The id itself, with no quoting or type suffix.</summary>
    public string Id { get; }

    ///<summary>The same id as it must appear inside a statement, e.g. <c>123L</c> or <c>'abc'</c>.</summary>
    public string Literal { get; }
}
