//Browser-direct Bolt for Neo4j / Memgraph, over the vendored neo4j-driver (window.neo4j, Bolt over
//WebSocket). Neo4jBrowserDb.cs drives this: run(handle, config, query) returns the same records envelope
//Neo4jServerDb builds on the host, so the record→graph mapping lives once, in C#'s Neo4jConverter. One
//driver is kept per handle so a bulk commit reuses a single WebSocket instead of dialing per query.
window.neo4jInterop = (function () {
    const drivers = new Map();

    function driverFor(handle, config) {
        let entry = drivers.get(handle);

        if (entry)
            return entry;

        const auth = neo4j.auth.basic(config.username, config.password);
        //disableLosslessIntegers turns Bolt integers into plain JS numbers, so JSON carries "30", not a
        //{low,high} Integer object — matching the exact-text stringifying the .NET side relies on.
        const driver = neo4j.driver(config.uri, auth, { disableLosslessIntegers: true });

        entry = { driver: driver, database: config.database };
        drivers.set(handle, entry);

        return entry;
    }

    function isTemporalOrSpatial(value) {
        return neo4j.isDate(value) || neo4j.isDateTime(value) || neo4j.isLocalDateTime(value)
            || neo4j.isTime(value) || neo4j.isLocalTime(value) || neo4j.isDuration(value)
            || neo4j.isPoint(value);
    }

    function nodeObject(node) {
        return { "$e": "node", id: node.elementId, labels: node.labels, props: mapProps(node.properties) };
    }

    function relObject(rel) {
        return {
            "$e": "rel",
            id: rel.elementId,
            type: rel.type,
            start: rel.startNodeElementId,
            end: rel.endNodeElementId,
            props: mapProps(rel.properties)
        };
    }

    function mapProps(properties) {
        const result = {};

        for (const key of Object.keys(properties))
            result[key] = mapValue(properties[key]);

        return result;
    }

    //Maps one Bolt value to its envelope form: nodes / relationships / paths get their $e tag, a returned
    //list or map recurses, and everything else travels as a JSON scalar (temporal and spatial types, which
    //have no JSON scalar, fall back to their string form) — the mirror of Neo4jServerDb.MapValue.
    function mapValue(value) {
        if (value === null || value === undefined)
            return null;

        if (neo4j.isNode(value))
            return nodeObject(value);

        if (neo4j.isRelationship(value))
            return relObject(value);

        if (neo4j.isPath(value)) {
            const nodes = [nodeObject(value.start)];
            const rels = [];

            for (const segment of value.segments) {
                rels.push(relObject(segment.relationship));
                nodes.push(nodeObject(segment.end));
            }

            return { "$e": "path", nodes: nodes, rels: rels };
        }

        //A stray lossless Integer (should not appear with disableLosslessIntegers, but a nested one might).
        if (neo4j.isInt(value))
            return value.toString();

        if (isTemporalOrSpatial(value))
            return value.toString();

        if (Array.isArray(value))
            return value.map(mapValue);

        if (typeof value === "object") {
            const result = {};

            for (const key of Object.keys(value))
                result[key] = mapValue(value[key]);

            return result;
        }

        return value;
    }

    //Flattens a query plan into the rows CypherPlan describes. Kept in step with Neo4jServerDb's version
    //by name: the C# side reads these exact columns whichever driver produced them.
    function planRows(plan, depth, rows, profiled) {
        //Neo4j reports an operator's time in nanoseconds, under an argument whose casing varies by version.
        let timeMs = null;
        const args = plan.arguments || {};
        const rawTime = args.Time !== undefined ? args.Time : args.time;

        if (rawTime !== undefined && rawTime !== null && !isNaN(Number(rawTime)))
            timeMs = Number(rawTime) / 1000000;

        //Neo4j supplies the concise "what this operator worked on" under Details, so it is preferred.
        //The fallback skips string-representation especially: that argument is the whole pretty-printed
        //plan as ASCII art, and would swamp the row it lands on.
        const skip = ['Time', 'time', 'Rows', 'DbHits', 'PageCacheHits', 'PageCacheMisses', 'PageCacheHitRatio',
            'EstimatedRows', 'planner', 'planner-impl', 'planner-version', 'runtime', 'runtime-impl',
            'runtime-version', 'version', 'Memory', 'GlobalMemory', 'Id', 'string-representation', 'Cypher'];

        let detailText;
        if (args.Details !== undefined && args.Details !== null) {
            detailText = String(args.Details);
        }
        else {
            const details = [];

            for (const key of Object.keys(args))
                if (skip.indexOf(key) < 0 && args[key] !== null && args[key] !== undefined)
                    details.push(String(args[key]));

            detailText = details.join(', ');
        }

        //A detail is a table cell, so it stays on one line and within a sane width.
        detailText = detailText.replace(/[\r\n]+/g, ' ').trim();
        if (detailText.length > 200)
            detailText = detailText.substring(0, 200) + '…';

        //EXPLAIN never ran the query, so only a profiled plan has anything measured to report.
        rows.push({
            depth: depth,
            operator: plan.operatorType,
            rows: profiled ? Number(plan.rows) : null,
            dbHits: profiled ? Number(plan.dbHits) : null,
            timeMs: profiled ? timeMs : null,
            details: detailText
        });

        for (const child of plan.children || [])
            planRows(child, depth + 1, rows, profiled);
    }

    //The plan envelope when EXPLAIN or PROFILE asked for one, else null. It has to come off the summary:
    //EXPLAIN produces no records at all, so reading only those would show an empty result.
    function planEnvelope(summary) {
        if (!summary)
            return null;

        const rows = [];

        if (summary.profile)
            planRows(summary.profile, 0, rows, true);
        else if (summary.plan)
            planRows(summary.plan, 0, rows, false);
        else
            return null;

        return JSON.stringify({
            columns: ['depth', 'operator', 'rows', 'dbHits', 'timeMs', 'details'],
            records: rows
        });
    }

    async function run(handle, config, query) {
        let session;

        try {
            const entry = driverFor(handle, config);

            if (entry.database)
                session = entry.driver.session({ database: entry.database });
            else
                session = entry.driver.session();

            const result = await session.run(query);

            const plan = planEnvelope(result.summary);
            if (plan)
                return plan;

            const records = result.records.map(function (record) {
                const row = {};

                for (const key of record.keys)
                    row[key] = mapValue(record.get(key));

                return row;
            });

            //Column order comes off the first record; an empty result has none, and Neo4jConverter reads
            //an empty column list as an empty graph rather than a zero-row table.
            let columns = [];
            if (result.records.length > 0)
                columns = result.records[0].keys;

            return JSON.stringify({ columns: columns, records: records });
        }
        catch (e) {
            let message = String(e);
            if (e && e.message)
                message = e.message;

            return JSON.stringify({ error: message });
        }
        finally {
            if (session) {
                try {
                    await session.close();
                }
                catch (e) { }
            }
        }
    }

    async function close(handle) {
        const entry = drivers.get(handle);

        if (!entry)
            return;

        drivers.delete(handle);

        try {
            await entry.driver.close();
        }
        catch (e) { }
    }

    return { run: run, close: close };
})();
