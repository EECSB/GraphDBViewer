window.graph3DInterop = {
    instance: null,
    dotNetRef: null,
    container: null,
    resizeObserver: null,
    resizeFrameId: null,
    labelLayer: null,
    labelFrameId: null,
    nodeLabels: [],
    linkLabels: [],
    nodeImages: [],
    axisSvg: null,
    axisItems: [],
    showNodeLabels: true,
    showLinkLabels: true,
    //Draw distances as 0–100 percentages. 100 = show everything (culling off, the default, so the view is
    //unchanged until a slider moves); lower hides things farther from the camera. Labels fade; node
    //geometry (and the edges touching a hidden node) is toggled off. Stored as percentages, not absolute
    //distances, because the per-frame loop derives the world-unit threshold from the graph's current
    //reach — which keeps growing while the force layout settles.
    labelDrawPercent: 100,
    nodeDrawPercent: 100,
    //Graph reach (radius from the centroid), refreshed on a throttle while either culling is active.
    graphExtent: 200,
    _extentTick: 0,
    //Widens the sliders' reach past the graph's own radius so the top of each range still shows everything
    //at the default framing. Shared by both draw-distance sliders.
    drawDistanceScale: 4,
    //Type labels (glabel) hidden by the label filter, so distance culling never un-hides a filtered node.
    hiddenLabelSet: null,

    setDotNetRef: function (ref) {
        this.dotNetRef = ref;
    },

    //Applies a dagMode hierarchical layout, or a free force layout when falsy/'force'.
    //Valid dagModes: td, bu, lr, rl, zout, zin, radialout, radialin.
    //onDagError no-op lets cyclic graphs (e.g. flight routes) still render instead of throwing.
    applyLayout: function (graph, dagMode) {
        let mode;
        //'userSaved' is not a hierarchical dagMode — it's the hand-pinned arrangement applied via
        //applyPositions (fx/fy/fz), so keep the free force layout and let the fixed positions win.
        if (dagMode && dagMode !== 'force' && dagMode !== 'userSaved')
            mode = dagMode;
        else
            mode = null;

        //Committed gdbv positions (fx/fy/fz) are honored ONLY for the user-saved layout; for every
        //other layout free the nodes so the selected layout can arrange them. Mutate the live node
        //objects in place (the simulation references them and picks it up on its next tick) rather than
        //re-setting graphData, which re-processes link references and can break the render.
        if (dagMode !== 'userSaved') {
            const nodes = graph.graphData().nodes || [];
            nodes.forEach(n => { n.fx = undefined; n.fy = undefined; n.fz = undefined; });
        }

        graph.dagMode(mode)
             .dagLevelDistance(60)
             .onDagError(() => { });

        return graph;
    },

    //Switches the layout on the already-drawn 3D graph and re-heats the simulation.
    setLayout: function (dagMode) {
        if (this.instance) {
            this.applyLayout(this.instance, dagMode);
            this.instance.d3ReheatSimulation();
        }
    },

    //Pins nodes at the given saved positions — the "user saved positions" layout. json is
    //[{id,x,y,z}]. Setting fx/fy/fz fixes each node so the arrangement is restored exactly.
    applyPositions: function (json) {
        if (!this.instance)
            return;

        const byId = {};
        JSON.parse(json).forEach(p => byId[p.id] = p);

        const data = this.instance.graphData();
        data.nodes.forEach(n => {
            const p = byId[n.id];
            if (p) {
                n.fx = p.x;
                n.fy = p.y;
                n.fz = p.z;
            }
        });

        this.instance.graphData(data);
        this.instance.d3ReheatSimulation();
        this.instance.zoomToFit(400, 50);
    },

    drawGraph: function (containerId, graphDataJson, dagMode) {
        const container = document.getElementById(containerId);
        if (!container) {
            console.error("3D graph container not found:", containerId);
            return;
        }

        const graphData = JSON.parse(graphDataJson);

        if (this.instance) {
            this.instance._destructor && this.instance._destructor();
            this.instance = null;
        }

        container.innerHTML = '';

        const self = this;

        const graph = ForceGraph3D()(container)
            .graphData(graphData)
            .nodeAutoColorBy('group')
            .nodeColor(node => node.bgColor || node.color)
            .nodeVal(node => {
                if (node.nodeSize)
                    return Math.pow(node.nodeSize / 40, 3);
                else
                    return 1;
            })
            //Hover tooltips — suppressed when the matching always-on labels are shown, so the same
            //text doesn't appear twice (the overlay label + the hover tooltip).
            .nodeLabel(node => self.showNodeLabels ? '' : (node.label || node.id))
            .linkLabel(link => self.showLinkLabels ? '' : (link.label || ''))
            .linkColor(link => link.color || '#999999')
            .linkDirectionalArrowLength(4)
            .linkDirectionalArrowRelPos(1)
            .width(container.clientWidth)
            .height(container.clientHeight)
            .onNodeClick(node => {
                if (!self.dotNetRef)
                    return;

                //Double-click a node to expand its neighbors; a single click just selects.
                const now = Date.now();
                if (self.expandNodeId === node.id && (now - self.expandNodeTime) < 350) {
                    self.dotNetRef.invokeMethodAsync('OnNodeExpand', String(node.id));
                    self.expandNodeId = null;
                    self.expandNodeTime = 0;
                } else {
                    self.expandNodeId = node.id;
                    self.expandNodeTime = now;
                }

                self.dotNetRef.invokeMethodAsync('OnElementSelected', JSON.stringify({
                    type: 'node',
                    id: node.id,
                    label: node.label || '',
                    glabel: node.glabel || '',
                    properties: node.properties || {}
                }));
            })
            .onLinkClick(link => {
                if (self.dotNetRef) {
                    let srcId;
                    if (typeof link.source === 'object')
                        srcId = link.source.id;
                    else
                        srcId = link.source;

                    let tgtId;
                    if (typeof link.target === 'object')
                        tgtId = link.target.id;
                    else
                        tgtId = link.target;

                    self.dotNetRef.invokeMethodAsync('OnElementSelected', JSON.stringify({
                        type: 'edge',
                        id: link.id || (srcId + '->' + tgtId),
                        label: link.label || '',
                        source: srcId,
                        target: tgtId,
                        properties: link.properties || {}
                    }));
                }
            })
            .onBackgroundClick(() => {
                //Empty-space click: add-component mode drops a new node here (the 3D force layout
                //positions it); otherwise .NET treats it as a deselect.
                if (self.dotNetRef) {
                    self.dotNetRef.invokeMethodAsync('OnCanvasTapped3d');
                }
            });

        const linkCountMap = {};
        graph.linkCurvature(link => {
            let srcId;
            if (typeof link.source === 'object')
                srcId = link.source.id;
            else
                srcId = link.source;

            let tgtId;
            if (typeof link.target === 'object')
                tgtId = link.target.id;
            else
                tgtId = link.target;

            const key = `${srcId}->${tgtId}`;

            if (!linkCountMap[key])
                linkCountMap[key] = 0;

            const count = ++linkCountMap[key];

            return Math.min(0.03 * (count - 1), 0.3);
        });

        this.applyLayout(graph, dagMode);
        this.instance = graph;
        this.container = container;

        this.setupLabels(container);
        this.setupCustomNodes();
        this.observeResize(container);
    },

    //Keeps the WebGL canvas sized to its container: the 3d-force-graph library fixes width/height at
    //creation and never re-measures, so without this a page/panel resize leaves the canvas at its old
    //size until the whole view is redrawn. A ResizeObserver pushes the new size straight into the instance.
    observeResize: function (container) {
        this.unobserveResize();

        if (typeof ResizeObserver === 'undefined')
            return;

        const self = this;
        this.resizeObserver = new ResizeObserver(() => {
            //Coalesce bursts of callbacks (e.g. a window drag fires many) into one update per frame.
            if (self.resizeFrameId)
                return;

            self.resizeFrameId = requestAnimationFrame(() => {
                self.resizeFrameId = null;
                self.applySize();
            });
        });

        this.resizeObserver.observe(container);
    },

    applySize: function () {
        if (!this.instance || !this.container)
            return;

        const w = this.container.clientWidth;
        const h = this.container.clientHeight;

        //Skip transient 0-sizes (e.g. while the 3D view is hidden) so the canvas isn't collapsed.
        if (w > 0 && h > 0) {
            this.instance.width(w);
            this.instance.height(h);
        }
    },

    unobserveResize: function () {
        if (this.resizeObserver) {
            this.resizeObserver.disconnect();
            this.resizeObserver = null;
        }

        if (this.resizeFrameId) {
            cancelAnimationFrame(this.resizeFrameId);
            this.resizeFrameId = null;
        }
    },

    //Incremental expansion: append not-yet-present nodes/links to the live 3D graph,
    //reusing existing node objects so their positions are preserved, then rebuild overlays.
    addData: function (graphJson) {
        if (!this.instance)
            return;

        const incoming = JSON.parse(graphJson);
        const cur = this.instance.graphData();

        const byId = new Map(cur.nodes.map(n => [n.id, n]));
        incoming.nodes.forEach(n => {
            const existing = byId.get(n.id);

            if (!existing) {
                cur.nodes.push(n);
                byId.set(n.id, n);

                return;
            }

            //Already drawn, but this answer may carry more: an expand is how a node that was only ever an
            //edge endpoint gets its own data. Copied onto the existing object so the simulation keeps its
            //position and velocity.
            Object.keys(n).forEach(key => {
                if (key === 'id' || key === 'x' || key === 'y' || key === 'z')
                    return;

                existing[key] = n[key];
            });
        });

        const linkKey = l => {
            let s;
            if (typeof l.source === 'object')
                s = l.source.id;
            else
                s = l.source;

            let t;
            if (typeof l.target === 'object')
                t = l.target.id;
            else
                t = l.target;

            return s + '->' + t + ':' + (l.label || '');
        };

        const linkKeys = new Set(cur.links.map(linkKey));
        incoming.links.forEach(l => {
            const k = linkKey(l);
            if (!linkKeys.has(k)) {
                cur.links.push(l);
                linkKeys.add(k);
            }
        });

        this.instance.graphData(cur);
        this.setupLabels(this.container);
    },

    //Reconciles the live 3D graph to match an effective graph ({nodes,links}) — the optimistic/
    //uncommitted view. Existing node objects are kept (so their positions survive) and updated in
    //place; new nodes/links are added and missing ones dropped. The force simulation is only re-heated
    //when the topology or a pinned position actually changed, so a color/label edit doesn't drift the
    //layout. graphJson has the same shape drawGraph consumes.
    syncData: function (graphJson) {
        if (!this.instance)
            return;

        const incoming = JSON.parse(graphJson);
        const cur = this.instance.graphData();

        const curNodeById = {};
        cur.nodes.forEach(n => curNodeById[n.id] = n);

        const displayKeys = ['label', 'group', 'glabel', 'properties', 'bgColor', 'nodeSize', 'shape', 'shape3d', 'image', 'model', 'showProps'];
        let positionChanged = false;

        //Build the reconciled node list, mutating kept node objects in place.
        const nodes = [];
        incoming.nodes.forEach(inc => {
            const existing = curNodeById[inc.id];
            if (existing) {
                displayKeys.forEach(k => {
                    if (inc[k] !== undefined)
                        existing[k] = inc[k];
                    else
                        delete existing[k];
                });

                ['fx', 'fy', 'fz'].forEach(k => {
                    if (inc[k] !== undefined && existing[k] !== inc[k]) {
                        existing[k] = inc[k];
                        positionChanged = true;
                    }
                });

                nodes.push(existing);
            } else {
                nodes.push(inc);
            }
        });

        const linkKey = l => {
            let s;
            if (typeof l.source === 'object')
                s = l.source.id;
            else
                s = l.source;

            let t;
            if (typeof l.target === 'object')
                t = l.target.id;
            else
                t = l.target;

            return s + '->' + t + ':' + (l.label || '');
        };

        const curLinkByKey = {};
        (cur.links || []).forEach(l => curLinkByKey[linkKey(l)] = l);

        const links = [];
        incoming.links.forEach(inc => {
            const existing = curLinkByKey[linkKey(inc)];
            if (existing) {
                existing.label = inc.label;
                existing.properties = inc.properties;
                links.push(existing);
            } else {
                links.push(inc);
            }
        });

        //Did the set of nodes / links change (add or remove)? Check both counts (catches removals) and
        //that every incoming element already existed (catches additions, incl. a simultaneous add+remove).
        const sameNodeCount = nodes.length === cur.nodes.length;
        const nodesAllKnown = incoming.nodes.every(n => curNodeById[n.id] !== undefined);
        const sameLinkCount = links.length === (cur.links || []).length;
        const curLinkKeys = new Set((cur.links || []).map(linkKey));
        const linksAllKnown = incoming.links.every(l => curLinkKeys.has(linkKey(l)));
        const topologyChanged = !sameNodeCount || !nodesAllKnown || !sameLinkCount || !linksAllKnown;

        if (topologyChanged)
            this.instance.graphData({ nodes: nodes, links: links });
        else if (positionChanged)
            this.instance.d3ReheatSimulation();

        //Repaint restyled nodes and rebuild the label / image / model overlays.
        this.instance.nodeColor(this.instance.nodeColor());
        this.instance.nodeVal(this.instance.nodeVal());
        this.setupLabels(this.container);
        this.setupCustomNodes();
    },

    centerGraph: function () {
        if (this.instance) {
            this.instance.zoomToFit(400, 50);
        }
    },

    //Dollies the 3D camera toward/away from the center. factor < 1 zooms in, > 1 zooms out.
    zoom: function (factor) {
        if (this.instance) {
            const p = this.instance.cameraPosition();
            this.instance.cameraPosition({ x: p.x * factor, y: p.y * factor, z: p.z * factor }, undefined, 200);
        }
    },

    //Returns an image data URL of the current 3D view, or null. Renders first so the
    //WebGL buffer is captured. format 'jpeg' emits image/jpeg, otherwise image/png.
    //Current node positions in the 3D view as JSON [{id, x, y, z}] — used to persist a hand-arranged layout.
    getPositions: function () {
        if (!this.instance)
            return "[]";

        const nodes = this.instance.graphData().nodes || [];
        const list = nodes.map(n => ({ id: n.id, x: Math.round(n.x || 0), y: Math.round(n.y || 0), z: Math.round(n.z || 0) }));

        return JSON.stringify(list);
    },

    image: function (format) {
        if (this.instance) {
            const renderer = this.instance.renderer();
            renderer.render(this.instance.scene(), this.instance.camera());

            let mime = 'image/png';
            if (format === 'jpeg')
                mime = 'image/jpeg';

            return renderer.domElement.toDataURL(mime);
        }

        return null;
    },

    //Exports the entire graph in the 3D viewer as a 3D-model string in the given
    //format ('obj' | 'ply' | 'stl'), or null. Built from graphData() (not the rendered
    //scene), so every node and link is included regardless of selection. A node with a
    //linked .obj model (gdbvModel) exports that model in place of its sphere; otherwise
    //a node is a round UV sphere. Each link becomes a thin tube.
    export3d: async function (format) {
        if (!this.instance)
            return null;

        const data = this.instance.graphData();
        if (!data || !data.nodes || data.nodes.length === 0)
            return null;

        let r = 4;
        if (typeof this.instance.nodeRelSize === 'function')
            r = this.instance.nodeRelSize();

        const mesh = { verts: [], tris: [] };
        const nodePos = {};
        const modelCache = {};

        //Each node becomes its linked .obj model (scaled to node size) or a sphere.
        for (const node of data.nodes) {
            const x = node.x || 0;
            const y = node.y || 0;
            const z = node.z || 0;
            nodePos[node.id] = { x: x, y: y, z: z };

            //data.model carries the per-node gdbvModel (only when the node is set to show it) or the
            //label model — the converter gates it, so an unshown model simply falls through to a sphere.
            let modelUrl = node.model || '';

            let model = null;
            if (modelUrl)
                model = await this.loadObjModel(modelUrl, modelCache);

            if (model)
                this.addModel(mesh, model, x, y, z, r);
            else
                this.addSphere(mesh, x, y, z, r);
        }

        //Each link becomes a thin tube between its endpoints.
        const tubeRadius = r * 0.15;
        const links = data.links || [];
        links.forEach(link => {
            let srcId;
            if (typeof link.source === 'object')
                srcId = link.source.id;
            else
                srcId = link.source;

            let tgtId;
            if (typeof link.target === 'object')
                tgtId = link.target.id;
            else
                tgtId = link.target;

            const a = nodePos[srcId];
            const b = nodePos[tgtId];
            if (a && b)
                this.addTube(mesh, a, b, tubeRadius);
        });

        if (mesh.verts.length === 0)
            return null;

        let fmt = 'obj';
        if (format)
            fmt = format.toLowerCase();

        if (fmt === 'ply')
            return this.meshToPly(mesh);
        else if (fmt === 'stl')
            return this.meshToStl(mesh);
        else if (fmt === 'gltf')
            return graphGeometry.meshToGltf(mesh);
        else
            return this.meshToObj(mesh);
    },

    //Geometry/serialization helpers live in graphGeometry.js (unit-tested under Node).
    addSphere: function (mesh, cx, cy, cz, r) {
        graphGeometry.addSphere(mesh, cx, cy, cz, r);
    },

    addTube: function (mesh, a, b, radius) {
        graphGeometry.addTube(mesh, a, b, radius);
    },

    meshToObj: function (mesh) {
        return graphGeometry.meshToObj(mesh);
    },

    meshToPly: function (mesh) {
        return graphGeometry.meshToPly(mesh);
    },

    meshToStl: function (mesh) {
        return graphGeometry.meshToStl(mesh);
    },

    normalizeModelUrl: function (url) {
        return graphGeometry.normalizeModelUrl(url);
    },

    //Fetches and parses an external OBJ model, caching by URL. Returns the parsed
    //model ({ verts, faces }) or null on any failure (network, CORS, empty, parse).
    loadObjModel: async function (url, cache) {
        if (cache[url] !== undefined)
            return cache[url];

        let parsed = null;
        const fetchUrl = this.normalizeModelUrl(url);

        try
        {
            const resp = await fetch(fetchUrl);
            if (resp.ok) {
                const buf = await resp.arrayBuffer();
                parsed = await graphGeometry.parseObjData(buf);
            }
            else
                console.warn('Model fetch failed (' + resp.status + '): ' + fetchUrl);
        }
        catch (e)
        {
            console.warn('Model fetch blocked (CORS / mixed-content / network): ' + fetchUrl, e);
        }

        cache[url] = parsed;
        return parsed;
    },

    parseObj: function (text) {
        return graphGeometry.parseObj(text);
    },

    addModel: function (mesh, model, cx, cy, cz, r) {
        graphGeometry.addModel(mesh, model, cx, cy, cz, r);
    },

    //Replaces node spheres in the LIVE 3D view with either a linked .obj model or a primitive shape
    //(cube / stretched cube / pyramid / hexagonal prism), keeping the default sphere for circle nodes.
    //A model wins over the shape; the shape defaults to rectangle (a stretched cube), matching the 2D
    //default. The bundled three.js isn't exposed, so its constructors are captured from a default node
    //mesh; on failure the nodes simply stay spheres.
    setupCustomNodes: async function () {
        if (!this.instance)
            return;

        const data = this.instance.graphData();
        if (!data || !data.nodes)
            return;

        const custom = data.nodes.filter(n => this.nodeCustomKind(n) !== 'sphere');
        if (custom.length === 0)
            return;

        //Wait for the default node meshes to exist so three.js can be captured.
        let three = null;
        for (let attempt = 0; attempt < 12 && !three; attempt++) {
            await new Promise(resolve => requestAnimationFrame(resolve));
            three = this.captureThree();
        }

        if (!three)
            return;

        let baseR = 4;
        if (typeof this.instance.nodeRelSize === 'function')
            baseR = this.instance.nodeRelSize();

        const cache = {};
        const objById = {};

        for (const node of custom) {
            const kind = this.nodeCustomKind(node);
            const color = node.bgColor || node.color;
            let mesh = null;

            if (kind === 'model') {
                const url = node.model;
                const parsed = await this.loadObjModel(url, cache);
                if (parsed)
                    mesh = this.buildModelMesh(parsed, three, baseR, color);
            }
            else {
                mesh = this.buildShapeMesh(kind, three, this.nodeRadius(node, baseR), color);
            }

            if (mesh)
                objById[node.id] = mesh;
        }

        if (Object.keys(objById).length === 0)
            return;

        this.modelObjById = objById;

        const self = this;
        this.instance.nodeThreeObjectExtend(false);
        this.instance.nodeThreeObject(node => {
            return self.modelObjById[node.id];//undefined => library keeps the default sphere
        });
    },

    //How a node is drawn in 3D: its linked .obj 'model', the default 'sphere', or a primitive solid
    //(cube / box / pyramid / prism). The 3D shape (node.shape3d) is independent of the 2D shape and only
    //applies when no model URL is set; unset defaults to 'sphere'.
    nodeCustomKind: function (node) {
        if (node.model)
            return 'model';

        const shape = node.shape3d || 'sphere';
        if (shape === 'sphere')
            return 'sphere';

        return shape;//cube / box / pyramid / prism → built by shapePositions
    },

    //A node's render radius, mirroring the library's sphere sizing (cbrt of nodeVal * nodeRelSize).
    nodeRadius: function (node, baseR) {
        let val = 1;
        if (node.nodeSize)
            val = Math.pow(node.nodeSize / 40, 3);

        return Math.cbrt(val) * baseR;
    },

    //Builds a flat-shaded three.js Mesh for a primitive node shape, scaled to the node radius and
    //given its own colored (double-sided) material. Geometry is cached per shape. Returns null on failure.
    buildShapeMesh: function (shape, three, r, color) {
        try
        {
            const geom = this.shapeGeometry(shape, three);
            if (!geom)
                return null;

            let mat;
            if (color)
                mat = new three.Material({ color: color, side: 2 });
            else
                mat = new three.Material({ side: 2 });

            const mesh = new three.Mesh(geom, mat);
            mesh.scale.set(r, r, r);

            return mesh;
        }
        catch (e)
        {
            console.warn('Failed to build shape mesh:', e);
            return null;
        }
    },

    //Returns (and caches) a unit BufferGeometry for a node shape, flat-shaded (non-indexed triangle
    //soup so each face gets its own normal).
    shapeGeometry: function (shape, three) {
        this._shapeGeom = this._shapeGeom || {};
        if (this._shapeGeom[shape])
            return this._shapeGeom[shape];

        const positions = this.shapePositions(shape);
        if (!positions)
            return null;

        const geom = new three.BufferGeometry();
        geom.setAttribute('position', new three.BufferAttribute(new Float32Array(positions), 3));
        geom.computeVertexNormals();

        this._shapeGeom[shape] = geom;

        return geom;
    },

    //Flat triangle-soup vertices (a flat [x,y,z, ...] list, 3 per triangle) for a unit 3D solid,
    //centered at the origin with a half-extent of ~1: cube = box, box = x-stretched box, pyramid =
    //square-based pyramid, prism = hexagonal prism, oval = x-stretched sphere (ellipsoid). Returns null
    //for sphere/unknown (the default sphere is kept). The material is double-sided, so winding order
    //doesn't matter.
    shapePositions: function (shape) {
        const tris = [];
        const push = (a, b, c) => tris.push(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
        const quad = (a, b, c, d) => { push(a, b, c); push(a, c, d); };

        if (shape === 'cube' || shape === 'box') {
            let hx = 1, hy = 1, hz = 1;
            if (shape === 'box') {
                hx = 1.6;
                hy = 0.8;
                hz = 0.8;
            }

            const c = [
                [-hx, -hy, -hz], [hx, -hy, -hz], [hx, hy, -hz], [-hx, hy, -hz],
                [-hx, -hy, hz], [hx, -hy, hz], [hx, hy, hz], [-hx, hy, hz]
            ];
            quad(c[4], c[5], c[6], c[7]);
            quad(c[1], c[0], c[3], c[2]);
            quad(c[5], c[1], c[2], c[6]);
            quad(c[0], c[4], c[7], c[3]);
            quad(c[3], c[2], c[6], c[7]);
            quad(c[4], c[0], c[1], c[5]);
        }
        else if (shape === 'pyramid') {
            //A taller, narrower square-based pyramid so it reads as a pyramid, not a squat tent.
            const b0 = [-0.9, -1.2, -0.9], b1 = [0.9, -1.2, -0.9], b2 = [0.9, -1.2, 0.9], b3 = [-0.9, -1.2, 0.9];
            const apex = [0, 1.5, 0];
            quad(b3, b2, b1, b0);
            push(b0, b1, apex);
            push(b1, b2, apex);
            push(b2, b3, apex);
            push(b3, b0, apex);
        }
        else if (shape === 'prism') {
            const d = 0.5;
            const ky = 1.35;//stretch the hexagon vertically so the prism is taller than wide
            const f = [], bk = [];
            for (let i = 0; i < 6; i++) {
                const ang = Math.PI / 3 * i + Math.PI / 6;
                const x = Math.cos(ang), y = Math.sin(ang) * ky;
                f.push([x, y, d]);
                bk.push([x, y, -d]);
            }

            for (let i = 1; i + 1 < 6; i++) {
                push(f[0], f[i], f[i + 1]);
                push(bk[0], bk[i + 1], bk[i]);
            }

            for (let i = 0; i < 6; i++) {
                const j = (i + 1) % 6;
                quad(f[i], f[j], bk[j], bk[i]);
            }
        }
        else if (shape === 'oval') {
            //A stretched sphere (ellipsoid) — an "oval" solid, wider along x than the sphere.
            const rx = 1.5, ry = 0.85, rz = 0.85;
            const slices = 20, stacks = 12;
            const at = (i, j) => {
                const theta = Math.PI * i / stacks;
                const phi = 2 * Math.PI * j / slices;
                const st = Math.sin(theta);
                return [rx * st * Math.cos(phi), ry * Math.cos(theta), rz * st * Math.sin(phi)];
            };

            for (let i = 0; i < stacks; i++) {
                for (let j = 0; j < slices; j++) {
                    quad(at(i, j), at(i + 1, j), at(i + 1, j + 1), at(i, j + 1));
                }
            }
        }
        else {
            return null;
        }

        return tris;
    },

    //Captures the bundled three.js constructors from a rendered default node mesh.
    captureThree: function () {
        if (this._three)
            return this._three;

        if (!this.instance)
            return null;

        const data = this.instance.graphData();
        let mesh = null;
        if (data && data.nodes) {
            for (const n of data.nodes) {
                if (!n.__threeObj)
                    continue;

                mesh = this.findMesh(n.__threeObj);
                if (mesh)
                    break;
            }
        }

        if (!mesh || !mesh.geometry || !mesh.material)
            return null;

        const geom = mesh.geometry;
        const posAttr = geom.attributes.position;
        if (!posAttr)
            return null;

        this._three = {
            Mesh: mesh.constructor,
            Material: mesh.material.constructor,
            BufferGeometry: Object.getPrototypeOf(Object.getPrototypeOf(geom)).constructor,
            BufferAttribute: posAttr.constructor
        };

        return this._three;
    },

    //Depth-first search for the first Mesh within a node's three.js object.
    findMesh: function (obj) {
        if (obj.isMesh && obj.geometry)
            return obj;

        if (obj.children) {
            for (const c of obj.children) {
                const found = this.findMesh(c);
                if (found)
                    return found;
            }
        }

        return null;
    },

    //Builds a three.js Mesh from a parsed OBJ, centered at the origin and scaled to
    //roughly the node diameter (the library then positions it at the node).
    buildModelMesh: function (model, three, r, color) {
        try
        {
            let minX = Infinity, minY = Infinity, minZ = Infinity;
            let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
            model.verts.forEach(v => {
                minX = Math.min(minX, v[0]);
                minY = Math.min(minY, v[1]);
                minZ = Math.min(minZ, v[2]);
                maxX = Math.max(maxX, v[0]);
                maxY = Math.max(maxY, v[1]);
                maxZ = Math.max(maxZ, v[2]);
            });

            const mcx = (minX + maxX) / 2;
            const mcy = (minY + maxY) / 2;
            const mcz = (minZ + maxZ) / 2;

            let maxDim = Math.max(maxX - minX, maxY - minY, maxZ - minZ);
            if (maxDim <= 0)
                maxDim = 1;

            const scale = (2 * r) / maxDim;

            const positions = new Float32Array(model.verts.length * 3);
            for (let i = 0; i < model.verts.length; i++) {
                const v = model.verts[i];
                positions[i * 3] = (v[0] - mcx) * scale;
                positions[i * 3 + 1] = (v[1] - mcy) * scale;
                positions[i * 3 + 2] = (v[2] - mcz) * scale;
            }

            //Fan-triangulate faces (1-based within the model) into a 0-based index list.
            const indices = [];
            model.faces.forEach(face => {
                for (let i = 1; i + 1 < face.length; i++) {
                    indices.push(face[0] - 1, face[i] - 1, face[i + 1] - 1);
                }
            });

            const geom = new three.BufferGeometry();
            geom.setAttribute('position', new three.BufferAttribute(positions, 3));
            geom.setIndex(indices);
            geom.computeVertexNormals();

            let mat;
            if (color)
                mat = new three.Material({ color: color });
            else
                mat = new three.Material();

            return new three.Mesh(geom, mat);
        }
        catch (e)
        {
            console.warn('Failed to build model mesh:', e);
            return null;
        }
    },

    //Builds the always-on HTML label overlay for the current graph and starts the
    //per-frame loop that pins each label over its node/link. The nodeLabel/linkLabel
    //hover tooltips stay active and serve the "labels off" state.
    setupLabels: function (container) {
        this.teardownLabels();

        if (!container)
            return;

        if (getComputedStyle(container).position === 'static')
            container.style.position = 'relative';

        const layer = document.createElement('div');
        layer.style.position = 'absolute';
        layer.style.top = '0';
        layer.style.left = '0';
        layer.style.width = '100%';
        layer.style.height = '100%';
        layer.style.overflow = 'hidden';
        layer.style.pointerEvents = 'none';
        container.appendChild(layer);
        this.labelLayer = layer;

        const data = this.instance.graphData();

        this.graphExtent = this.computeGraphExtent(data.nodes);

        this.nodeLabels = [];
        data.nodes.forEach(node => {
            const text = node.label || node.id;
            if (!text)
                return;

            const el = this.makeLabelEl(layer, text, false);
            this.nodeLabels.push({ el: el, node: node });
        });

        this.linkLabels = [];
        (data.links || []).forEach(link => {
            if (!link.label)
                return;

            const el = this.makeLabelEl(layer, link.label, true);
            this.linkLabels.push({ el: el, link: link });
        });

        this.nodeImages = [];
        data.nodes.forEach(node => {
            //data.image is gated by the node's "show" state in the converter — unshown images are absent.
            const url = node.image;
            if (!url)
                return;

            const img = this.makeImageEl(layer, url);
            this.nodeImages.push({ el: img, node: node });
        });

        //Shown-properties (gdbvShow) floating text pinned just below each node that carries a list.
        this.nodeShowLabels = [];
        data.nodes.forEach(node => {
            const lines = node.showProps;
            if (!lines || !lines.length)
                return;

            const el = this.makeShowEl(layer, lines);
            this.nodeShowLabels.push({ el: el, node: node });
        });

        this.setupAxisIndicator(layer);
        this.applyLabelVisibility();
        this.startLabelLoop();
    },

    //Builds the corner X/Y/Z orientation gizmo (an SVG overlay) in the bottom-left.
    setupAxisIndicator: function (layer) {
        const NS = 'http://www.w3.org/2000/svg';
        const size = 84;

        const svg = document.createElementNS(NS, 'svg');
        svg.setAttribute('width', size);
        svg.setAttribute('height', size);
        svg.style.position = 'absolute';
        svg.style.left = '8px';
        svg.style.bottom = '8px';
        svg.style.overflow = 'visible';
        svg.style.pointerEvents = 'none';

        const cx = size / 2;
        const cy = size / 2;

        const axes = [
            { dir: [1, 0, 0], color: '#e74c3c', label: 'X' },
            { dir: [0, 1, 0], color: '#2ecc71', label: 'Y' },
            { dir: [0, 0, 1], color: '#4aa3ff', label: 'Z' }
        ];

        this.axisItems = [];
        axes.forEach(a => {
            const line = document.createElementNS(NS, 'line');
            line.setAttribute('x1', cx);
            line.setAttribute('y1', cy);
            line.setAttribute('stroke', a.color);
            line.setAttribute('stroke-width', '2');
            svg.appendChild(line);

            const text = document.createElementNS(NS, 'text');
            text.setAttribute('fill', a.color);
            text.setAttribute('font-size', '11');
            text.setAttribute('font-weight', 'bold');
            text.setAttribute('text-anchor', 'middle');
            text.setAttribute('dominant-baseline', 'middle');
            text.textContent = a.label;
            svg.appendChild(text);

            this.axisItems.push({ line: line, text: text, dir: a.dir });
        });

        layer.appendChild(svg);
        this.axisSvg = svg;
        this.axisCenter = { x: cx, y: cy, r: cx - 12 };
    },

    //Projects each world axis onto screen space using the camera orientation so the
    //gizmo spins as the view orbits. Foreshortened axes naturally draw shorter.
    updateAxisIndicator: function () {
        if (!this.axisSvg || !this.instance)
            return;

        const cam = this.instance.camera();
        if (!cam)
            return;

        const e = cam.matrixWorld.elements;
        const c = this.axisCenter;

        this.axisItems.forEach(item => {
            const d = item.dir;
            const sx = e[0] * d[0] + e[1] * d[1] + e[2] * d[2];
            const sy = e[4] * d[0] + e[5] * d[1] + e[6] * d[2];

            item.line.setAttribute('x2', c.x + sx * c.r);
            item.line.setAttribute('y2', c.y - sy * c.r);
            item.text.setAttribute('x', c.x + sx * (c.r + 7));
            item.text.setAttribute('y', c.y - sy * (c.r + 7));
        });
    },

    //Creates one positioned <img> overlay pinned over a node that has a linked image.
    makeImageEl: function (layer, url) {
        const img = document.createElement('img');
        img.src = url;
        img.style.position = 'absolute';
        img.style.transform = 'translate(-50%, -50%)';
        img.style.width = '40px';
        img.style.height = '40px';
        img.style.objectFit = 'cover';
        img.style.borderRadius = '50%';
        img.style.border = '1px solid rgba(255, 255, 255, 0.7)';
        img.style.pointerEvents = 'none';
        img.style.userSelect = 'none';
        layer.appendChild(img);
        return img;
    },

    //Creates one positioned label element. isLink shrinks/dims edge labels.
    makeLabelEl: function (layer, text, isLink) {
        const el = document.createElement('div');
        el.textContent = text;
        el.style.position = 'absolute';
        el.style.transform = 'translate(-50%, -50%)';
        el.style.whiteSpace = 'nowrap';
        el.style.pointerEvents = 'none';
        el.style.userSelect = 'none';
        el.style.padding = '1px 4px';
        el.style.borderRadius = '3px';
        el.style.color = '#fff';
        el.style.background = 'rgba(0, 0, 0, 0.55)';

        if (isLink) {
            el.style.fontSize = '9px';
            el.style.opacity = '0.85';
        } else {
            el.style.fontSize = '11px';
        }

        layer.appendChild(el);
        return el;
    },

    //Creates the "shown properties" overlay pinned below a node — plain floating text (one line per
    //property), shadowed so it stays legible over any background.
    makeShowEl: function (layer, lines) {
        const el = document.createElement('div');
        el.textContent = lines.join('\n');
        el.style.position = 'absolute';
        //Anchored at its top-center so it scales toward the node's bottom as the camera moves.
        el.style.transformOrigin = 'top center';
        el.style.whiteSpace = 'pre';
        el.style.textAlign = 'center';
        el.style.pointerEvents = 'none';
        el.style.userSelect = 'none';
        el.style.fontSize = '8px';
        el.style.lineHeight = '1.25';
        el.style.color = '#fff';
        el.style.textShadow = '0 1px 2px rgba(0,0,0,0.9), 0 -1px 2px rgba(0,0,0,0.9)';
        layer.appendChild(el);
        return el;
    },

    //Sets the label draw distance from a 0–100 percentage. 100 shows every label (culling off); lower
    //values fade out vertex and edge labels farther than that fraction of the graph's reach from the
    //camera. Images, the shown-properties overlay and the axis gizmo are never affected.
    setLabelDrawDistance: function (percent) {
        this.labelDrawPercent = Math.max(0, Math.min(100, +percent || 0));

        //The per-frame loop only writes opacity while culling is active, so restore full opacity here
        //when it is switched back off.
        if (this.labelDrawPercent >= 100)
            this.resetLabelOpacity();
    },

    //The graph's radius: the farthest node from the centroid, floored so a tiny or still-settling graph
    //keeps the slider usable.
    computeGraphExtent: function (nodes) {
        if (!nodes || !nodes.length)
            return 200;

        let cx = 0, cy = 0, cz = 0;
        nodes.forEach(n => { cx += n.x || 0; cy += n.y || 0; cz += n.z || 0; });
        cx /= nodes.length;
        cy /= nodes.length;
        cz /= nodes.length;

        let max = 0;
        nodes.forEach(n => {
            const dx = (n.x || 0) - cx;
            const dy = (n.y || 0) - cy;
            const dz = (n.z || 0) - cz;
            const d = Math.sqrt(dx * dx + dy * dy + dz * dz);
            if (d > max)
                max = d;
        });

        return Math.max(50, max);
    },

    //Restores every label to its full opacity — used when culling is switched off, since the per-frame
    //loop leaves opacity alone in that state.
    resetLabelOpacity: function () {
        (this.nodeLabels || []).forEach(item => { item.el.style.opacity = '1'; });
        (this.linkLabels || []).forEach(item => { item.el.style.opacity = '0.85'; });
    },

    //Distance from the camera position to a world point.
    dist3: function (camPos, x, y, z) {
        const dx = camPos.x - x;
        const dy = camPos.y - y;
        const dz = camPos.z - z;

        return Math.sqrt(dx * dx + dy * dy + dz * dz);
    },

    //A label's opacity for a given camera distance: full up to a fade band before the threshold, then
    //ramping to zero at it. base is the label's normal opacity (edge labels sit dimmer than vertices).
    labelOpacityForDistance: function (dist, threshold, base) {
        const fadeStart = threshold * 0.8;

        if (dist <= fadeStart)
            return base;

        if (dist >= threshold)
            return 0;

        return base * (1 - (dist - fadeStart) / (threshold - fadeStart));
    },

    startLabelLoop: function () {
        const self = this;
        const tick = () => {
            self.refreshExtentIfCulling();
            self.updateLabelPositions();
            self.updateNodeCulling();
            self.labelFrameId = requestAnimationFrame(tick);
        };

        this.labelFrameId = requestAnimationFrame(tick);
    },

    //Refreshes the shared graph extent on a throttle, but only while some draw-distance culling is active.
    refreshExtentIfCulling: function () {
        if (!this.instance)
            return;

        if (this.labelDrawPercent >= 100 && this.nodeDrawPercent >= 100)
            return;

        if ((this._extentTick++ % 30) === 0)
            this.graphExtent = this.computeGraphExtent(this.instance.graphData().nodes);
    },

    //Sets the node draw distance from a 0–100 percentage. 100 shows every node; lower hides nodes farther
    //from the camera, along with the edges touching a hidden node, revealing them again as you move in.
    setNodeDrawDistance: function (percent) {
        this.nodeDrawPercent = Math.max(0, Math.min(100, +percent || 0));

        //The per-frame loop only writes .visible while culling is active, so restore full visibility
        //(respecting the label filter) when it is switched back off.
        if (this.nodeDrawPercent >= 100)
            this.restoreNodeVisibility();
    },

    //Whether a node's type label is hidden by the label filter — distance culling must not un-hide it.
    isLabelHidden: function (node) {
        return this.hiddenLabelSet != null && this.hiddenLabelSet.has(node.glabel);
    },

    //Hides node geometry — and the edges touching a hidden node — beyond the node draw distance, updated
    //every frame so nodes reappear as the camera moves closer. The label filter still wins.
    updateNodeCulling: function () {
        if (!this.instance || this.nodeDrawPercent >= 100)
            return;

        const cam = this.instance.camera();
        if (!cam)
            return;

        const self = this;
        const cp = cam.position;
        const threshold = (this.nodeDrawPercent / 100) * this.drawDistanceScale * this.graphExtent;
        const data = this.instance.graphData();

        data.nodes.forEach(n => {
            const obj = n.__threeObj;
            if (!obj)
                return;

            const within = self.dist3(cp, n.x || 0, n.y || 0, n.z || 0) <= threshold;
            obj.visible = within && !self.isLabelHidden(n);
        });

        (data.links || []).forEach(l => {
            const s = l.source;
            const t = l.target;
            if (typeof s !== 'object' || typeof t !== 'object')
                return;

            //An edge shows only when both its nodes do — so it drops with either end that the distance
            //(or the label filter) hides.
            const within = self.dist3(cp, s.x || 0, s.y || 0, s.z || 0) <= threshold
                && self.dist3(cp, t.x || 0, t.y || 0, t.z || 0) <= threshold;
            const vis = within && !self.isLabelHidden(s) && !self.isLabelHidden(t);

            if (l.__lineObj)
                l.__lineObj.visible = vis;
            if (l.__arrowObj)
                l.__arrowObj.visible = vis;
        });
    },

    //Restores every node/link to the visibility the label filter dictates, clearing any distance culling.
    restoreNodeVisibility: function () {
        if (!this.instance)
            return;

        const self = this;
        const data = this.instance.graphData();

        data.nodes.forEach(n => {
            if (n.__threeObj)
                n.__threeObj.visible = !self.isLabelHidden(n);
        });

        (data.links || []).forEach(l => {
            const s = l.source;
            const t = l.target;
            const hidden = (typeof s === 'object' && self.isLabelHidden(s)) || (typeof t === 'object' && self.isLabelHidden(t));

            if (l.__lineObj)
                l.__lineObj.visible = !hidden;
            if (l.__arrowObj)
                l.__arrowObj.visible = !hidden;
        });
    },

    //Projects each visible node/link position to screen space and repositions its label. When a finite
    //draw distance is set, also fades each vertex/edge label by its distance from the camera.
    updateLabelPositions: function () {
        if (!this.instance)
            return;

        const self = this;

        //Draw-distance culling setup. The world-unit threshold is derived from graphExtent, refreshed on
        //a throttle in the loop below because the graph keeps expanding while the force layout settles.
        const cull = this.labelDrawPercent < 100;
        let threshold = Infinity;
        let camPos = null;

        if (cull) {
            threshold = (this.labelDrawPercent / 100) * this.drawDistanceScale * this.graphExtent;

            const cam = this.instance.camera();
            if (cam)
                camPos = cam.position;
        }

        this.nodeLabels.forEach(item => {
            if (item.el.style.display === 'none')
                return;

            const nx = item.node.x || 0;
            const ny = item.node.y || 0;
            const nz = item.node.z || 0;

            const c = self.instance.graph2ScreenCoords(nx, ny, nz);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';

            if (cull && camPos)
                item.el.style.opacity = self.labelOpacityForDistance(self.dist3(camPos, nx, ny, nz), threshold, 1);
        });

        this.linkLabels.forEach(item => {
            if (item.el.style.display === 'none')
                return;

            const s = item.link.source;
            const t = item.link.target;
            if (typeof s !== 'object' || typeof t !== 'object')
                return;

            const mx = ((s.x || 0) + (t.x || 0)) / 2;
            const my = ((s.y || 0) + (t.y || 0)) / 2;
            const mz = ((s.z || 0) + (t.z || 0)) / 2;

            const c = self.instance.graph2ScreenCoords(mx, my, mz);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';

            if (cull && camPos)
                item.el.style.opacity = self.labelOpacityForDistance(self.dist3(camPos, mx, my, mz), threshold, 0.85);
        });

        (this.nodeImages || []).forEach(item => {
            const c = self.instance.graph2ScreenCoords(item.node.x || 0, item.node.y || 0, item.node.z || 0);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';
        });

        let baseR = 4;
        if (typeof self.instance.nodeRelSize === 'function')
            baseR = self.instance.nodeRelSize();

        (this.nodeShowLabels || []).forEach(item => {
            if (item.el.style.display === 'none')
                return;

            const n = item.node;
            const c = self.instance.graph2ScreenCoords(n.x || 0, n.y || 0, n.z || 0);

            //Project a point one node-radius away to measure the node's apparent (screen) size, so the
            //text scales with the camera and sits just below the node instead of a fixed pixel offset.
            const r = self.nodeRadius(n, baseR);
            const edge = self.instance.graph2ScreenCoords((n.x || 0) + r, n.y || 0, n.z || 0);
            const apparent = Math.hypot(edge.x - c.x, edge.y - c.y);
            const scale = Math.max(0.25, Math.min(3, apparent / 8));

            item.el.style.left = c.x + 'px';
            item.el.style.top = (c.y + apparent + 14) + 'px';
            item.el.style.transform = 'translate(-50%, 0) scale(' + scale + ')';
        });

        this.updateAxisIndicator();
    },

    //Shows/hides the always-on labels. When a kind is off, only its hover tooltip remains.
    setLabelVisibility: function (showNodes, showLinks, showProps) {
        this.showNodeLabels = showNodes;
        this.showLinkLabels = showLinks;
        this.showNodeProps = showProps;
        this.applyLabelVisibility();
    },

    applyLabelVisibility: function () {
        let nodeDisplay;
        if (this.showNodeLabels)
            nodeDisplay = 'block';
        else
            nodeDisplay = 'none';

        let linkDisplay;
        if (this.showLinkLabels)
            linkDisplay = 'block';
        else
            linkDisplay = 'none';

        //Shown-properties overlay: hidden only when explicitly turned off (default on).
        let propsDisplay;
        if (this.showNodeProps === false)
            propsDisplay = 'none';
        else
            propsDisplay = 'block';

        (this.nodeLabels || []).forEach(item => {
            item.el.style.display = nodeDisplay;
        });

        (this.linkLabels || []).forEach(item => {
            item.el.style.display = linkDisplay;
        });

        (this.nodeShowLabels || []).forEach(item => {
            item.el.style.display = propsDisplay;
        });
    },

    teardownLabels: function () {
        if (this.labelFrameId) {
            cancelAnimationFrame(this.labelFrameId);
            this.labelFrameId = null;
        }

        if (this.labelLayer && this.labelLayer.parentNode)
            this.labelLayer.parentNode.removeChild(this.labelLayer);

        this.labelLayer = null;
        this.nodeLabels = [];
        this.linkLabels = [];
        this.nodeImages = [];
        this.nodeShowLabels = [];
        this.axisSvg = null;
        this.axisItems = [];
    },

    //Centers the camera on the first node matching the term (label/id/properties). Returns count.
    search: function (term) {
        if (!this.instance)
            return 0;

        const q = (term || '').trim().toLowerCase();
        if (q.length === 0)
            return 0;

        const data = this.instance.graphData();
        const matches = data.nodes.filter(n => {
            if ((n.label || '').toLowerCase().includes(q))
                return true;

            if (String(n.id || '').toLowerCase().includes(q))
                return true;

            const props = n.properties || {};
            for (const k in props) {
                if (String(props[k]).toLowerCase().includes(q))
                    return true;
            }

            return false;
        });

        if (matches.length > 0) {
            const n = matches[0];
            const x = n.x || 0, y = n.y || 0, z = n.z || 0;
            const nd = Math.hypot(x, y, z);

            let camPos;
            if (nd > 1) {
                const ratio = 1 + 120 / nd;
                camPos = { x: x * ratio, y: y * ratio, z: z * ratio };
            } else {
                camPos = { x: x, y: y, z: z + 120 };
            }

            this.instance.cameraPosition(camPos, { x: x, y: y, z: z }, 800);
        }

        return matches.length;
    },

    //Hides nodes whose type label (glabel) is in the hidden list, and any incident links.
    setLabelFilter: function (hiddenJson) {
        if (!this.instance)
            return;

        const hidden = JSON.parse(hiddenJson);
        //Kept so node distance culling can respect the same filter (see isLabelHidden).
        this.hiddenLabelSet = new Set(hidden);
        const isHidden = function (glabel) {
            return hidden.indexOf(glabel) !== -1;
        };

        this.instance.nodeVisibility(n => !isHidden(n.glabel));
        this.instance.linkVisibility(l => {
            const s = l.source;
            const t = l.target;

            if (s && typeof s === 'object' && isHidden(s.glabel))
                return false;

            if (t && typeof t === 'object' && isHidden(t.glabel))
                return false;

            return true;
        });
    },

    //Applies per-label color/size live by writing each node's bgColor/nodeSize and re-poking the
    //nodeColor/nodeVal accessors so the library repaints. stylesJson maps type label -> { color, size }.
    //Display-property and icon need a full re-render instead (labels/overlays are rebuilt on draw).
    applyLabelStyles: function (stylesJson) {
        if (!this.instance)
            return;

        const styles = JSON.parse(stylesJson);
        const data = this.instance.graphData();
        data.nodes.forEach(node => {
            const s = styles[node.glabel];

            if (s && s.color)
                node.bgColor = s.color;
            else
                delete node.bgColor;

            if (s && s.size > 0)
                node.nodeSize = s.size;
            else
                delete node.nodeSize;
        });

        this.instance.nodeColor(this.instance.nodeColor());
        this.instance.nodeVal(this.instance.nodeVal());

        //Custom shape meshes (cube / pyramid / …) bake in their color and size when built, and re-poking
        //nodeVal re-pulls each node's three-object — so rebuild them here or a color/size change would
        //drop the shape back to the default sphere (and never recolor the shape).
        this.setupCustomNodes();
    },

    destroyGraph: function () {
        this.unobserveResize();
        this.teardownLabels();

        if (this.instance) {
            this.instance._destructor && this.instance._destructor();
            this.instance = null;
        }
    }
};
