//Browser-direct GUN, over the vendored bundle (window.Gun). GunDb.cs drives this.
//
//GUN has no query language — it is a chained API, gun.get('alice').get('knows') — so the viewer's "query"
//is a path of keys, optionally followed by ~depth. This resolves that path to a node and then walks the
//links outward, returning the reachable sub-graph in GUN's own shape for GunConverter to read.
//
//It is also the one backend that cannot run on the host: GUN is a JavaScript library with no .NET
//counterpart, so a GUN connection is browser-direct or nothing.
window.gunInterop = (function () {
    const instances = new Map();

    //How long to wait for a node to arrive. GUN is push-based and never says "not found" — a key that does
    //not exist simply never calls back — so a read has to be given a deadline of its own.
    const READ_TIMEOUT_MS = 2500;

    ///Default hops walked outward from the resolved node.
    const DEFAULT_DEPTH = 1;

    //How long to wait for a write to be acknowledged. A write GUN accepts is already applied locally, so
    //this only bounds how long an error can take to arrive.
    const WRITE_TIMEOUT_MS = 2000;

    //A cap, so a "~depth" on a densely linked graph cannot walk the whole peer into the browser.
    const MAX_NODES = 2000;

    function instanceFor(handle, config) {
        let gun = instances.get(handle);

        if (gun)
            return gun;

        const options = {};

        //No peer means a purely local, in-browser graph — which is a legitimate way to run GUN.
        if (config.peers && config.peers.length)
            options.peers = config.peers;

        //localStorage is off: the viewer is a window onto someone's database, and should not quietly
        //accumulate a copy of it in the browser.
        options.localStorage = false;
        options.radisk = false;

        gun = Gun(options);
        instances.set(handle, gun);

        return gun;
    }

    //Reads one node by soul, resolving to its raw form or null if nothing arrives in time.
    function readSoul(gun, soul) {
        return new Promise(function (resolve) {
            let settled = false;

            const timer = setTimeout(function () {
                if (!settled) {
                    settled = true;
                    resolve(null);
                }
            }, READ_TIMEOUT_MS);

            gun.get(soul).once(function (data) {
                if (settled)
                    return;

                settled = true;
                clearTimeout(timer);
                resolve(data || null);
            });
        });
    }

    //Walks a chain of keys from a starting soul, so "alice/knows" lands on whatever alice.knows links to.
    async function resolvePath(gun, keys) {
        let soul = keys[0];
        let node = await readSoul(gun, soul);

        for (let i = 1; i < keys.length; i++) {
            if (!node)
                return { soul: null, node: null, missing: keys.slice(0, i + 1).join('/') };

            const value = node[keys[i]];
            const link = value && value['#'];

            if (!link)
                return { soul: null, node: null, missing: keys.slice(0, i + 1).join('/') };

            soul = link;
            node = await readSoul(gun, soul);
        }

        return { soul: soul, node: node, missing: null };
    }

    //Strips GUN's own bookkeeping into the plain shape the converter reads, keeping the soul.
    function plain(soul, node) {
        const out = { _: { '#': soul } };

        for (const key of Object.keys(node)) {
            if (key === '_')
                continue;

            const value = node[key];

            if (value && typeof value === 'object' && value['#'])
                out[key] = { '#': value['#'] };
            else
                out[key] = value;
        }

        return out;
    }

    ///Reads the graph reachable from a path. Returns GUN's soul-keyed graph, or an { error } envelope.
    async function run(handle, config, query) {
        try {
            const gun = instanceFor(handle, config);
            const parsed = parseQuery(query);

            if (!parsed.keys.length)
                return JSON.stringify({ error: "Enter a key to start from. GUN cannot list every node, so a read has to begin somewhere known." });

            const graph = {};
            let frontier = [];

            if (parsed.map) {
                //Walking a node's children — how a GUN graph is normally listed.
                const children = await readChildren(gun, parsed.keys);

                if (!children.length)
                    return JSON.stringify({ error: "'" + parsed.keys.join('/') + "' has no children to list. GUN reports no error for a key that does not exist — it simply never answers — so this may also mean the key is wrong or the peer is unreachable." });

                for (const child of children) {
                    graph[child.soul] = plain(child.soul, child.node);
                    frontier.push(child);
                }
            }
            else {
                const start = await resolvePath(gun, parsed.keys);

                if (!start.node) {
                    if (start.missing)
                        return JSON.stringify({ error: "Nothing found at '" + start.missing + "'." });

                    return JSON.stringify({ error: "Nothing found at '" + parsed.keys[0] + "'. GUN reports no error for a key that does not exist — it simply never answers — so this may also mean the peer is unreachable." });
                }

                graph[start.soul] = plain(start.soul, start.node);
                frontier.push({ soul: start.soul, node: start.node });
            }

            //Breadth-first from there, following links out to the requested depth.

            for (let hop = 0; hop < parsed.depth; hop++) {
                const next = [];

                for (const current of frontier) {
                    for (const key of Object.keys(current.node)) {
                        if (key === '_')
                            continue;

                        const value = current.node[key];
                        const link = value && typeof value === 'object' && value['#'];

                        if (!link || graph[link])
                            continue;

                        if (Object.keys(graph).length >= MAX_NODES)
                            break;

                        const node = await readSoul(gun, link);

                        //A link whose target never arrives still stands: the converter fills in an empty
                        //node for it, so the edge is drawn rather than silently dropped.
                        if (node) {
                            graph[link] = plain(link, node);
                            next.push({ soul: link, node: node });
                        }
                    }
                }

                if (!next.length)
                    break;

                frontier = next;
            }

            return JSON.stringify(graph);
        }
        catch (e) {
            let message = String(e);
            if (e && e.message)
                message = e.message;

            return JSON.stringify({ error: message });
        }
    }

    ///Splits "users* ~2" into its key path, whether to map over children, and its depth. The mirror of
    ///GunQuery in C#, which is what builds these — keep the two in step.
    function parseQuery(query) {
        let text = (query || '').trim();
        let depth = DEFAULT_DEPTH;

        const tilde = text.lastIndexOf('~');

        if (tilde >= 0) {
            const requested = parseInt(text.substring(tilde + 1).trim(), 10);

            if (!isNaN(requested))
                depth = Math.max(0, Math.min(requested, 5));

            text = text.substring(0, tilde).trim();
        }

        //A trailing * means .map() — walk the node's children rather than reading the node itself.
        const map = text.endsWith('*');

        if (map)
            text = text.substring(0, text.length - 1).trim();

        const keys = text.split('/').map(function (k) { return k.trim(); }).filter(function (k) { return k.length > 0; });

        return { keys: keys, map: map, depth: depth };
    }

    //Reads a node's children — GUN's .map(). This is how a GUN graph is normally listed: souls cannot be
    //enumerated, so the data hangs off a root node that can be walked.
    function readChildren(gun, keys) {
        return new Promise(function (resolve) {
            const found = [];
            let chain = gun;

            for (const key of keys)
                chain = chain.get(key);

            //.map() fires once per child and never signals "that was the last one", so the deadline is
            //what ends it — the same reason a single read carries one.
            const timer = setTimeout(function () { resolve(found); }, READ_TIMEOUT_MS);

            chain.map().once(function (data, key) {
                if (!data || typeof data !== 'object')
                    return;

                const soul = data._ && data._['#'];

                if (soul && !found.some(function (f) { return f.soul === soul; }))
                    found.push({ soul: soul, node: data });
            });

            //Kept so a caller can see the timer is deliberate rather than forgotten.
            void timer;
        });
    }

    ///Performs one write. GunDb sends the operation, never the statement text — the JavaScript the user
    ///reviews in the Generated tab describes what will happen, and this is what actually happens. Nothing
    ///typed into that box is evaluated. Returns an error message, or "" when the write went through.
    async function apply(handle, config, op) {
        try {
            const gun = instanceFor(handle, config);

            if (!op || !op.soul)
                return "A GUN write needs a key to write to.";

            let chain = gun.get(op.soul);

            if (op.edge)
                chain = chain.get(op.edge);

            if (op.kind === 'clear')
                //GUN has no delete: null is the tombstone, and it syncs like any other write.
                return await acknowledged(chain, null);

            if (op.kind === 'link') {
                if (!op.target)
                    return "A GUN link needs a node to link to.";

                return await acknowledged(chain, gun.get(op.target));
            }

            if (op.kind === 'put') {
                const values = JSON.parse(op.values || '{}');

                //GUN rejects an empty put, and a write of nothing is not an error worth reporting either.
                if (!Object.keys(values).length)
                    return "";

                return await acknowledged(chain, values);
            }

            return "Unknown GUN write: " + op.kind;
        }
        catch (e) {
            if (e && e.message)
                return e.message;

            return String(e);
        }
    }

    //Writes and waits for GUN to acknowledge, returning its error or "". Without the ack a refused write
    //looks exactly like one that went through — GUN rejects some outright ("Data at root of graph must be
    //a node"), and a viewer that reported success for those would be lying.
    function acknowledged(chain, value) {
        return new Promise(function (resolve) {
            let settled = false;

            const timer = setTimeout(function () {
                if (!settled) {
                    settled = true;

                    //No ack in time is not a failure: GUN writes locally first and syncs after, so the
                    //write stands even if no peer has answered yet.
                    resolve("");
                }
            }, WRITE_TIMEOUT_MS);

            chain.put(value, function (ack) {
                if (settled)
                    return;

                settled = true;
                clearTimeout(timer);

                if (ack && ack.err)
                    resolve(String(ack.err));
                else
                    resolve("");
            });
        });
    }

    //Live subscriptions by handle: { chains: Map<soul, gunChain>, ref: DotNetObjectReference }.
    const watches = new Map();

    ///Watches everything the query reaches, pushing each change to .NET as it arrives.
    ///
    ///This is the thing GUN exists for and no other backend here can offer: .once() reads a snapshot,
    ///.on() keeps firing as peers change the data. Each callback pushes only the node that changed, in the
    ///same shape a query answers with, so the viewer merges it into what is drawn rather than redrawing.
    async function watch(handle, config, query, dotNetRef) {
        try {
            await unwatch(handle);

            const gun = instanceFor(handle, config);
            const parsed = parseQuery(query);

            if (!parsed.keys.length)
                return JSON.stringify({ error: "Enter a key to start from before switching Live on." });

            const state = { chains: new Map(), ref: dotNetRef, closed: false };
            watches.set(handle, state);

            //The same starting set the read resolves, so Live watches exactly what is on screen.
            let start = [];

            if (parsed.map) {
                start = await readChildren(gun, parsed.keys);
            }
            else {
                const resolved = await resolvePath(gun, parsed.keys);

                if (resolved.soul)
                    start = [{ soul: resolved.soul, node: resolved.node }];
            }

            if (!start.length)
                return JSON.stringify({ error: "Nothing to watch at '" + parsed.keys.join('/') + "'." });

            for (const entry of start)
                subscribe(gun, state, entry.soul, parsed.depth);

            return JSON.stringify({ watching: state.chains.size });
        }
        catch (e) {
            let message = String(e);
            if (e && e.message)
                message = e.message;

            return JSON.stringify({ error: message });
        }
    }

    //Subscribes to one soul, and — while there are hops left — to whatever it links to as those appear.
    //A link's target may not exist yet: adding one is exactly the change Live is here to show.
    function subscribe(gun, state, soul, hopsLeft) {
        if (state.closed || state.chains.has(soul) || state.chains.size >= MAX_NODES)
            return;

        const chain = gun.get(soul);
        state.chains.set(soul, chain);

        chain.on(function (data) {
            if (state.closed || !data || typeof data !== 'object')
                return;

            const graph = {};
            graph[soul] = plain(soul, data);

            if (hopsLeft > 0)
                for (const key of Object.keys(data)) {
                    if (key === '_')
                        continue;

                    const value = data[key];
                    const link = value && typeof value === 'object' && value['#'];

                    if (link)
                        subscribe(gun, state, link, hopsLeft - 1);
                }

            //Fire-and-forget: GUN calls this from its own event loop, and a push that fails (the page is
            //navigating, the component is gone) must not take the subscription down with it.
            try {
                state.ref.invokeMethodAsync('OnGunGraphChanged', JSON.stringify(graph));
            }
            catch (e) { }
        });
    }

    ///Stops watching. GUN unsubscribes through the chain's own .off().
    async function unwatch(handle) {
        const state = watches.get(handle);

        if (!state)
            return;

        state.closed = true;
        watches.delete(handle);

        for (const chain of state.chains.values())
            try {
                chain.off();
            }
            catch (e) { }

        state.chains.clear();
    }

    async function close(handle) {
        await unwatch(handle);

        const gun = instances.get(handle);

        if (!gun)
            return;

        instances.delete(handle);

        //GUN has no disconnect of its own; dropping the reference lets its peers close with the page.
        try {
            if (gun.back && gun.back('opt.peers')) {
                const peers = gun.back('opt.peers');

                for (const url of Object.keys(peers))
                    if (peers[url] && peers[url].wire && peers[url].wire.close)
                        peers[url].wire.close();
            }
        }
        catch (e) { }
    }

    return { run: run, apply: apply, watch: watch, unwatch: unwatch, close: close, parseQuery: parseQuery };
})();
