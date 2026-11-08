using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphDBViewerWeb.Code;

///<summary>A JSON-LD document that could not be read, with the reason in the words of the JSON-LD API's own error names.</summary>
public sealed class JsonLdException : Exception
{
    public JsonLdException(string message) : base(message)
    {
    }
}

///<summary>
///Reads a JSON-LD document into RDF triples: the JSON-LD 1.1 API's expansion, then its deserialization to RDF.
///
///A SPARQL endpoint with no RDF/JSON answers a CONSTRUCT or DESCRIBE in JSON-LD, and each engine writes it its
///own way. Measured 2026-09-15: Fuseki compacts it against the query's prefixes; Virtuoso defines a term for each
///property and makes some of them turn a plain string into a link; Blazegraph expands every value into an array;
///Oxigraph expands too, and spells lists and types out as ordinary triples. Only the algorithm makes those four
///mean the same triples. Reading the keys as they stand would draw Virtuoso's links as text.
///
///<strong>Nothing is fetched.</strong> A context named by URL, or imported, is an error rather than a guess:
///without it the document's terms mean nothing. The rest of expansion is here, except what only framing uses,
///and <c>@protected</c>, which refuses a later redefinition and changes nothing about a document that makes
///none. Where the algorithm resolves against the document's own URL there is none, so a relative reference
///stays as written unless the context gives an <c>@base</c>.
///</summary>
public static class JsonLdReader
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "@base", "@container", "@context", "@default", "@direction", "@embed", "@explicit", "@graph", "@id", "@import",
        "@included", "@index", "@json", "@language", "@list", "@nest", "@none", "@omitDefault", "@prefix", "@preserve",
        "@propagate", "@protected", "@requireAll", "@reverse", "@set", "@type", "@value", "@version", "@vocab"
    };

    ///<summary>The entries of a context that are about the context itself rather than a term.</summary>
    private static readonly HashSet<string> ContextEntries = new(StringComparer.Ordinal)
    {
        "@base", "@direction", "@import", "@language", "@propagate", "@protected", "@version", "@vocab"
    };

    private static readonly HashSet<string> TermEntries = new(StringComparer.Ordinal)
    {
        "@id", "@reverse", "@container", "@context", "@direction", "@index", "@language", "@nest", "@prefix", "@protected", "@type"
    };

    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "@graph", "@id", "@index", "@language", "@list", "@set", "@type"
    };

    private static readonly Regex KeywordForm = new("^@[A-Za-z]+$");
    private static readonly Regex Scheme = new("^[A-Za-z][A-Za-z0-9+.-]*:");
    private static readonly Regex IntegerDigits = new("^-?[0-9]+$");

    //RFC 3986, appendix B: 2 the scheme, 4 the authority, 5 the path, 7 the query, 9 the fragment.
    private static readonly Regex ReferenceParts = new(@"^(([^:/?#]+):)?(//([^/?#]*))?([^?#]*)(\?([^#]*))?(#(.*))?$", RegexOptions.Singleline);

    ///<summary>Every triple the document states, in every graph it names. Blank nodes keep the labels the document gives them.</summary>
    public static List<RdfTriple> Read(JsonElement document)
    {
        var expanded = Expand(new Context(), null, document, false);

        if (expanded is Dictionary<string, object?> top && top.Count == 1 && top.TryGetValue("@graph", out var graph))
            expanded = graph;

        return new TripleWriter(AsList(expanded)).Write();
    }

    //── The active context ──────────────────────────────────────────────

    private sealed class Context
    {
        public Dictionary<string, Term> Terms { get; init; } = new(StringComparer.Ordinal);
        public string? Base { get; set; }
        public string? Vocab { get; set; }
        public string? Language { get; set; }
        public string? Direction { get; set; }

        ///<summary>The context a type-scoped context was applied over, which a node object inside it goes back to.</summary>
        public Context? Previous { get; set; }

        public Context Copy()
        {
            return new Context
            {
                Terms = new Dictionary<string, Term>(Terms, StringComparer.Ordinal),
                Base = Base,
                Vocab = Vocab,
                Language = Language,
                Direction = Direction,
                Previous = Previous
            };
        }

        public Term? Find(string? name)
        {
            if (name != null && Terms.TryGetValue(name, out var term))
                return term;

            return null;
        }
    }

    private sealed class Term
    {
        public string? Iri { get; set; }
        public bool Reverse { get; set; }
        public string? Type { get; set; }
        public bool HasLanguage { get; set; }
        public string? Language { get; set; }
        public bool HasDirection { get; set; }
        public string? Direction { get; set; }
        public HashSet<string> Container { get; } = new(StringComparer.Ordinal);
        public string? Index { get; set; }
        public bool HasScopedContext { get; set; }
        public JsonElement ScopedContext { get; set; }
        public bool Prefix { get; set; }
    }

    ///<summary>The API's context processing: a local context applied over the active one, which is left as it was.</summary>
    private static Context Process(Context active, JsonElement local, bool propagate = true)
    {
        var result = active.Copy();

        if (local.ValueKind == JsonValueKind.Object && local.TryGetProperty("@propagate", out var propagates))
        {
            if (propagates.ValueKind != JsonValueKind.True && propagates.ValueKind != JsonValueKind.False)
                throw new JsonLdException("invalid @propagate value");

            propagate = propagates.ValueKind == JsonValueKind.True;
        }

        if (!propagate && result.Previous == null)
            result.Previous = active;

        foreach (var context in Items(local))
        {
            if (context.ValueKind == JsonValueKind.Null)
            {
                var reset = new Context();

                if (!propagate)
                    reset.Previous = result;

                result = reset;
                continue;
            }
            else if (context.ValueKind == JsonValueKind.String)
                throw new JsonLdException($"its context is loaded from {context.GetString()}, and contexts are not fetched");

            if (context.ValueKind != JsonValueKind.Object)
                throw new JsonLdException("invalid local context");

            if (context.TryGetProperty("@version", out var version) && (version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1.1))
                throw new JsonLdException("invalid @version value");

            if (context.TryGetProperty("@import", out var import))
                throw new JsonLdException($"its context imports {import}, and contexts are not fetched");

            if (context.TryGetProperty("@base", out var baseIri))
                result.Base = BaseIri(result, baseIri);

            if (context.TryGetProperty("@vocab", out var vocab))
                //A relative @vocab resolves against the document's URL, which there is none of, so it stays as written.
                if (vocab.ValueKind == JsonValueKind.Null)
                    result.Vocab = null;
                else if (vocab.ValueKind == JsonValueKind.String)
                    result.Vocab = ExpandIri(result, vocab.GetString(), documentRelative: true, vocab: true);
                else
                    throw new JsonLdException("invalid vocab mapping");

            if (context.TryGetProperty("@language", out var language))
            {
                if (language.ValueKind != JsonValueKind.Null && language.ValueKind != JsonValueKind.String)
                    throw new JsonLdException("invalid default language");

                result.Language = language.GetString();
            }

            if (context.TryGetProperty("@direction", out var direction))
                result.Direction = DirectionOf(direction);

            var defined = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var entry in context.EnumerateObject())
                if (!ContextEntries.Contains(entry.Name))
                    Define(result, context, entry.Name, defined);
        }

        return result;
    }

    private static string? BaseIri(Context result, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.String)
            throw new JsonLdException("invalid base IRI");

        var text = value.GetString()!;

        if (IsAbsolute(text))
            return text;

        if (result.Base != null)
            return Resolve(result.Base, text);

        throw new JsonLdException("invalid base IRI");
    }

    private static string? DirectionOf(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.String && value.GetString() is "ltr" or "rtl")
            return value.GetString();

        throw new JsonLdException("invalid base direction");
    }

    ///<summary>The API's Create Term Definition: one entry of a local context, defining first whatever it depends on.</summary>
    private static void Define(Context active, JsonElement local, string term, Dictionary<string, bool> defined)
    {
        if (defined.TryGetValue(term, out var done))
        {
            if (done)
                return;

            throw new JsonLdException($"cyclic IRI mapping: {term}");
        }

        if (term.Length == 0)
            throw new JsonLdException("invalid term definition: an empty term");

        defined[term] = false;

        var value = local.GetProperty(term);

        //@type may only be said to be a set, which is compaction's business.
        if (term == "@type")
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("@container", out var set) || set.ValueKind != JsonValueKind.String || set.GetString() != "@set")
                throw new JsonLdException("keyword redefinition: @type");

            defined[term] = true;
            return;
        }

        if (IsKeyword(term))
            throw new JsonLdException($"keyword redefinition: {term}");

        //A term that looks like a keyword but is not one is ignored.
        if (KeywordForm.IsMatch(term))
        {
            defined[term] = true;
            return;
        }

        active.Terms.Remove(term);

        var definition = new Term();
        var simple = value.ValueKind == JsonValueKind.String;
        var entries = value;

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in value.EnumerateObject())
                if (!TermEntries.Contains(entry.Name))
                    throw new JsonLdException($"invalid term definition: {term}");
        }
        else if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.String)
            throw new JsonLdException($"invalid term definition: {term}");

        //null and a plain string are both shorthand for an @id.
        var hasId = value.ValueKind != JsonValueKind.Object;
        var id = value;

        if (value.ValueKind == JsonValueKind.Object)
            hasId = value.TryGetProperty("@id", out id);

        var expandedTerm = value.ValueKind == JsonValueKind.Object;

        if (expandedTerm && entries.TryGetProperty("@type", out var type))
        {
            if (type.ValueKind != JsonValueKind.String)
                throw new JsonLdException($"invalid type mapping: {term}");

            definition.Type = ExpandIri(active, type.GetString(), vocab: true, local: local, defined: defined);

            if (definition.Type is not ("@id" or "@json" or "@none" or "@vocab") && !IsAbsolute(definition.Type))
                throw new JsonLdException($"invalid type mapping: {term}");
        }

        if (expandedTerm && entries.TryGetProperty("@reverse", out var reverse))
        {
            DefineReverse(active, local, term, entries, reverse, hasId, definition, defined);
            return;
        }

        var colon = -1;

        if (term.Length > 1)
            colon = term.IndexOf(':', 1);

        if (hasId && !(id.ValueKind == JsonValueKind.String && id.GetString() == term))
        {
            if (id.ValueKind != JsonValueKind.Null)
            {
                if (id.ValueKind != JsonValueKind.String)
                    throw new JsonLdException($"invalid IRI mapping: {term}");

                var mapped = id.GetString()!;

                if (!IsKeyword(mapped) && KeywordForm.IsMatch(mapped))
                {
                    defined[term] = true;
                    return;
                }

                definition.Iri = ExpandIri(active, mapped, vocab: true, local: local, defined: defined);

                if (!IsKeyword(definition.Iri) && !IsAbsolute(definition.Iri) && !IsBlank(definition.Iri))
                    throw new JsonLdException($"invalid IRI mapping: {term}");

                if (definition.Iri == "@context")
                    throw new JsonLdException($"invalid keyword alias: {term}");

                //A term that is itself an IRI or a compact IRI may not say it means a different one.
                if ((term.Length > 2 && term.IndexOf(':', 1, term.Length - 2) >= 0) || term.Contains('/'))
                {
                    defined[term] = true;

                    if (ExpandIri(active, term, vocab: true, local: local, defined: defined) != definition.Iri)
                        throw new JsonLdException($"invalid IRI mapping: {term}");
                }

                //Only a plain string ending where an IRI's segments end can be the prefix of a compact IRI, as in
                //jsonld.js: "ex": "http://example.org/" can, "ex": "http://example.org/ns" cannot.
                if (simple && !term.Contains(':') && !term.Contains('/') && (IsBlank(definition.Iri) || EndsWithDelimiter(definition.Iri)))
                    definition.Prefix = true;
            }
        }
        else if (colon > 0)
        {
            var prefix = term[..colon];

            if (local.TryGetProperty(prefix, out _))
                Define(active, local, prefix, defined);

            var prefixTerm = active.Find(prefix);

            if (prefixTerm?.Iri != null)
                definition.Iri = prefixTerm.Iri + term[(colon + 1)..];
            else
                definition.Iri = term;
        }
        else if (term.Contains('/'))
        {
            definition.Iri = ExpandIri(active, term, vocab: true);

            if (!IsAbsolute(definition.Iri))
                throw new JsonLdException($"invalid IRI mapping: {term}");
        }
        else if (active.Vocab != null)
            definition.Iri = active.Vocab + term;
        else
            throw new JsonLdException($"invalid IRI mapping: {term} is not an IRI, and there is no @vocab to make it one");

        if (expandedTerm)
            DefineEntries(active, term, entries, definition);

        active.Terms[term] = definition;
        defined[term] = true;
    }

    private static void DefineReverse(
        Context active,
        JsonElement local,
        string term,
        JsonElement entries,
        JsonElement reverse,
        bool hasId,
        Term definition,
        Dictionary<string, bool> defined)
    {
        if (hasId || entries.TryGetProperty("@nest", out _))
            throw new JsonLdException($"invalid reverse property: {term}");

        if (reverse.ValueKind != JsonValueKind.String)
            throw new JsonLdException($"invalid IRI mapping: {term}");

        if (KeywordForm.IsMatch(reverse.GetString()!))
        {
            defined[term] = true;
            return;
        }

        definition.Iri = ExpandIri(active, reverse.GetString(), vocab: true, local: local, defined: defined);

        if (!IsAbsolute(definition.Iri) && !IsBlank(definition.Iri))
            throw new JsonLdException($"invalid IRI mapping: {term}");

        if (entries.TryGetProperty("@container", out var container))
            if (container.ValueKind == JsonValueKind.String && container.GetString() is "@set" or "@index")
                definition.Container.Add(container.GetString()!);
            else if (container.ValueKind != JsonValueKind.Null)
                throw new JsonLdException($"invalid reverse property: {term}");

        definition.Reverse = true;
        active.Terms[term] = definition;
        defined[term] = true;
    }

    ///<summary>What an expanded term definition says besides the IRI it maps to.</summary>
    private static void DefineEntries(Context active, string term, JsonElement entries, Term definition)
    {
        if (entries.TryGetProperty("@container", out var container))
        {
            foreach (var item in Items(container))
            {
                if (item.ValueKind == JsonValueKind.Null)
                    continue;

                if (item.ValueKind != JsonValueKind.String || !Containers.Contains(item.GetString()!))
                    throw new JsonLdException($"invalid container mapping: {term}");

                definition.Container.Add(item.GetString()!);
            }

            if (definition.Container.Contains("@type"))
            {
                if (definition.Type == null)
                    definition.Type = "@id";

                if (definition.Type is not ("@id" or "@vocab"))
                    throw new JsonLdException($"invalid type mapping: {term}");
            }
        }

        if (entries.TryGetProperty("@index", out var index))
        {
            if (!definition.Container.Contains("@index") || index.ValueKind != JsonValueKind.String)
                throw new JsonLdException($"invalid term definition: {term}");

            definition.Index = index.GetString();
        }

        //Applied where the term is used, not here: a scoped context may say what a term it uses means.
        if (entries.TryGetProperty("@context", out var scoped))
        {
            definition.HasScopedContext = true;
            definition.ScopedContext = scoped;
        }

        if (!entries.TryGetProperty("@type", out _))
        {
            if (entries.TryGetProperty("@language", out var language))
            {
                if (language.ValueKind != JsonValueKind.Null && language.ValueKind != JsonValueKind.String)
                    throw new JsonLdException($"invalid language mapping: {term}");

                definition.HasLanguage = true;
                definition.Language = language.GetString();
            }

            if (entries.TryGetProperty("@direction", out var direction))
            {
                definition.HasDirection = true;
                definition.Direction = DirectionOf(direction);
            }
        }

        if (entries.TryGetProperty("@prefix", out var prefix))
        {
            if (term.Contains(':') || term.Contains('/') || (prefix.ValueKind != JsonValueKind.True && prefix.ValueKind != JsonValueKind.False))
                throw new JsonLdException($"invalid term definition: {term}");

            definition.Prefix = prefix.ValueKind == JsonValueKind.True;

            if (definition.Prefix && IsKeyword(definition.Iri))
                throw new JsonLdException($"invalid term definition: {term}");
        }
    }

    ///<summary>The API's IRI Expansion: a term, compact IRI, blank node label or relative reference, as the IRI it stands for.</summary>
    private static string? ExpandIri(
        Context active,
        string? value,
        bool documentRelative = false,
        bool vocab = false,
        JsonElement? local = null,
        Dictionary<string, bool>? defined = null)
    {
        if (value == null || IsKeyword(value))
            return value;

        if (KeywordForm.IsMatch(value))
            return null;

        if (local is JsonElement context && defined != null && context.TryGetProperty(value, out _) && !(defined.TryGetValue(value, out var done) && done))
            Define(active, context, value, defined);

        var term = active.Find(value);

        if (term != null && IsKeyword(term.Iri))
            return term.Iri;

        if (vocab && term != null)
            return term.Iri;

        var colon = -1;

        if (value.Length > 1)
            colon = value.IndexOf(':', 1);

        if (colon > 0)
        {
            var prefix = value[..colon];
            var suffix = value[(colon + 1)..];

            if (prefix == "_" || suffix.StartsWith("//", StringComparison.Ordinal))
                return value;

            if (local is JsonElement prefixes && defined != null && prefixes.TryGetProperty(prefix, out _) && !(defined.TryGetValue(prefix, out var prefixDone) && prefixDone))
                Define(active, prefixes, prefix, defined);

            var prefixTerm = active.Find(prefix);

            if (prefixTerm?.Iri != null && prefixTerm.Prefix)
                return prefixTerm.Iri + suffix;

            if (IsAbsolute(value))
                return value;
        }

        if (vocab && active.Vocab != null)
            return active.Vocab + value;

        if (documentRelative)
            return Resolve(active.Base, value);

        return value;
    }

    //── Expansion ───────────────────────────────────────────────────────
    //An expanded document is plain .NET values: a map is Dictionary<string, object?>, an array List<object?>, and a
    //scalar a string, a bool, a JsonNumber or, for an @json value, a JsonLiteral.

    private sealed record JsonNumber(string Text);

    private sealed record JsonLiteral(string Text);

    ///<summary>The API's Expansion algorithm, for one element under the property it is the value of.</summary>
    private static object? Expand(Context active, string? property, JsonElement element, bool fromMap)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var definition = active.Find(property);

        if (element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            //A value with no property to be the value of is dropped.
            if (property == null || property == "@graph")
                return null;

            if (definition is { HasScopedContext: true })
                active = Process(active, definition.ScopedContext);

            return ExpandValue(active, property, Scalar(element));
        }
        else if (element.ValueKind == JsonValueKind.Array)
            return ExpandArray(active, property, definition, element, fromMap);

        //A type-scoped context covers its own node's properties, not a node inside one of them.
        if (active.Previous != null && !fromMap && Reverts(active, element))
            active = active.Previous;

        if (definition is { HasScopedContext: true })
            active = Process(active, definition.ScopedContext);

        if (element.TryGetProperty("@context", out var context))
            active = Process(active, context);

        var typeScoped = active;
        string? typeKey = null;

        foreach (var key in element.EnumerateObject().Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal))
        {
            if (ExpandIri(active, key, vocab: true) != "@type")
                continue;

            typeKey ??= key;

            foreach (var type in Items(element.GetProperty(key)).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).OrderBy(name => name, StringComparer.Ordinal))
            {
                var typeTerm = typeScoped.Find(type);

                if (typeTerm is { HasScopedContext: true })
                    active = Process(active, typeTerm.ScopedContext, propagate: false);
            }
        }

        //The type an @value is read as, which matters only when it is @json.
        string? inputType = null;

        if (typeKey != null)
        {
            var last = Items(element.GetProperty(typeKey)).LastOrDefault();

            if (last.ValueKind == JsonValueKind.String)
                inputType = ExpandIri(active, last.GetString(), vocab: true);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        ExpandEntries(active, typeScoped, property, element, result, inputType);

        return Finish(result, property);
    }

    private static List<object?> ExpandArray(Context active, string? property, Term? definition, JsonElement element, bool fromMap)
    {
        var items = new List<object?>();

        foreach (var item in element.EnumerateArray())
        {
            var expanded = Expand(active, property, item, fromMap);

            //An array inside a list is a list of its own.
            if (definition != null && definition.Container.Contains("@list") && expanded is List<object?> nested)
                expanded = new Dictionary<string, object?> { ["@list"] = nested };

            if (expanded is List<object?> many)
                items.AddRange(many);
            else if (expanded != null)
                items.Add(expanded);
        }

        return items;
    }

    //A value object, or a node reference that is nothing but an @id, stays in the type-scoped context.
    private static bool Reverts(Context active, JsonElement element)
    {
        var count = 0;
        var onlyId = true;

        foreach (var entry in element.EnumerateObject())
        {
            var expanded = ExpandIri(active, entry.Name, vocab: true);

            if (expanded == "@value")
                return false;

            if (expanded != "@id")
                onlyId = false;

            count++;
        }

        return !(count == 1 && onlyId);
    }

    ///<summary>Steps 13 and 14: every entry of a map, and then the maps its @nest entries hold.</summary>
    private static void ExpandEntries(
        Context active,
        Context typeScoped,
        string? property,
        JsonElement element,
        Dictionary<string, object?> result,
        string? inputType)
    {
        var nests = new List<string>();

        //In key order, as jsonld.js reads them: every keyword comes before every term, so an @reverse entry is read
        //before a reverse term adds to it, rather than the two colliding when the term is written first.
        foreach (var entry in element.EnumerateObject().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var key = entry.Name;

            if (key == "@context")
                continue;

            var expandedProperty = ExpandIri(active, key, vocab: true);

            //A key that means nothing, which is any term the context does not define, is dropped.
            if (expandedProperty == null || (!expandedProperty.Contains(':') && !IsKeyword(expandedProperty)))
                continue;

            if (IsKeyword(expandedProperty))
            {
                ExpandKeyword(active, typeScoped, property, key, expandedProperty, entry.Value, result, nests, inputType);
                continue;
            }

            var definition = active.Find(key);
            var value = entry.Value;
            object? expanded;

            if (definition?.Type == "@json")
                expanded = new Dictionary<string, object?> { ["@value"] = new JsonLiteral(JsonText(value)), ["@type"] = "@json" };
            else if (definition != null && definition.Container.Contains("@language") && value.ValueKind == JsonValueKind.Object)
                expanded = ExpandLanguageMap(active, definition, value);
            else if (definition != null && value.ValueKind == JsonValueKind.Object && (definition.Container.Contains("@index") || definition.Container.Contains("@id") || definition.Container.Contains("@type")))
                expanded = ExpandIndexMap(active, key, definition, value);
            else
                expanded = Expand(active, key, value, false);

            if (expanded == null)
                continue;

            if (definition != null && definition.Container.Contains("@list") && !IsList(expanded))
                expanded = new Dictionary<string, object?> { ["@list"] = AsList(expanded) };

            if (definition != null && definition.Container.Contains("@graph") && !definition.Container.Contains("@id") && !definition.Container.Contains("@index"))
                expanded = AsList(expanded).Select(item => (object?)new Dictionary<string, object?> { ["@graph"] = AsList(item) }).ToList();

            if (definition is { Reverse: true })
            {
                var reverseMap = ReverseMap(result);

                foreach (var item in AsList(expanded))
                {
                    if (IsValue(item) || IsList(item))
                        throw new JsonLdException($"invalid reverse property value: {key}");

                    AddValue(reverseMap, expandedProperty, item);
                }

                continue;
            }

            AddValue(result, expandedProperty, expanded);
        }

        foreach (var nest in nests)
            foreach (var nested in Items(element.GetProperty(nest)))
            {
                if (nested.ValueKind != JsonValueKind.Object || nested.EnumerateObject().Any(entry => ExpandIri(active, entry.Name, vocab: true) == "@value"))
                    throw new JsonLdException("invalid @nest value");

                ExpandEntries(active, typeScoped, property, nested, result, inputType);
            }
    }

    private static void ExpandKeyword(
        Context active,
        Context typeScoped,
        string? property,
        string key,
        string keyword,
        JsonElement value,
        Dictionary<string, object?> result,
        List<string> nests,
        string? inputType)
    {
        if (property == "@reverse")
            throw new JsonLdException("invalid reverse property map");

        if (result.ContainsKey(keyword) && keyword is not ("@included" or "@type"))
            throw new JsonLdException($"colliding keywords: {keyword}");

        object? expanded;

        switch (keyword)
        {
            case "@id":
                if (value.ValueKind != JsonValueKind.String)
                    throw new JsonLdException("invalid @id value");

                expanded = ExpandIri(active, value.GetString(), documentRelative: true);
                break;

            case "@type":
                expanded = ExpandTypes(typeScoped, value, result);
                break;

            case "@graph":
                expanded = AsList(Expand(active, "@graph", value, false)).Where(item => item is Dictionary<string, object?>).ToList();
                break;

            case "@included":
                var included = AsList(Expand(active, null, value, false));

                if (included.Any(item => item is not Dictionary<string, object?> || IsValue(item) || IsList(item)))
                    throw new JsonLdException("invalid @included value");

                if (result.TryGetValue("@included", out var earlier))
                    included.InsertRange(0, AsList(earlier));

                expanded = included;
                break;

            case "@value":
                if (inputType == "@json")
                    expanded = new JsonLiteral(JsonText(value));
                else if (value.ValueKind == JsonValueKind.Null)
                {
                    //Kept, since an @type beside a null @value still says this is a value object and not a node.
                    result["@value"] = null;
                    return;
                }
                else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw new JsonLdException("invalid value object value");
                else
                    expanded = Scalar(value);

                break;

            case "@language":
                if (value.ValueKind != JsonValueKind.String)
                    throw new JsonLdException("invalid language-tagged string");

                expanded = value.GetString();
                break;

            case "@direction":
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("ltr" or "rtl"))
                    throw new JsonLdException("invalid base direction");

                expanded = value.GetString();
                break;

            case "@index":
                if (value.ValueKind != JsonValueKind.String)
                    throw new JsonLdException("invalid @index value");

                expanded = value.GetString();
                break;

            case "@list":
                //A list with no property to be the value of is dropped.
                if (property == null || property == "@graph")
                    return;

                expanded = AsList(Expand(active, property, value, false));
                break;

            case "@set":
                expanded = Expand(active, property, value, false);
                break;

            case "@reverse":
                ExpandReverse(active, value, result);
                return;

            case "@nest":
                nests.Add(key);
                return;

            default:
                //@default, @embed and the rest are framing's.
                return;
        }

        result[keyword] = expanded;
    }

    private static object? ExpandTypes(Context typeScoped, JsonElement value, Dictionary<string, object?> result)
    {
        object? expanded;

        if (value.ValueKind == JsonValueKind.String)
            expanded = ExpandIri(typeScoped, value.GetString(), documentRelative: true, vocab: true);
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            expanded = value.EnumerateArray().Select(item => (object?)ExpandIri(typeScoped, item.GetString(), documentRelative: true, vocab: true)).Where(item => item != null).ToList();
        else
            throw new JsonLdException("invalid type value");

        if (!result.TryGetValue("@type", out var earlier))
            return expanded;

        var types = AsList(earlier).ToList();

        types.AddRange(AsList(expanded));

        return types;
    }

    private static void ExpandReverse(Context active, JsonElement value, Dictionary<string, object?> result)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonLdException("invalid @reverse value");

        if (Expand(active, "@reverse", value, false) is not Dictionary<string, object?> reversed)
            return;

        //Reversed twice is forwards.
        if (reversed.TryGetValue("@reverse", out var twice) && twice is Dictionary<string, object?> forwards)
            foreach (var (name, items) in forwards)
                AddValue(result, name, items);

        foreach (var (name, items) in reversed)
        {
            if (name == "@reverse")
                continue;

            var reverseMap = ReverseMap(result);

            foreach (var item in AsList(items))
            {
                if (IsValue(item) || IsList(item))
                    throw new JsonLdException($"invalid reverse property value: {name}");

                AddValue(reverseMap, name, item);
            }
        }
    }

    ///<summary>Steps 15 to 20: what the map turned out to be, and whether it stays.</summary>
    private static object? Finish(Dictionary<string, object?> result, string? property)
    {
        object? output = result;

        if (result.ContainsKey("@value"))
        {
            if (result.Keys.Any(name => name is not ("@direction" or "@index" or "@language" or "@type" or "@value")))
                throw new JsonLdException("invalid value object");

            if (result.ContainsKey("@type") && (result.ContainsKey("@language") || result.ContainsKey("@direction")))
                throw new JsonLdException("invalid value object");

            var type = result.GetValueOrDefault("@type");
            var json = type is "@json";
            var value = result["@value"];

            if (!json && (value == null || value is List<object?> { Count: 0 }))
                return null;

            if (!json && value is not string && result.ContainsKey("@language"))
                throw new JsonLdException("invalid language-tagged value");

            if (!json && result.ContainsKey("@type") && !(type is string typeIri && IsAbsolute(typeIri)))
                throw new JsonLdException("invalid typed value");
        }
        else if (result.TryGetValue("@type", out var types) && types is not List<object?>)
            result["@type"] = new List<object?> { types };
        else if (result.ContainsKey("@set") || result.ContainsKey("@list"))
        {
            if (result.Count > 2 || (result.Count == 2 && !result.ContainsKey("@index")))
                throw new JsonLdException("invalid set or list object");

            if (result.TryGetValue("@set", out var set))
                output = set;
        }

        if (output is not Dictionary<string, object?> map)
            return output;

        if (map.Count == 1 && map.ContainsKey("@language"))
            return null;

        //At the top, or directly in a graph, only a node with something to say about itself stays.
        if (property == null || property == "@graph")
        {
            if (map.Count == 0 || map.ContainsKey("@value") || map.ContainsKey("@list"))
                return null;

            if (map.Count == 1 && map.ContainsKey("@id"))
                return null;
        }

        return map;
    }

    ///<summary>The API's Value Expansion: a scalar as a value object, or as a node reference where the term says it is one.</summary>
    private static Dictionary<string, object?> ExpandValue(Context active, string? property, object? value)
    {
        var definition = active.Find(property);

        if (definition?.Type == "@id" && value is string reference)
            return new Dictionary<string, object?> { ["@id"] = ExpandIri(active, reference, documentRelative: true) };

        if (definition?.Type == "@vocab" && value is string term)
            return new Dictionary<string, object?> { ["@id"] = ExpandIri(active, term, documentRelative: true, vocab: true) };

        var result = new Dictionary<string, object?> { ["@value"] = value };

        if (definition?.Type is not (null or "@id" or "@vocab" or "@none"))
            result["@type"] = definition.Type;
        else if (value is string)
        {
            var language = active.Language;
            var direction = active.Direction;

            if (definition is { HasLanguage: true })
                language = definition.Language;

            if (definition is { HasDirection: true })
                direction = definition.Direction;

            if (language != null)
                result["@language"] = language;

            if (direction != null)
                result["@direction"] = direction;
        }

        return result;
    }

    private static List<object?> ExpandLanguageMap(Context active, Term definition, JsonElement value)
    {
        var expanded = new List<object?>();
        var direction = active.Direction;

        if (definition.HasDirection)
            direction = definition.Direction;

        foreach (var entry in value.EnumerateObject())
            foreach (var item in Items(entry.Value))
            {
                if (item.ValueKind == JsonValueKind.Null)
                    continue;

                if (item.ValueKind != JsonValueKind.String)
                    throw new JsonLdException("invalid language map value");

                var text = new Dictionary<string, object?> { ["@value"] = item.GetString() };

                if (entry.Name != "@none" && ExpandIri(active, entry.Name, vocab: true) != "@none")
                    text["@language"] = entry.Name;

                if (direction != null)
                    text["@direction"] = direction;

                expanded.Add(text);
            }

        return expanded;
    }

    ///<summary>An index, id or type map: each key is an index, an @id or an @type of the values it holds.</summary>
    private static List<object?> ExpandIndexMap(Context active, string key, Term definition, JsonElement value)
    {
        var expanded = new List<object?>();
        var container = definition.Container;
        var indexKey = definition.Index ?? "@index";
        var outer = active;

        //As jsonld.js does: a type map's keys are types, read outside whatever a type scoped over this node.
        if (container.Contains("@type"))
            outer = active.Previous ?? active;

        foreach (var entry in value.EnumerateObject())
        {
            var index = entry.Name;
            var mapContext = outer;

            if (container.Contains("@type") && mapContext.Find(index) is { HasScopedContext: true } typeTerm)
                mapContext = Process(mapContext, typeTerm.ScopedContext, propagate: false);

            var expandedIndex = ExpandIri(active, index, vocab: true);
            var items = new List<object?>();

            foreach (var element in Items(entry.Value))
                items.AddRange(AsList(Expand(mapContext, key, element, true)));

            foreach (var raw in items)
            {
                if (raw is not Dictionary<string, object?> item)
                    continue;

                if (container.Contains("@graph") && !IsGraph(item))
                    item = new Dictionary<string, object?> { ["@graph"] = new List<object?> { item } };

                if (container.Contains("@index") && indexKey != "@index" && expandedIndex != "@none")
                {
                    //The index is a property's value, read as that property reads a value.
                    var indexProperty = ExpandIri(active, indexKey, vocab: true)!;
                    var values = new List<object?> { ExpandValue(active, indexKey, index) };

                    if (item.TryGetValue(indexProperty, out var existing))
                        values.AddRange(AsList(existing));

                    item[indexProperty] = values;

                    if (IsValue(item))
                        throw new JsonLdException("invalid value object");
                }
                else if (container.Contains("@index") && !item.ContainsKey("@index") && expandedIndex != "@none")
                    item["@index"] = index;
                else if (container.Contains("@id") && !item.ContainsKey("@id") && expandedIndex != "@none")
                    item["@id"] = ExpandIri(active, index, documentRelative: true);
                else if (container.Contains("@type") && expandedIndex != "@none")
                {
                    var types = new List<object?> { expandedIndex };

                    if (item.TryGetValue("@type", out var existingTypes))
                        types.AddRange(AsList(existingTypes));

                    item["@type"] = types;
                }

                expanded.Add(item);
            }
        }

        return expanded;
    }

    //── Deserialization to RDF ──────────────────────────────────────────

    ///<summary>
    ///The API's Deserialize JSON-LD to RDF, walking the expanded document rather than building its node map first:
    ///the triples are the same, and a node described in two places states some of them twice, which the graph
    ///drawn from them does not mind. Every named graph's triples are read with the default graph's, because a
    ///drawing has one canvas.
    ///</summary>
    private sealed class TripleWriter
    {
        private readonly List<object?> _nodes;
        private readonly HashSet<string> _labels = new(StringComparer.Ordinal);
        private readonly List<RdfTriple> _triples = new();
        private int _next;

        public TripleWriter(List<object?> nodes)
        {
            _nodes = nodes;
            CollectLabels(nodes);
        }

        public List<RdfTriple> Write()
        {
            foreach (var item in _nodes)
                if (item is Dictionary<string, object?> node && !IsValue(node) && !IsList(node))
                    Subject(node);

            return _triples;
        }

        //The labels the document gives its blank nodes, so that one made up for a node it left unnamed is never one of them.
        private void CollectLabels(object? item)
        {
            if (item is List<object?> list)
            {
                foreach (var entry in list)
                    CollectLabels(entry);

                return;
            }

            if (item is not Dictionary<string, object?> map)
                return;

            foreach (var (key, value) in map)
            {
                if (key is "@id" or "@type")
                    foreach (var name in AsList(value).OfType<string>())
                        if (IsBlank(name))
                            _labels.Add(name);

                CollectLabels(value);
            }
        }

        private string NewBlank()
        {
            string label;

            do
            {
                label = "_:b" + _next.ToString(CultureInfo.InvariantCulture);
                _next++;
            }
            while (_labels.Contains(label));

            return label;
        }

        private string Subject(Dictionary<string, object?> node)
        {
            var id = node.GetValueOrDefault("@id") as string ?? NewBlank();

            foreach (var type in AsList(node.GetValueOrDefault("@type")).OfType<string>())
                Add(id, Rdf + "type", Resource(type));

            foreach (var (property, values) in node)
            {
                //A blank node as a predicate is not RDF, and a keyword is not a predicate at all.
                if (IsKeyword(property) || IsBlank(property))
                    continue;

                foreach (var item in AsList(values))
                {
                    var term = Object(item);

                    if (term != null)
                        Add(id, property, term);
                }
            }

            if (node.GetValueOrDefault("@reverse") is Dictionary<string, object?> reverse)
                foreach (var (property, values) in reverse)
                {
                    if (IsBlank(property))
                        continue;

                    foreach (var item in AsList(values))
                        if (item is Dictionary<string, object?> other && !IsValue(other) && !IsList(other))
                            Add(Subject(other), property, Resource(id));
                }

            foreach (var member in AsList(node.GetValueOrDefault("@graph")).Concat(AsList(node.GetValueOrDefault("@included"))))
                if (member is Dictionary<string, object?> inner && !IsValue(inner) && !IsList(inner))
                    Subject(inner);

            return id;
        }

        private RdfTerm? Object(object? item)
        {
            if (item is not Dictionary<string, object?> map)
                return null;

            if (map.ContainsKey("@value"))
                return Literal(map);

            if (map.TryGetValue("@list", out var list))
                return List(AsList(list));

            return Resource(Subject(map));
        }

        //rdf:first and rdf:rest through a blank node for each item, ending in rdf:nil, as RDF/JSON states a list.
        private RdfTerm List(List<object?> items)
        {
            if (items.Count == 0)
                return Resource(Rdf + "nil");

            var labels = items.Select(_ => NewBlank()).ToList();

            for (var i = 0; i < items.Count; i++)
            {
                var first = Object(items[i]);

                if (first != null)
                    Add(labels[i], Rdf + "first", first);

                var rest = Rdf + "nil";

                if (i + 1 < labels.Count)
                    rest = labels[i + 1];

                Add(labels[i], Rdf + "rest", Resource(rest));
            }

            return Resource(labels[0]);
        }

        private static RdfTerm? Literal(Dictionary<string, object?> map)
        {
            var value = map["@value"];
            var datatype = map.GetValueOrDefault("@type") as string;
            var language = map.GetValueOrDefault("@language") as string;

            if (value is JsonLiteral json)
                return new RdfTerm(RdfTermKind.Literal, json.Text, Rdf + "JSON");
            else if (value is bool flag)
            {
                var text = "false";

                if (flag)
                    text = "true";

                return new RdfTerm(RdfTermKind.Literal, text, datatype ?? Xsd + "boolean");
            }
            else if (value is JsonNumber number)
                return Number(number, datatype);

            if (value is not string lexical)
                return null;

            if (language != null)
                return new RdfTerm(RdfTermKind.Literal, lexical, Rdf + "langString", language);

            return new RdfTerm(RdfTermKind.Literal, lexical, datatype ?? Xsd + "string");
        }

        //A whole number is its digits, as the API writes it. A fraction keeps the digits the document wrote, where
        //the API would write 1.68 as 1.68E0: the graph only ever shows the digits, and 1.68 is what was meant.
        private static RdfTerm Number(JsonNumber number, string? datatype)
        {
            var value = double.Parse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture);

            if (Math.Abs(value) >= 1e21 || Math.Floor(value) != value || datatype == Xsd + "double")
                return new RdfTerm(RdfTermKind.Literal, number.Text, datatype ?? Xsd + "double");

            var digits = number.Text;

            if (!IntegerDigits.IsMatch(digits))
                digits = new BigInteger(value).ToString(CultureInfo.InvariantCulture);

            return new RdfTerm(RdfTermKind.Literal, digits, datatype ?? Xsd + "integer");
        }

        private void Add(string subject, string predicate, RdfTerm obj)
        {
            _triples.Add(new RdfTriple(subject, predicate, obj));
        }

        private static RdfTerm Resource(string id)
        {
            if (IsBlank(id))
                return new RdfTerm(RdfTermKind.Blank, id);

            return new RdfTerm(RdfTermKind.Iri, id);
        }
    }

    //── Helpers ─────────────────────────────────────────────────────────

    private static bool IsKeyword(string? value)
    {
        return value != null && Keywords.Contains(value);
    }

    private static bool IsAbsolute(string? value)
    {
        return value != null && Scheme.IsMatch(value);
    }

    private static bool IsBlank(string? value)
    {
        return value != null && value.StartsWith("_:", StringComparison.Ordinal);
    }

    private static bool EndsWithDelimiter(string? iri)
    {
        return !string.IsNullOrEmpty(iri) && ":/?#[]@".Contains(iri[^1]);
    }

    private static bool IsValue(object? item)
    {
        return item is Dictionary<string, object?> map && map.ContainsKey("@value");
    }

    private static bool IsList(object? item)
    {
        return item is Dictionary<string, object?> map && map.ContainsKey("@list");
    }

    private static bool IsGraph(Dictionary<string, object?> map)
    {
        return map.ContainsKey("@graph") && map.Keys.All(name => name is "@graph" or "@id" or "@index");
    }

    ///<summary>An element's items: an array's, or the element alone.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray();

        return new[] { element };
    }

    ///<summary>An expanded value as a list: the list itself, nothing for null, or the value alone.</summary>
    private static List<object?> AsList(object? value)
    {
        if (value is List<object?> list)
            return list;
        else if (value == null)
            return new List<object?>();

        return new List<object?> { value };
    }

    private static void AddValue(Dictionary<string, object?> map, string key, object? value)
    {
        if (!map.TryGetValue(key, out var existing) || existing is not List<object?> list)
        {
            list = AsList(existing);
            map[key] = list;
        }

        if (value is List<object?> values)
            list.AddRange(values);
        else
            list.Add(value);
    }

    private static Dictionary<string, object?> ReverseMap(Dictionary<string, object?> result)
    {
        if (result.GetValueOrDefault("@reverse") is Dictionary<string, object?> map)
            return map;

        map = new Dictionary<string, object?>(StringComparer.Ordinal);
        result["@reverse"] = map;

        return map;
    }

    private static object? Scalar(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Number:
                return new JsonNumber(element.GetRawText());

            default:
                return null;
        }
    }

    private static string JsonText(JsonElement element)
    {
        return JsonSerializer.Serialize(element);
    }

    //── RFC 3986 reference resolution, for @base ─────────────────────────

    private static string Resolve(string? baseIri, string reference)
    {
        if (baseIri == null)
            return reference;

        var target = ReferenceParts.Match(reference);

        if (target.Groups[1].Success)
            return reference;

        var source = ReferenceParts.Match(baseIri);
        var path = target.Groups[5].Value;
        string? authority = null;
        string? query = null;

        if (target.Groups[3].Success)
        {
            authority = target.Groups[4].Value;
            path = RemoveDotSegments(path);
        }
        else
        {
            if (source.Groups[3].Success)
                authority = source.Groups[4].Value;

            if (path.Length == 0)
                path = source.Groups[5].Value;
            else if (path.StartsWith('/'))
                path = RemoveDotSegments(path);
            else
                path = RemoveDotSegments(Merge(source, path));
        }

        if (target.Groups[6].Success)
            query = target.Groups[7].Value;
        else if (target.Groups[5].Value.Length == 0 && !target.Groups[3].Success && source.Groups[6].Success)
            query = source.Groups[7].Value;

        var text = new StringBuilder(source.Groups[2].Value).Append(':');

        if (authority != null)
            text.Append("//").Append(authority);

        text.Append(path);

        if (query != null)
            text.Append('?').Append(query);

        if (target.Groups[8].Success)
            text.Append('#').Append(target.Groups[9].Value);

        return text.ToString();
    }

    private static string Merge(Match source, string path)
    {
        var sourcePath = source.Groups[5].Value;

        if (source.Groups[3].Success && sourcePath.Length == 0)
            return "/" + path;

        var slash = sourcePath.LastIndexOf('/');

        if (slash < 0)
            return path;

        return sourcePath[..(slash + 1)] + path;
    }

    private static string RemoveDotSegments(string path)
    {
        var input = path;
        var output = new StringBuilder();

        while (input.Length > 0)
            if (input.StartsWith("../", StringComparison.Ordinal))
                input = input[3..];
            else if (input.StartsWith("./", StringComparison.Ordinal))
                input = input[2..];
            else if (input.StartsWith("/./", StringComparison.Ordinal))
                input = input[2..];
            else if (input == "/.")
                input = "/";
            else if (input.StartsWith("/../", StringComparison.Ordinal))
            {
                input = input[3..];
                RemoveLastSegment(output);
            }
            else if (input == "/..")
            {
                input = "/";
                RemoveLastSegment(output);
            }
            else if (input is "." or "..")
                input = "";
            else
            {
                var start = 0;

                if (input.StartsWith('/'))
                    start = 1;

                var next = input.IndexOf('/', start);

                if (next < 0)
                    next = input.Length;

                output.Append(input, 0, next);
                input = input[next..];
            }

        return output.ToString();
    }

    private static void RemoveLastSegment(StringBuilder output)
    {
        var text = output.ToString();
        var slash = text.LastIndexOf('/');

        output.Clear();

        if (slash > 0)
            output.Append(text, 0, slash);
    }
}
