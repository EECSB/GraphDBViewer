//The embedded viewer's messages with the page that embeds it (2026-10-23, DESIGN.md's embeddable viewer), the browser's
//half: Home.EmbedMessages.cs does what a message asks. Off unless the embedding page names its own origin in the viewer's
//address (EmbedSettings' origin), and the viewer is in a frame at all. On, a message is read only when it comes from the
//parent window and from that origin exactly, and the viewer posts only to the parent window, addressed to that origin and
//never to '*': a page that has since gone somewhere else is told nothing.
//
//A message is a plain object with a namespaced type. One this does not know, or one whose fields are not what its type
//needs, is dropped without an answer, and nothing in a message is run as code: a query is text the viewer sends its
//graph, as the address's own query is, and a view and a theme are names looked up.
//
//The page sends:
//  { type: 'treeality:query', id, query }  a query to run, read-only; the id, a string or a number, comes back on its result
//  { type: 'treeality:view', view }        json, 2d, 3d, table, map or globe
//  { type: 'treeality:theme', theme }      dark, light, tinted, dark-blue, light-blue or blue-tinted
//The viewer sends:
//  { type: 'treeality:ready', version }                          once, when it has started listening
//  { type: 'treeality:result', id, ok: true, vertices, edges }   what a query drew
//  { type: 'treeality:result', id, ok: false, error }            or why it drew nothing
//  { type: 'treeality:selection', element }                      each time the selection changes: { kind, id, label, properties }, or null
window.embedMessagesInterop = {
    //The messages' version, which ready carries: raised when a message changes in a way a page written for this one would misread.
    version: 1,

    dotNetRef: null,
    origin: null,
    handler: null,

    //Starts listening to the page around the viewer and says ready to it. Returns whether it did: not when the viewer is
    //in no frame, and not without an origin to hold the page to.
    attach: function (ref, origin) {
        this.detach();

        if (typeof origin !== 'string' || origin === '' || window.parent === window)
            return false;

        this.dotNetRef = ref;
        this.origin = origin;

        const self = this;

        this.handler = function (e) {
            if (!self.accepts(e))
                return;

            const message = self.read(e.data);

            if (message)
                self.dispatch(message);
        };

        window.addEventListener('message', this.handler);
        this.post({ type: 'treeality:ready', version: this.version });

        return true;
    },

    detach: function () {
        if (this.handler)
            window.removeEventListener('message', this.handler);

        this.handler = null;
        this.dotNetRef = null;
        this.origin = null;
    },

    //Whether a message event came from the page that embeds the viewer: the parent window, at the origin it named.
    accepts: function (e) {
        return !!e && this.origin !== null && e.source === window.parent && e.origin === this.origin;
    },

    //What a message asks, as plain strings, or null when it asks nothing this knows. A blank query is a query all the
    //same, handed on so the viewer answers it ("No query was sent.", EmbedQueryAnswer.Blank): every treeality:query gets
    //one treeality:result, and a page that sent one empty by mistake hears why rather than nothing (2026-11-07, the user).
    read: function (data) {
        if (data === null || typeof data !== 'object' || Array.isArray(data))
            return null;

        if (data.type === 'treeality:query' && typeof data.query === 'string')
            return { type: data.type, id: this.idOf(data.id), query: data.query };
        else if (data.type === 'treeality:view' && typeof data.view === 'string')
            return { type: data.type, view: data.view };
        else if (data.type === 'treeality:theme' && typeof data.theme === 'string')
            return { type: data.type, theme: data.theme };

        return null;
    },

    //A query's id as its result carries it back: a string or a finite number, and anything else none.
    idOf: function (id) {
        if (typeof id === 'string' || Number.isFinite(id))
            return id;

        return null;
    },

    dispatch: function (message) {
        const ref = this.dotNetRef;
        const self = this;

        if (!ref)
            return Promise.resolve();

        if (message.type === 'treeality:query')
            return ref.invokeMethodAsync('OnEmbedQuery', message.query).then(
                function (answer) {
                    self.answer(message.id, answer);
                },
                function () {
                    self.answer(message.id, null);
                });
        else if (message.type === 'treeality:view')
            return ref.invokeMethodAsync('OnEmbedView', message.view).catch(function () { });

        return ref.invokeMethodAsync('OnEmbedTheme', message.theme).catch(function () { });
    },

    //Posts a query's result: what it drew, or why it drew nothing.
    answer: function (id, answer) {
        if (answer && answer.ok)
            this.post({ type: 'treeality:result', id: id, ok: true, vertices: answer.vertices, edges: answer.edges });
        else {
            let error = 'The query could not be run.';

            if (answer && typeof answer.error === 'string' && answer.error !== '')
                error = answer.error;

            this.post({ type: 'treeality:result', id: id, ok: false, error: error });
        }
    },

    //Tells the page what is selected now: { kind, id, label, properties }, or null for nothing.
    selection: function (element) {
        this.post({ type: 'treeality:selection', element: element || null });
    },

    post: function (message) {
        if (this.origin === null || window.parent === window)
            return;

        try {
            window.parent.postMessage(message, this.origin);
        }
        catch (e) { }
    }
};
