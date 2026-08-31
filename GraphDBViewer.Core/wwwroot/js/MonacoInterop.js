//Self-hosted Monaco editor interop (no npm/bundler). The AMD loader is injected lazily on first use —
//after the UMD libraries (cytoscape, 3d-force-graph) have already attached to window — so Monaco's
//global `define`/`require` can't hijack them.
//
//This file and the Monaco build it loads live in the GraphDBViewer.Core class library, so both are
//served from _content/GraphDBViewer.Core/ rather than the app's own wwwroot. Everything that has to
//agree on where Monaco is comes from MonacoRoot below.
window.monacoInterop = (function () {
    let loadPromise = null;
    let editor = null;
    let viewerEditor = null;//separate read-only editor for the JSON results view (coexists with the query editor)
    let dotNetRef = null;
    let suppressChange = false;

    //Schema vocabulary from the connected DB, pushed via setSchema, used for schema-aware autocomplete.
    let schemaData = { vertexLabels: [], edgeLabels: [], propertyKeys: [] };

    //Debugger: character ranges of the query's steps and their editor decoration ids.
    let debugRanges = [];
    let debugDecorations = [];

    //Where the vendored Monaco build is served from, relative to the app root. It is a class-library
    //asset, so the framework publishes it under _content/<assembly>/.
    //
    //Three separate things have to agree on this — the <script> that pulls in the AMD loader, the
    //require.config that resolves 'vs/...' module ids, and the worker bootstrap below — and the third is
    //the one that fails quietly: a wrong path there leaves the editor drawing perfectly while the
    //services it defers to the worker never start. So it is stated once and the rest is derived.
    const MonacoRoot = '_content/GraphDBViewer.Core/lib/monaco/';

    //Absolute base URL of the app, honoring <base href> in dev and under a deployed subpath.
    function baseUrl() {
        let href = document.baseURI;
        if (!href.endsWith('/'))
            href += '/';

        return href;
    }

    function monacoRootUrl() {
        return baseUrl() + MonacoRoot;
    }

    function monacoVsUrl() {
        return monacoRootUrl() + 'vs';
    }

    //Injects a <script> and resolves once it has loaded.
    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            const s = document.createElement('script');
            s.src = src;
            s.onload = resolve;
            s.onerror = reject;
            document.head.appendChild(s);
        });
    }

    //Loads the AMD loader + Monaco editor core once, then registers the Gremlin language.
    function ensureMonaco() {
        if (loadPromise)
            return loadPromise;

        const vs = monacoVsUrl();

        //The editor web worker is loaded same-origin via a tiny bootstrap blob that, in turn,
        //importScripts the real workerMain with the right base URL.
        window.MonacoEnvironment = {
            getWorkerUrl: function () {
                const js = "self.MonacoEnvironment={baseUrl:'" + monacoRootUrl() + "'};" +
                    "importScripts('" + vs + "/base/worker/workerMain.js');";
                return URL.createObjectURL(new Blob([js], { type: 'text/javascript' }));
            }
        };

        loadPromise = loadScript(vs + '/loader.js').then(function () {
            return new Promise(function (resolve) {
                window.require.config({ paths: { vs: vs } });
                window.require(['vs/editor/editor.main'], function () {
                    registerGremlin();
                    registerAql();
                    registerDql();
                    registerJsonView();
                    registerGunJs();
                    resolve();
                });
            });
        });

        return loadPromise;
    }

    //Registers a lightweight Gremlin/Groovy-flavored language: Monarch highlighting plus a
    //completion list of common traversal steps.
    function registerGremlin() {
        const exists = monaco.languages.getLanguages().some(function (l) {
            return l.id === 'gremlin';
        });

        if (exists)
            return;

        monaco.languages.register({ id: 'gremlin' });

        monaco.languages.setMonarchTokensProvider('gremlin', {
            defaultToken: '',
            keywords: [
                'g', 'V', 'E', 'has', 'hasLabel', 'hasId', 'hasKey', 'hasValue', 'out', 'in',
                'both', 'outE', 'inE', 'bothE', 'outV', 'inV', 'bothV', 'otherV', 'addV', 'addE',
                'drop', 'property', 'properties', 'values', 'valueMap', 'elementMap', 'label', 'id',
                'key', 'value', 'count', 'group', 'groupCount', 'by', 'project', 'select', 'where',
                'and', 'or', 'not', 'is', 'within', 'without', 'inside', 'outside', 'between',
                'eq', 'neq', 'lt', 'lte', 'gt', 'gte', 'order', 'limit', 'range', 'tail', 'skip',
                'dedup', 'fold', 'unfold', 'sum', 'max', 'min', 'mean', 'as', 'from', 'to',
                'repeat', 'until', 'emit', 'times', 'path', 'simplePath', 'cyclicPath', 'coalesce',
                'choose', 'optional', 'union', 'local', 'sideEffect', 'aggregate', 'store', 'cap',
                'math', 'constant', 'inject', 'barrier', 'T', 'P', 'Order', 'Scope', 'Column',
                'Pop', 'Cardinality', 'Direction', 'addAll', 'next', 'toList', 'iterate'
            ],
            tokenizer: {
                root: [
                    [/\/\/.*$/, 'comment'],
                    [/\/\*/, 'comment', '@comment'],
                    [/"([^"\\]|\\.)*"/, 'string'],
                    [/'([^'\\]|\\.)*'/, 'string'],
                    [/\d+\.\d+([eE][\-+]?\d+)?/, 'number.float'],
                    [/\d+[lL]?/, 'number'],
                    [/[a-zA-Z_$][\w$]*/, { cases: { '@keywords': 'keyword', '@default': 'identifier' } }],
                    [/[{}()\[\]]/, '@brackets'],
                    [/[<>=!]+/, 'operator'],
                    [/[.,;]/, 'delimiter']
                ],
                comment: [
                    [/[^/*]+/, 'comment'],
                    [/\*\//, 'comment', '@pop'],
                    [/[/*]/, 'comment']
                ]
            }
        });

        //Brackets, auto-closing pairs and indentation rules so the editor auto-indents and
        //auto-closes ()/[]/{}/quotes (cypher and sparql ship their own configuration).
        monaco.languages.setLanguageConfiguration('gremlin', {
            comments: { lineComment: '//', blockComment: ['/*', '*/'] },
            brackets: [['(', ')'], ['[', ']'], ['{', '}']],
            autoClosingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: "'", close: "'" },
                { open: '"', close: '"' }
            ],
            surroundingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: "'", close: "'" },
                { open: '"', close: '"' }
            ],
            indentationRules: {
                increaseIndentPattern: /[\(\[\{][^\)\]\}]*$/,
                decreaseIndentPattern: /^\s*[\)\]\}]/
            }
        });

        //Traversal source + common steps. The "g" root is included so completions appear from the
        //very first character; the rest are the steps you'd chain after a "." .
        const roots = ['g'];
        const steps = [
            'V()', 'E()', 'has()', 'hasLabel()', 'hasId()', 'hasKey()', 'hasValue()', 'out()', 'in()',
            'both()', 'outE()', 'inE()', 'bothE()', 'outV()', 'inV()', 'bothV()', 'otherV()', 'addV()',
            'addE()', 'property()', 'properties()', 'values()', 'valueMap()', 'elementMap()', 'label()',
            'id()', 'key()', 'value()', 'count()', 'group()', 'groupCount()', 'by()', 'project()',
            'select()', 'where()', 'and()', 'or()', 'not()', 'is()', 'order()', 'limit()', 'range()',
            'tail()', 'skip()', 'dedup()', 'fold()', 'unfold()', 'sum()', 'max()', 'min()', 'mean()',
            'as()', 'from()', 'to()', 'path()', 'simplePath()', 'repeat()', 'until()', 'emit()',
            'times()', 'coalesce()', 'choose()', 'optional()', 'union()', 'local()', 'sideEffect()',
            'aggregate()', 'store()', 'cap()', 'inject()', 'drop()', 'next()', 'toList()', 'iterate()'
        ];

        monaco.languages.registerCompletionItemProvider('gremlin', {
            //"." opens the step list; a quote or "(" opens the schema labels / property keys.
            triggerCharacters: ['.', '(', "'", '"'],
            provideCompletionItems: function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };

                const linePrefix = model.getValueInRange({
                    startLineNumber: position.lineNumber,
                    startColumn: 1,
                    endLineNumber: position.lineNumber,
                    endColumn: position.column
                });

                //Inside a quoted string, suggest the schema's labels / property keys; otherwise, steps.
                if (insideString(linePrefix))
                    return { suggestions: schemaSuggestions(linePrefix, range) };

                const suggestions = [];

                roots.forEach(function (r) {
                    suggestions.push({
                        label: r,
                        kind: monaco.languages.CompletionItemKind.Variable,
                        insertText: r,
                        range: range
                    });
                });

                steps.forEach(function (s) {
                    suggestions.push({
                        label: s,
                        kind: monaco.languages.CompletionItemKind.Function,
                        insertText: s,
                        //Filter by the step's name without the "()" so typing "ha" matches "has()".
                        filterText: s.replace('()', ''),
                        range: range
                    });
                });

                return { suggestions: suggestions };
            }
        });
    }

    //Registers ArangoDB's AQL: Monarch highlighting plus a completion list of its keywords and the
    //functions a graph user reaches for. The vendored Monaco ships cypher and sparql under
    //basic-languages but no AQL, so — like gremlin — it is defined here rather than lazy-loaded.
    function registerAql() {
        const exists = monaco.languages.getLanguages().some(function (l) {
            return l.id === 'aql';
        });

        if (exists)
            return;

        monaco.languages.register({ id: 'aql' });

        //AQL keywords are case-insensitive; the tokenizer below matches them that way.
        const aqlKeywords = [
            'FOR', 'RETURN', 'FILTER', 'SEARCH', 'SORT', 'LIMIT', 'LET', 'COLLECT', 'INTO', 'AGGREGATE',
            'INSERT', 'UPDATE', 'REPLACE', 'REMOVE', 'UPSERT', 'WITH', 'IN', 'INBOUND', 'OUTBOUND', 'ANY',
            'ALL', 'NONE', 'AND', 'OR', 'NOT', 'DISTINCT', 'GRAPH', 'SHORTEST_PATH', 'K_SHORTEST_PATHS',
            'PRUNE', 'OPTIONS', 'WINDOW', 'ASC', 'DESC', 'LIKE', 'INTO', 'KEEP', 'COUNT', 'TRUE', 'FALSE',
            'NULL'
        ];

        const aqlFunctions = [
            'LENGTH', 'FIRST', 'LAST', 'UNION', 'UNION_DISTINCT', 'APPEND', 'FLATTEN', 'UNIQUE', 'SLICE',
            'DOCUMENT', 'EDGES', 'NEIGHBORS', 'PATHS', 'ATTRIBUTES', 'VALUES', 'KEEP', 'UNSET', 'MERGE',
            'CONCAT', 'CONTAINS', 'LOWER', 'UPPER', 'SUBSTRING', 'SPLIT', 'TRIM', 'REGEX_TEST', 'TO_STRING',
            'TO_NUMBER', 'TO_BOOL', 'TO_ARRAY', 'IS_NULL', 'IS_DOCUMENT', 'MIN', 'MAX', 'SUM', 'AVERAGE',
            'SORTED', 'REVERSE', 'RAND', 'DATE_NOW', 'PARSE_IDENTIFIER'
        ];

        monaco.languages.setMonarchTokensProvider('aql', {
            defaultToken: '',
            ignoreCase: true,
            keywords: aqlKeywords,
            builtins: aqlFunctions,
            tokenizer: {
                root: [
                    [/\/\/.*$/, 'comment'],
                    [/\/\*/, 'comment', '@comment'],
                    [/"([^"\\]|\\.)*"/, 'string'],
                    [/'([^'\\]|\\.)*'/, 'string'],
                    //Backticks quote a collection or attribute name, as they do in Cypher.
                    [/`[^`]*`/, 'identifier'],
                    //Bind parameters: @value and @@collection. The "@" goes in a character class on
                    //purpose — Monarch reads a bare "@" as an attribute reference and "@@" as an escaped
                    //one, so the obvious /@@?…/ silently becomes "optional @" and swallows every
                    //identifier in the query.
                    [/[@][@]?[A-Za-z_]\w*/, 'variable.parameter'],
                    [/\d+\.\d+([eE][\-+]?\d+)?/, 'number.float'],
                    [/\d+/, 'number'],
                    [/[a-zA-Z_]\w*/, { cases: { '@keywords': 'keyword', '@builtins': 'predefined', '@default': 'identifier' } }],
                    [/[{}()\[\]]/, '@brackets'],
                    [/[<>=!+\-*/%]+/, 'operator'],
                    [/[.,;:]/, 'delimiter']
                ],
                comment: [
                    [/[^/*]+/, 'comment'],
                    [/\*\//, 'comment', '@pop'],
                    [/[/*]/, 'comment']
                ]
            }
        });

        monaco.languages.setLanguageConfiguration('aql', {
            comments: { lineComment: '//', blockComment: ['/*', '*/'] },
            brackets: [['(', ')'], ['[', ']'], ['{', '}']],
            autoClosingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: "'", close: "'" },
                { open: '"', close: '"' },
                { open: '`', close: '`' }
            ],
            surroundingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: "'", close: "'" },
                { open: '"', close: '"' },
                { open: '`', close: '`' }
            ],
            indentationRules: {
                increaseIndentPattern: /[\(\[\{][^\)\]\}]*$/,
                decreaseIndentPattern: /^\s*[\)\]\}]/
            }
        });

        monaco.languages.registerCompletionItemProvider('aql', {
            provideCompletionItems: function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };

                const linePrefix = model.getValueInRange({
                    startLineNumber: position.lineNumber,
                    startColumn: 1,
                    endLineNumber: position.lineNumber,
                    endColumn: position.column
                });

                //Inside a string literal, offer nothing — the same rule the Gremlin provider follows.
                if (insideString(linePrefix))
                    return { suggestions: [] };

                const suggestions = [];

                //Schema-aware suggestions (collections, attribute keys) when the caret is somewhere one
                //belongs; setSchema publishes them from the connected database.
                schemaSuggestions(linePrefix, range).forEach(function (s) {
                    suggestions.push(s);
                });

                aqlKeywords.forEach(function (k) {
                    suggestions.push({
                        label: k,
                        kind: monaco.languages.CompletionItemKind.Keyword,
                        insertText: k,
                        range: range
                    });
                });

                aqlFunctions.forEach(function (f) {
                    suggestions.push({
                        label: f,
                        kind: monaco.languages.CompletionItemKind.Function,
                        insertText: f + '()',
                        filterText: f,
                        range: range
                    });
                });

                return { suggestions: suggestions };
            }
        });
    }

    //Registers Dgraph's DQL. Its shape is block-and-brace rather than clause-based: a query names blocks,
    //each with a root function, and selects predicates inside them. Highlighting leans on that — the root
    //functions and directives are the keywords, and a bare word is a predicate.
    function registerDql() {
        const exists = monaco.languages.getLanguages().some(function (l) {
            return l.id === 'dql';
        });

        if (exists)
            return;

        monaco.languages.register({ id: 'dql' });

        //Root and filter functions, plus the block keywords a mutation or schema query uses.
        const dqlKeywords = [
            'func', 'filter', 'facets', 'cascade', 'normalize', 'ignorereflex', 'recurse', 'query',
            'schema', 'mutation', 'upsert', 'set', 'delete', 'as', 'var', 'groupby', 'orderasc',
            'orderdesc', 'first', 'offset', 'after', 'depth', 'loop'
        ];

        const dqlFunctions = [
            'eq', 'lt', 'le', 'gt', 'ge', 'has', 'uid', 'uid_in', 'type', 'allofterms', 'anyofterms',
            'alloftext', 'anyoftext', 'regexp', 'match', 'between', 'near', 'within', 'contains',
            'intersects', 'count', 'sum', 'avg', 'min', 'max', 'val', 'expand', 'reverse', 'math',
            'since', 'not', 'and', 'or'
        ];

        monaco.languages.setMonarchTokensProvider('dql', {
            defaultToken: '',
            keywords: dqlKeywords,
            builtins: dqlFunctions,
            tokenizer: {
                root: [
                    [/#.*$/, 'comment'],
                    [/"([^"\\]|\\.)*"/, 'string'],
                    //A GraphQL-style variable, as a named query declares them.
                    [/[$][A-Za-z_]\w*/, 'variable.parameter'],
                    //A blank-node label in a mutation: _:alice.
                    [/_:[A-Za-z_]\w*/, 'variable'],
                    //A predicate in angle brackets, which is how RDF names one.
                    [/<[^>]*>/, 'string.escape'],
                    //Dgraph's own predicates are dotted — dgraph.type.
                    [/[A-Za-z_]\w*(\.\w+)+/, 'type'],
                    [/0x[0-9a-fA-F]+/, 'number.hex'],
                    [/\d+\.\d+([eE][\-+]?\d+)?/, 'number.float'],
                    [/\d+/, 'number'],
                    [/[a-zA-Z_]\w*/, { cases: { '@keywords': 'keyword', '@builtins': 'predefined', '@default': 'identifier' } }],
                    [/[{}()\[\]]/, '@brackets'],
                    [/[<>=!~@]+/, 'operator'],
                    [/[.,;:]/, 'delimiter']
                ]
            }
        });

        monaco.languages.setLanguageConfiguration('dql', {
            //DQL comments are #, as in GraphQL.
            comments: { lineComment: '#' },
            brackets: [['(', ')'], ['[', ']'], ['{', '}']],
            autoClosingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: '"', close: '"' }
            ],
            surroundingPairs: [
                { open: '(', close: ')' },
                { open: '[', close: ']' },
                { open: '{', close: '}' },
                { open: '"', close: '"' }
            ],
            indentationRules: {
                increaseIndentPattern: /[\(\[\{][^\)\]\}]*$/,
                decreaseIndentPattern: /^\s*[\)\]\}]/
            }
        });

        monaco.languages.registerCompletionItemProvider('dql', {
            provideCompletionItems: function (model, position) {
                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn
                };

                const linePrefix = model.getValueInRange({
                    startLineNumber: position.lineNumber,
                    startColumn: 1,
                    endLineNumber: position.lineNumber,
                    endColumn: position.column
                });

                if (insideString(linePrefix))
                    return { suggestions: [] };

                const suggestions = [];

                //Schema-aware suggestions (types, predicates) once the connection publishes them.
                schemaSuggestions(linePrefix, range).forEach(function (s) {
                    suggestions.push(s);
                });

                dqlKeywords.forEach(function (k) {
                    suggestions.push({
                        label: k,
                        kind: monaco.languages.CompletionItemKind.Keyword,
                        insertText: k,
                        range: range
                    });
                });

                dqlFunctions.forEach(function (f) {
                    suggestions.push({
                        label: f,
                        kind: monaco.languages.CompletionItemKind.Function,
                        insertText: f + '()',
                        filterText: f,
                        range: range
                    });
                });

                return { suggestions: suggestions };
            }
        });
    }

    //Registers a self-contained JSON language for the read-only results viewer. The vendored Monaco
    //ships no built-in 'json' contribution (only cypher/sparql under basic-languages), so using the
    //built-in id would trigger a failing lazy-load of a missing file — which can crash the app. This
    //custom id + Monarch tokenizer highlights JSON without loading anything.
    function registerJsonView() {
        const exists = monaco.languages.getLanguages().some(function (l) {
            return l.id === 'jsonview';
        });

        if (exists)
            return;

        monaco.languages.register({ id: 'jsonview' });

        monaco.languages.setMonarchTokensProvider('jsonview', {
            defaultToken: '',
            tokenizer: {
                root: [
                    [/"(?:[^"\\]|\\.)*"\s*(?=:)/, 'type'],
                    [/"(?:[^"\\]|\\.)*"/, 'string'],
                    [/\b(?:true|false|null)\b/, 'keyword'],
                    [/-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?/, 'number'],
                    [/[{}\[\]]/, '@brackets'],
                    [/[:,]/, 'delimiter']
                ]
            }
        });

        monaco.languages.setLanguageConfiguration('jsonview', {
            brackets: [['{', '}'], ['[', ']']]
        });
    }

    //Registers just enough JavaScript to read the GUN preview. GUN has no query language, so the viewer
    //shows the chained code it will actually run — gun.get('alice').map().once(…) — and this highlights
    //it. Monaco's real 'javascript' contribution is not vendored, and a small Monarch is all a four-line
    //read-only preview needs.
    function registerGunJs() {
        const exists = monaco.languages.getLanguages().some(function (l) {
            return l.id === 'gunjs';
        });

        if (exists)
            return;

        monaco.languages.register({ id: 'gunjs' });

        monaco.languages.setMonarchTokensProvider('gunjs', {
            defaultToken: '',
            tokenizer: {
                root: [
                    [/\/\/.*$/, 'comment'],
                    [/\b(?:gun|soul)\b/, 'keyword'],
                    [/\b(?:get|map|once|on|put|set|back)(?=\s*\()/, 'type'],
                    [/'(?:[^'\\]|\\.)*'/, 'string'],
                    [/"(?:[^"\\]|\\.)*"/, 'string'],
                    [/\d+/, 'number'],
                    [/[()]/, '@brackets'],
                    [/[.,]/, 'delimiter']
                ]
            }
        });

        monaco.languages.setLanguageConfiguration('gunjs', {
            comments: { lineComment: '//' },
            brackets: [['(', ')']]
        });
    }

    //True when the cursor sits inside a single- or double-quoted string on the current line.
    function insideString(text) {
        let inSingle = false;
        let inDouble = false;

        for (let i = 0; i < text.length; i++) {
            const c = text[i];

            if (c === "'" && !inDouble)
                inSingle = !inSingle;
            else if (c === '"' && !inSingle)
                inDouble = !inDouble;
        }

        return inSingle || inDouble;
    }

    //Guesses which schema category the current string argument wants, from the step that opens it:
    //hasLabel('… → vertex label, out('… → edge label, has('… / values('… → property key.
    function schemaCategory(linePrefix) {
        const match = /(\w+)\s*\(\s*['"][^'"]*$/.exec(linePrefix);
        let step = '';
        if (match)
            step = match[1];

        if (/^(hasLabel|label|V)$/.test(step))
            return 'vertexLabel';

        if (/^(out|in|both|outE|inE|bothE|to|from)$/.test(step))
            return 'edgeLabel';

        if (/^(has|hasKey|values|valueMap|properties|by|group|groupCount|order|dedup)$/.test(step))
            return 'propertyKey';

        return 'any';
    }

    //Completion items for the schema labels / property keys relevant to the current context (an
    //unknown context offers all three so nothing is hidden).
    function schemaSuggestions(linePrefix, range) {
        const category = schemaCategory(linePrefix);
        const kinds = monaco.languages.CompletionItemKind;
        const items = [];

        function add(names, kind, detail) {
            (names || []).forEach(function (name) {
                items.push({ label: name, kind: kind, insertText: name, detail: detail, range: range });
            });
        }

        if (category === 'vertexLabel')
            add(schemaData.vertexLabels, kinds.Class, 'vertex label');
        else if (category === 'edgeLabel')
            add(schemaData.edgeLabels, kinds.Interface, 'edge label');
        else if (category === 'propertyKey')
            add(schemaData.propertyKeys, kinds.Field, 'property');
        else {
            add(schemaData.vertexLabels, kinds.Class, 'vertex label');
            add(schemaData.edgeLabels, kinds.Interface, 'edge label');
            add(schemaData.propertyKeys, kinds.Field, 'property');
        }

        return items;
    }

    function currentTheme() {
        if (document.documentElement.getAttribute('data-bs-theme') === 'dark')
            return 'vs-dark';

        return 'vs';
    }

    return {
        //Creates (or recreates) the single editor inside the given element and binds it to a
        //Blazor component reference; content changes are pushed back via OnMonacoChanged.
        create: async function (elementId, text, dotNetReference, language) {
            await ensureMonaco();

            const el = document.getElementById(elementId);
            if (!el)
                return;

            if (editor) {
                editor.dispose();
                editor = null;
            }

            //Fresh editor: drop any debug ranges/decorations left over from a previously created one
            //(e.g. after switching between the Query and Generated tabs) so stale step clicks can't fire.
            debugRanges = [];
            debugDecorations = [];

            dotNetRef = dotNetReference;

            editor = monaco.editor.create(el, {
                value: text || '',
                language: language || 'gremlin',
                theme: currentTheme(),
                automaticLayout: true,
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                fontSize: 13,
                lineNumbers: 'on',
                roundedSelection: false,
                //Render the suggest/hover popups at document.body so the short editor and its
                //Bootstrap card can't clip them.
                fixedOverflowWidgets: true,
                //Auto-suggest inside strings too, so schema labels / property keys pop as you type them.
                quickSuggestions: { other: true, comments: false, strings: true },
                suggestOnTriggerCharacters: true
            });

            editor.onDidChangeModelContent(function () {
                if (suppressChange)
                    return;

                if (dotNetRef)
                    dotNetRef.invokeMethodAsync('OnMonacoChanged', editor.getValue());
            });

            //While debugging, clicking a step highlights it and reports its index to .NET.
            editor.onMouseDown(function (e) {
                if (debugRanges.length === 0 || !dotNetRef)
                    return;

                if (!e.target || !e.target.position)
                    return;

                const offset = editor.getModel().getOffsetAt(e.target.position);
                for (let i = 0; i < debugRanges.length; i++) {
                    if (offset >= debugRanges[i].start && offset < debugRanges[i].end) {
                        dotNetRef.invokeMethodAsync('OnDebugStepClicked', i);
                        return;
                    }
                }
            });
        },

        setReadOnly: function (readOnly) {
            if (editor)
                editor.updateOptions({ readOnly: !!readOnly });
        },

        //Underlines each debuggable step (the active one stronger) and enables click-to-focus. cursorJson, when
        //set, is the [start,end] of a nested sub-traversal step the cursor stepped into — highlighted distinctly.
        setDebug: function (rangesJson, activeIndex, cursorJson) {
            if (!editor)
                return;

            const parsed = JSON.parse(rangesJson);
            debugRanges = parsed.map(function (r) {
                return { start: r[0], end: r[1] };
            });

            const model = editor.getModel();
            const decorations = debugRanges.map(function (r, i) {
                const start = model.getPositionAt(r.start);
                const end = model.getPositionAt(r.end);

                let cls = 'debug-step';
                if (i === activeIndex)
                    cls = 'debug-step debug-step-active';

                return {
                    range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column),
                    options: { inlineClassName: cls }
                };
            });

            if (cursorJson) {
                const c = JSON.parse(cursorJson);

                if (c && c.length === 2) {
                    const cs = model.getPositionAt(c[0]);
                    const ce = model.getPositionAt(c[1]);

                    decorations.push({
                        range: new monaco.Range(cs.lineNumber, cs.column, ce.lineNumber, ce.column),
                        options: { inlineClassName: 'debug-step debug-step-cursor' }
                    });
                }
            }

            debugDecorations = editor.deltaDecorations(debugDecorations, decorations);
        },

        clearDebug: function () {
            debugRanges = [];

            if (editor)
                debugDecorations = editor.deltaDecorations(debugDecorations, []);
        },

        //Sets the schema vocabulary used for autocomplete (vertex labels, edge labels, property keys).
        setSchema: function (schema) {
            schemaData = {
                vertexLabels: (schema && schema.vertexLabels) || [],
                edgeLabels: (schema && schema.edgeLabels) || [],
                propertyKeys: (schema && schema.propertyKeys) || []
            };
        },

        //Switches the syntax-highlighting language of the live editor (gremlin / cypher / sparql).
        //cypher and sparql are Monaco built-ins (lazy-loaded from vs/basic-languages); gremlin is custom.
        setLanguage: function (language) {
            if (!editor)
                return;

            monaco.editor.setModelLanguage(editor.getModel(), language || 'gremlin');
        },

        //Pushes an external value into the editor without echoing a change back to .NET.
        setValue: function (text) {
            if (!editor)
                return;

            const next = text || '';
            if (editor.getValue() === next)
                return;

            suppressChange = true;
            editor.setValue(next);
            suppressChange = false;
        },

        getValue: function () {
            if (editor)
                return editor.getValue();

            return '';
        },

        //Creates (or recreates) a separate READ-ONLY editor for viewing text (the JSON results view).
        //It's independent of the main query editor, so both can be on screen at the same time. Theme
        //changes are global (monaco.editor.setTheme), so setTheme already covers this one too.
        createViewer: async function (elementId, text) {
            try {
                await ensureMonaco();

                const el = document.getElementById(elementId);
                if (!el)
                    return;

                if (viewerEditor) {
                    viewerEditor.dispose();
                    viewerEditor = null;
                }

                viewerEditor = monaco.editor.create(el, {
                    value: text || '',
                    language: 'jsonview',
                    theme: currentTheme(),
                    readOnly: true,
                    automaticLayout: true,
                    minimap: { enabled: false },
                    scrollBeyondLastLine: false,
                    fontSize: 13,
                    lineNumbers: 'on',
                    folding: true,
                    fixedOverflowWidgets: true
                });
            }
            catch (e) {
                //A viewer failure must never tear down the whole app.
                console.warn('Monaco JSON viewer failed to initialize:', e);
            }
        },

        setViewerValue: function (text) {
            if (!viewerEditor)
                return;

            const next = text || '';
            if (viewerEditor.getValue() === next)
                return;

            viewerEditor.setValue(next);
        },

        disposeViewer: function () {
            if (viewerEditor) {
                viewerEditor.dispose();
                viewerEditor = null;
            }
        },

        setTheme: function (isDark) {
            if (!window.monaco)
                return;

            if (isDark)
                monaco.editor.setTheme('vs-dark');
            else
                monaco.editor.setTheme('vs');
        },

        dispose: function () {
            if (editor) {
                editor.dispose();
                editor = null;
            }

            dotNetRef = null;
        }
    };
})();
