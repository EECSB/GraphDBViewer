let cy = null;
let currentLayout = null;
//Above this many elements (nodes + edges) the "big graph" render options are switched on: they hide edges
//and render a cached bitmap while panning/zooming and drop hi-DPI supersampling, trading a little fidelity
//during interaction for speed. Below it they stay off so small graphs render crisp. Tunable.
const CY_HEAVY_THRESHOLD = 500;
//Whether the live instance was built in heavy mode — a redraw that crosses the threshold must re-create
//the instance, since these are init-time render options that can't be toggled on a live graph.
let cyHeavyMode = false;
//Above this many elements the graph is drawn by Cytoscape's WebGL renderer where the browser gives WebGL 2
//(2026-10-21): the GPU draws in batches what the canvas draws a shape at a time, which is how gdotV shows tens of
//thousands of elements. Past the zoom its textures are made for, it hands back to the canvas renderer itself, so
//close up a graph reads as it always did. Tunable.
const CY_WEBGL_THRESHOLD = 2000;
//Whether the live instance draws with WebGL: fixed when it is made, as the heavy mode is.
let cyWebglMode = false;
//Whether the browser gives WebGL 2, asked once; null until then.
let cyWebgl2 = null;
let cyResizeObserver = null;
let cyResizeFrameId = null;
//Takes the middle-button pan's listeners back off its container and the window, while they are on (attachMiddleDragPan).
let middlePanDetach = null;
let dotNetRef = null;
let lastTapNodeId = null;
let lastTapTime = 0;
let gridEnabled = false;
let gridStep = 50;//spacing (graph units) for grid-mode snapping and the grid overlay: Grid size, setCytoscapeGridSize

//Per-node HTML overlay: a small +/- toggle on each node with properties, plus a lighter-colored box
//pinned below the node listing either its "show"-marked properties (data.showProps) or — when the node
//is temporarily expanded via its toggle — ALL of its properties. Kept in sync with the canvas via cy's
//'render' event. Three layers: leader lines BEHIND the graph canvas (so they don't draw over the nodes),
//boxes on top, and the clickable toggles on top.
let showLineLayer = null;
let showBoxLayer = null;
let showButtonLayer = null;
let showItems = [];
let nodeButtons = [];

//View-only set of node ids whose full property list is temporarily expanded on the 2D canvas. It's
//never persisted and never staged as a query — it only changes what this overlay shows, right here.
let expandedNodes = new Set();

//Node ids whose expanded box also has the "Additional Viewer Properties" (gdbv*) section open. Same
//view-only, non-persisted nature as expandedNodes.
let viewerExpandedNodes = new Set();

function teardownShowLabels() {
    if (showLineLayer && showLineLayer.parentNode)
        showLineLayer.parentNode.removeChild(showLineLayer);

    if (showBoxLayer && showBoxLayer.parentNode)
        showBoxLayer.parentNode.removeChild(showBoxLayer);

    if (showButtonLayer && showButtonLayer.parentNode)
        showButtonLayer.parentNode.removeChild(showButtonLayer);

    showLineLayer = null;
    showBoxLayer = null;
    showButtonLayer = null;
    showItems = [];
    nodeButtons = [];
}

//Mixes a hex color toward white by amount (0..1) so the extension reads as a lighter shade of the node.
function lightenColor(hex, amount) {
    if (!hex || hex[0] !== '#')
        return hex || '#e9ecef';

    let h = hex.slice(1);
    if (h.length === 3)
        h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];

    const r = parseInt(h.slice(0, 2), 16);
    const g = parseInt(h.slice(2, 4), 16);
    const b = parseInt(h.slice(4, 6), 16);
    const mix = c => Math.round(c + (255 - c) * amount);

    return 'rgb(' + mix(r) + ', ' + mix(g) + ', ' + mix(b) + ')';
}

//A node's full property list as "key: value" lines, excluding the viewer-reserved gdbv* keys.
function nodeAllPropLines(n) {
    const props = n.data('properties') || {};
    const lines = [];

    Object.keys(props).forEach(function (k) {
        if (k.indexOf('gdbv') !== 0)
            lines.push(k + ': ' + props[k]);
    });

    return lines;
}

//A node's viewer-reserved (gdbv*) properties as "key: value" lines — revealed by the box's
//"Additional Viewer Properties" expander.
function nodeViewerPropLines(n) {
    const props = n.data('properties') || {};
    const lines = [];

    Object.keys(props).forEach(function (k) {
        if (k.indexOf('gdbv') === 0)
            lines.push(k + ': ' + props[k]);
    });

    return lines;
}

//The lines the node's box should show: ALL properties while temporarily expanded (via the +/- toggle),
//otherwise just the "show"-marked ones. The expand is view-only and overrides the show list here only.
function boxContentFor(n) {
    if (expandedNodes.has(String(n.id())))
        return nodeAllPropLines(n);

    return n.data('showProps') || [];
}

//Show details on canvas (2026-11-06, the user): a vertex whose label's style, or whose own gdbvDetails, asks for it is drawn
//as a small element card in place of its box (the same evening, the user: not under it, instead of it). The card has a head
//in the vertex's color with its name, label and id, and under it a row for each property, the key muted beside its value.
//The vertex itself stays, see-through and sized to the card (data detailsW and detailsH, the live stylesheet's
//node[detailsW]), so its edges end at the card's edge and a click, a drag or a right-click on the card is the vertex's.
//What the card says is graphGeometry's (detailsCardContent), which lays out the same card for what has no HTML to show
//it: an image export, and the 3D view.
function hasCanvasDetails(n) {
    return n.data('details') === true;
}

//What a carded element's card says (graphGeometry.detailsCardContent): a vertex's kind and id over its name, an edge's two
//ends over its label (Detailed view on edges, 2026-11-07), and under either a row for each property. An edge's id is left
//out: it is seldom anyone's to read, and often made up.
function detailsContentOf(el) {
    const geo = window.graphGeometry;

    if (el.isEdge()) {
        const content = geo.detailsCardContent({ kind: el.source().id() + ' → ' + el.target().id(), id: el.id(), label: el.data('label'), properties: el.data('properties') });
        content.id = null;

        return content;
    }

    return geo.detailsCardContent({ kind: el.data('glabel') || 'vertex', id: el.id(), label: el.data('label'), properties: el.data('properties') });
}

//A card's head color: a vertex's color, or an edge's.
function detailsColorOf(el) {
    if (el.isEdge())
        return el.data('edgeColor') || '#6c757d';

    return el.data('bgColor') || '#0d6efd';
}

function hasEdgeDetails(e) {
    return e.data('details') === true;
}

//Where a carded element stands, in the graph's own coordinates and on screen: a vertex's center, an edge's middle.
function modelPlaceOf(el) {
    if (el.isEdge())
        return el.midpoint();

    return el.position();
}

function renderedPlaceOf(el) {
    if (el.isEdge())
        return el.renderedMidpoint();

    return el.renderedPosition();
}

function makeDetailsCard(n) {
    const content = detailsContentOf(n);
    const card = document.createElement('div');
    card.className = 'gdbv-canvas-details';
    card.style.setProperty('--gdbv-details-color', detailsColorOf(n));

    if (n.isEdge())
        card.setAttribute('data-edge', String(n.id()));
    else
        card.setAttribute('data-node', String(n.id()));

    const head = document.createElement('div');
    head.className = 'gdbv-canvas-details-head';

    const meta = document.createElement('div');
    meta.className = 'gdbv-canvas-details-meta';

    const kindEl = document.createElement('span');
    kindEl.className = 'gdbv-canvas-details-kind';
    kindEl.textContent = content.kind;
    meta.appendChild(kindEl);

    if (content.id !== null) {
        const idEl = document.createElement('span');
        idEl.className = 'gdbv-canvas-details-id';
        idEl.textContent = '#' + content.id;
        meta.appendChild(idEl);
    }

    head.appendChild(meta);

    const titleEl = document.createElement('div');
    titleEl.className = 'gdbv-canvas-details-title';
    titleEl.textContent = content.title;
    head.appendChild(titleEl);
    card.appendChild(head);

    if (content.rows.length === 0)
        return card;

    const grid = document.createElement('div');
    grid.className = 'gdbv-canvas-details-rows';

    content.rows.forEach(function (row) {
        const key = document.createElement('span');
        key.className = 'gdbv-canvas-details-key';
        key.textContent = row[0];

        const value = document.createElement('span');
        value.className = 'gdbv-canvas-details-value';
        value.textContent = row[1];

        grid.appendChild(key);
        grid.appendChild(value);
    });

    card.appendChild(grid);

    if (content.more > 0) {
        const more = document.createElement('div');
        more.className = 'gdbv-canvas-details-more';
        more.textContent = '+' + content.more + ' more';
        card.appendChild(more);
    }

    return card;
}

//Sizes each carded vertex to its card, measured as laid out at a zoom of 1, and gives a vertex whose card has gone its own
//box back. On the map a vertex's sizes are read once for the marker scale, so a vertex whose size changed is read again.
function fitDetailsNodes(cards) {
    const changed = [];

    cy.batch(() => {
        cy.nodes('[detailsW]').forEach(n => {
            if (hasCanvasDetails(n))
                return;

            n.removeData('detailsW detailsH');
            changed.push(n);
        });

        cards.forEach(item => {
            const rect = item.el.getBoundingClientRect();
            const w = Math.ceil(rect.width);
            const h = Math.ceil(rect.height);

            //Not laid out (the 2D view hidden): the vertex keeps its box until the next build.
            if (w === 0 || h === 0)
                return;

            item.el.style.width = w + 'px';
            item.el.style.height = h + 'px';

            //An edge's card is drawn over it, the edge keeping its own shape.
            if (item.node.isEdge()) {
                item.node.scratch('gdbvCardW', w);
                return;
            }

            if (item.node.data('detailsW') === w && item.node.data('detailsH') === h)
                return;

            item.node.data({ detailsW: w, detailsH: h });
            changed.push(item.node);
        });
    });

    if (changed.length === 0 || !geoLayerOn)
        return;

    changed.forEach(n => n.removeScratch('gdbvMarker'));
    applyGeoMarkerScale();
}

//Gives a label's vertices on the 2D canvas their details cards, or takes them away, where they stand; a vertex with its own
//gdbvDetails keeps its own answer. False with no 2D graph, so the caller draws the graph instead.
function setCytoscapeLabelDetails(label, on) {
    if (!cy)
        return false;

    cy.nodes().filter(n => n.data('glabel') === label).forEach(n => {
        const own = (n.data('properties') || {}).gdbvDetails;

        if (own === 'true' || own === 'false')
            return;

        n.data('details', !!on);
    });

    setupCytoscapeShowLabels();

    return true;
}

//Builds the lighter-colored property box (one left-aligned, separator-lined row per property). When the
//node is fully expanded it also gets an "Additional Viewer Properties" expander that reveals its gdbv*
//keys in the same box.
function makeShowBox(color, lines, n) {
    const box = document.createElement('div');
    box.style.position = 'absolute';
    //Anchored at its top-center so it scales down toward the node's bottom edge as you zoom out.
    box.style.transformOrigin = 'top center';
    box.style.pointerEvents = 'none';
    box.style.userSelect = 'none';
    box.style.textAlign = 'left';
    box.style.fontSize = '8px';
    box.style.lineHeight = '1.3';
    box.style.padding = '1px 5px';
    box.style.borderRadius = '5px';
    box.style.color = '#212529';
    box.style.background = lightenColor(color, 0.55);

    lines.forEach(function (text, i) {
        const row = document.createElement('div');
        row.textContent = text;
        row.style.whiteSpace = 'nowrap';
        row.style.padding = '1px 0';
        if (i < lines.length - 1)
            row.style.borderBottom = '1px solid rgba(0, 0, 0, 0.18)';
        box.appendChild(row);
    });

    //"Additional Viewer Properties" expander — shown only while the node is fully expanded (via the +/-
    //toggle) and it actually has gdbv* keys. Clicking the row reveals them below, in the same box. The box
    //layer is pointer-events:none, so this row opts back in (pointerEvents:auto) to catch its own clicks.
    if (n && expandedNodes.has(String(n.id()))) {
        const viewerLines = nodeViewerPropLines(n);

        if (viewerLines.length > 0) {
            const id = String(n.id());
            const viewerOpen = viewerExpandedNodes.has(id);

            const toggleRow = document.createElement('div');
            if (viewerOpen)
                toggleRow.textContent = '− Additional Viewer Properties';
            else
                toggleRow.textContent = '+ Additional Viewer Properties';

            toggleRow.style.whiteSpace = 'nowrap';
            toggleRow.style.padding = '1px 0';
            toggleRow.style.marginTop = '1px';
            toggleRow.style.fontWeight = 'bold';
            toggleRow.style.cursor = 'pointer';
            toggleRow.style.pointerEvents = 'auto';
            toggleRow.style.borderTop = '1px solid rgba(0, 0, 0, 0.35)';

            ['pointerdown', 'pointerup', 'mousedown', 'mouseup'].forEach(function (evt) {
                toggleRow.addEventListener(evt, function (e) { e.stopPropagation(); });
            });

            toggleRow.addEventListener('click', function (e) {
                e.stopPropagation();

                if (viewerExpandedNodes.has(id))
                    viewerExpandedNodes.delete(id);
                else
                    viewerExpandedNodes.add(id);

                setupCytoscapeShowLabels();
            });

            box.appendChild(toggleRow);

            if (viewerOpen)
                viewerLines.forEach(function (text) {
                    const row = document.createElement('div');
                    row.textContent = text;
                    row.style.whiteSpace = 'nowrap';
                    row.style.padding = '1px 0';
                    row.style.borderTop = '1px solid rgba(0, 0, 0, 0.12)';
                    box.appendChild(row);
                });
        }
    }

    return box;
}

//Builds a node's +/- toggle. Clicking it flips the node's temporary "show all properties" state and
//rebuilds the overlay — no query is staged. The button captures its own clicks so the graph doesn't
//also handle them (select / deselect the node).
function makeToggleButton(n) {
    const expanded = expandedNodes.has(String(n.id()));

    const btn = document.createElement('div');
    btn.style.position = 'absolute';
    btn.style.pointerEvents = 'auto';
    btn.style.cursor = 'pointer';
    btn.style.userSelect = 'none';
    btn.style.boxSizing = 'border-box';
    btn.style.width = '8px';
    btn.style.height = '8px';
    btn.style.borderRadius = '50%';
    btn.style.display = 'flex';
    btn.style.alignItems = 'center';
    btn.style.justifyContent = 'center';
    btn.style.fontSize = '9px';
    btn.style.fontWeight = 'bold';
    btn.style.lineHeight = '1';
    btn.style.color = '#fff';
    //No circle — just the glyph, with a dark outline so it stays legible over any background.
    btn.style.textShadow = '0 0 2px rgba(0, 0, 0, 0.9), 0 0 2px rgba(0, 0, 0, 0.9)';
    //Centered on its (left, top) point and scaled with the zoom (set in the update loop) so it stays
    //the same size relative to the node.
    btn.style.transformOrigin = 'center center';
    if (expanded) {
        btn.textContent = '−';
        btn.title = 'Hide properties';
    } else {
        btn.textContent = '+';
        btn.title = 'Show all properties';
    }

    ['pointerdown', 'pointerup', 'mousedown', 'mouseup'].forEach(function (evt) {
        btn.addEventListener(evt, function (e) { e.stopPropagation(); });
    });

    btn.addEventListener('click', function (e) {
        e.stopPropagation();

        const id = String(n.id());
        if (expandedNodes.has(id))
            expandedNodes.delete(id);
        else
            expandedNodes.add(id);

        setupCytoscapeShowLabels();
    });

    return btn;
}

//Shows or hides a node's properties, as a click on its +/- button does: the element menu's Show / Hide properties.
function toggleCytoscapeNodeProps(id) {
    id = String(id);

    if (expandedNodes.has(id))
        expandedNodes.delete(id);
    else
        expandedNodes.add(id);

    setupCytoscapeShowLabels();
}

//(Re)builds the per-node overlay: a +/- toggle on every node with properties, plus a box for the nodes
//that currently have something to show (their show-marked props, or all props while expanded).
function setupCytoscapeShowLabels() {
    teardownShowLabels();

    if (!cyIsLive())
        return;

    const container = cy.container();
    if (!container)
        return;

    const propNodes = cy.nodes().filter(function (n) {
        return nodeAllPropLines(n).length > 0 || hasCanvasDetails(n);
    });
    const cardedEdges = cy.edges().filter(hasEdgeDetails);

    if (propNodes.length === 0 && cardedEdges.length === 0) {
        fitDetailsNodes([]);
        return;
    }

    if (getComputedStyle(container).position === 'static')
        container.style.position = 'relative';

    //Leader-line SVG inserted as the FIRST child so it renders behind the graph canvases (a last-child
    //overlay draws on top; a first-child one draws underneath) — the lines then pass behind the nodes.
    const svgNS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(svgNS, 'svg');
    svg.style.position = 'absolute';
    svg.style.top = '0';
    svg.style.left = '0';
    svg.style.width = '100%';
    svg.style.height = '100%';
    svg.style.overflow = 'visible';
    svg.style.pointerEvents = 'none';
    container.insertBefore(svg, container.firstChild);
    showLineLayer = svg;

    //Boxes and buttons both sit on top of the graph (appended last). The layers pass clicks through;
    //only the individual buttons capture them.
    const boxLayer = document.createElement('div');
    boxLayer.style.position = 'absolute';
    boxLayer.style.top = '0';
    boxLayer.style.left = '0';
    boxLayer.style.width = '100%';
    boxLayer.style.height = '100%';
    boxLayer.style.overflow = 'hidden';
    boxLayer.style.pointerEvents = 'none';
    container.appendChild(boxLayer);
    showBoxLayer = boxLayer;

    const btnLayer = document.createElement('div');
    btnLayer.style.position = 'absolute';
    btnLayer.style.top = '0';
    btnLayer.style.left = '0';
    btnLayer.style.width = '100%';
    btnLayer.style.height = '100%';
    btnLayer.style.overflow = 'visible';
    btnLayer.style.pointerEvents = 'none';
    container.appendChild(btnLayer);
    showButtonLayer = btnLayer;

    showItems = [];
    nodeButtons = [];

    const cards = [];

    propNodes.forEach(function (n) {
        //A details card is the vertex, and already shows every property, so it has no +/- to show them.
        if (hasCanvasDetails(n)) {
            const card = makeDetailsCard(n);

            boxLayer.appendChild(card);
            cards.push({ el: card, line: null, node: n, card: true });

            return;
        }

        const btn = makeToggleButton(n);
        btnLayer.appendChild(btn);
        nodeButtons.push({ el: btn, node: n });

        const lines = boxContentFor(n);
        if (lines.length === 0)
            return;

        const color = n.data('bgColor') || '#0d6efd';

        const line = document.createElementNS(svgNS, 'line');
        line.setAttribute('stroke', color);
        line.setAttribute('stroke-linecap', 'round');
        svg.appendChild(line);

        const box = makeShowBox(color, lines, n);
        boxLayer.appendChild(box);

        showItems.push({ el: box, line: line, node: n });
    });

    cardedEdges.forEach(function (e) {
        const card = makeDetailsCard(e);

        boxLayer.appendChild(card);
        cards.push({ el: card, line: null, node: e, card: true });
    });

    fitDetailsNodes(cards);
    cards.forEach(item => showItems.push(item));
    updateCytoscapeShowLabels();
}

//Pins each toggle to its node's bottom-right corner and each box just under its node, in rendered
//(screen) space; called on every cy render.
function updateCytoscapeShowLabels() {
    if (!showButtonLayer || !cyIsLive())
        return;

    //Scale the boxes with the zoom so they stay proportional to the (also-scaling) nodes. On the map the nodes keep their
    //zoom-1 size whatever the zoom (applyGeoMarkerScale), so the + and the box keep theirs too, where they grew with the
    //earth until 2026-11-05 (the user).
    let z = cy.zoom();

    if (geoLayerOn)
        z = geoMarkerSize;

    nodeButtons.forEach(function (item) {
        const n = item.node;
        if (n.removed() || !n.visible()) {
            item.el.style.display = 'none';
            return;
        }

        item.el.style.display = 'flex';
        const p = n.renderedPosition();
        item.el.style.left = (p.x + n.renderedWidth() / 2 + 3 * z - 2) + 'px';
        item.el.style.top = (p.y + n.renderedHeight() / 2 + 3 * z - 2) + 'px';
        item.el.style.transform = 'translate(-50%, -50%) scale(' + z + ')';
    });

    showItems.forEach(item => {
        const n = item.node;
        if (n.removed() || !n.visible()) {
            item.el.style.display = 'none';
            if (item.line)
                item.line.style.display = 'none';

            return;
        }

        item.el.style.display = '';
        const p = renderedPlaceOf(n);

        //A details card stands where its vertex is, at its size, or at its edge's middle, and shows its selection.
        if (item.card) {
            item.el.style.left = p.x + 'px';
            item.el.style.top = p.y + 'px';
            item.el.style.transform = 'translate(-50%, -50%) scale(' + z + ')';
            item.el.classList.toggle('is-selected', n.selected());
            return;
        }

        const h = n.renderedHeight();
        const nodeBottom = p.y + h / 2;
        const boxTop = nodeBottom + 16 * z;

        item.el.style.left = p.x + 'px';
        item.el.style.top = boxTop + 'px';
        item.el.style.transform = 'translate(-50%, 0) scale(' + z + ')';

        //Leader line from the node's center to the box's center (the node-colored ends are hidden
        //behind the node and the box, so it reads as a stem connecting the two).
        if (item.line) {
            item.line.style.display = '';
            const boxCenterY = boxTop + (item.el.offsetHeight * z) / 2;
            item.line.setAttribute('x1', p.x);
            item.line.setAttribute('y1', p.y);
            item.line.setAttribute('x2', p.x);
            item.line.setAttribute('y2', boxCenterY);
            item.line.setAttribute('stroke-width', Math.max(1.4, Math.min(4, z * 2)));
        }
    });
}

//Keeps the Cytoscape canvas sized to its container. Cytoscape fixes its canvas dimensions when drawn
//and doesn't re-measure, so a page/panel resize would otherwise leave the 2D canvas at its old size
//(bigger than the container). A ResizeObserver calls cy.resize() (coalesced to one call per frame).
function observeCyResize(container) {
    unobserveCyResize();

    if (typeof ResizeObserver === 'undefined' || !container)
        return;

    cyResizeObserver = new ResizeObserver(() => {
        if (cyResizeFrameId)
            return;

        cyResizeFrameId = requestAnimationFrame(() => {
            cyResizeFrameId = null;
            if (cy && cy.container())
                cy.resize();
        });
    });

    cyResizeObserver.observe(container);
}

function unobserveCyResize() {
    if (cyResizeObserver) {
        cyResizeObserver.disconnect();
        cyResizeObserver = null;
    }

    if (cyResizeFrameId) {
        cancelAnimationFrame(cyResizeFrameId);
        cyResizeFrameId = null;
    }
}

//Takes the middle-button pan's listeners back off the container they were put on and off the window. The window's
//would otherwise hold that container once it has left the page, one more for every container the view was drawn in.
function detachMiddleDragPan() {
    if (middlePanDetach) {
        middlePanDetach();
        middlePanDetach = null;
    }
}

function setCytoscapeDotNetRef(ref) {
    dotNetRef = ref;
}

//Stops any in-flight (possibly still-animating) layout before we mutate or tear down the graph.
//A layout animates for ~500ms after run(); if cy.destroy() lands during that window (e.g. the user
//clicks Load DB again the moment it re-enables) the old layout's next animation frame runs against a
//destroyed core and throws inside Cytoscape (endBatch -> notify on a null internal).
//
//Stopping alone does not prevent that for cose, which runs on animation frames of its own (Cytoscape 3.34.3): the
//frame it has already asked for still comes, finds it stopped, and finishes as a completed run does, placing every
//node and fitting the view. Switching from 2D to Table and back while one ran destroyed the instance in between, and
//that frame threw (2026-11-02); on a live instance it moved the graph after a newer layout had taken over. So a
//stopped layout is left nothing to place and no view to fit.
function stopCurrentLayout() {
    if (currentLayout) {
        const layout = currentLayout;//stop() clears currentLayout, through runTrackedLayout's layoutstop

        try {
            layout.stop();
        }
        catch { }

        if (layout.options && layout.options.cy) {
            layout.options.eles = layout.options.cy.collection();
            layout.options.fit = false;
        }

        currentLayout = null;
    }
}

//True only when the graph exists and its container is still attached to the page. Mutating/batching
//calls must check this first: an optimistic sync (fired when the Generated buffer changes on commit)
//can race a redraw / Load DB that has just removed #cyGraph, and batching against a detached (or
//torn-down) core throws inside Cytoscape (endBatch -> notify on a null internal). Skipping is safe —
//the pending draw redraws the canvas from the current data anyway.
function cyIsLive() {
    return !!cy && !!cy.container() && cy.container().isConnected;
}

//Ceiling on how far an auto-fit may zoom in. Without it, fitting a single small node (or a tiny graph)
//zooms so far that the one node fills the whole canvas. Users can still zoom in further by hand — this
//only clamps the automatic fit.
const CY_MAX_FIT_ZOOM = 2.5;

function capCytoscapeZoom() {
    if (!cyIsLive())
        return;

    if (cy.zoom() > CY_MAX_FIT_ZOOM) {
        cy.zoom(CY_MAX_FIT_ZOOM);
        cy.center();
    }
}

//Runs a layout as the tracked "current" one (canceling any previous), so a later redraw/destroy can
//stop it cleanly. Returns the layout so callers can still attach their own layoutstop handlers.
function runTrackedLayout(layout) {
    stopCurrentLayout();
    currentLayout = layout;
    layout.one('layoutstop', () => {
        if (currentLayout === layout)
            currentLayout = null;

        capCytoscapeZoom();
    });
    layout.run();

    return layout;
}

//── The Map: the 2D view under a geographic layout ─────────────────────────────────────────────
//Located nodes (data.geoLat / geoLng, read from their properties by GeoCoordinates.cs) are placed by an
//equirectangular projection onto a plane the size of the bundled world image, and the earth is drawn
//on a canvas behind the graph, kept in step with every pan and zoom. Nodes with no place are hidden
//with their edges — placed arbitrarily they would read as wrong, and a map is a claim about where
//things are.
//
//The layer is its own <canvas>, a sibling just behind the graph container: two styles paint it — the
//satellite image, or the coastlines of Natural Earth's 1:110m countries as lines on the theme's own
//background — and grid mode, which on a plain graph is a square grid on the container's CSS
//background, becomes here a graticule: meridians and parallels at a spacing that suits the zoom, with
//their degrees along the edges. Every asset loads on first use.
//
//The satellite image comes in two sizes. The standard one, 2048 × 1024 and 0.3 MB, is all a map fetches
//by itself: sharp at the zoom a world fits in, soft on a country. The detailed one, 8192 × 4096 and
//3.7 MB, is fetched only once the tab asks for high resolution (setCytoscapeMapDetail), with the standard
//one painted until it arrives — the user's call, 2026-09-13, so that nobody downloads 3.7 MB for a map
//they never zoom into. The PWA's service worker leaves it out of its precache for the same reason.
const GEO_MAP_URL = '_content/GraphDBViewer.Core/img/earth-2048.jpg';
const GEO_MAP_DETAILED_URL = '_content/GraphDBViewer.Core/img/earth-8192.jpg';
const GEO_OUTLINE_URL = '_content/GraphDBViewer.Core/img/earth-outline.json';
const GEO_MAP_WIDTH = 2048;
const GEO_MAP_HEIGHT = 1024;
const GEO_GRATICULE_MIN_SPACING = 70;//px between graticule lines, at least
const GEO_MARKER_PROPS = 'font-size padding text-max-width width height border-width overlay-padding underlay-padding';//what the marker scale bypasses on a node
const GEO_MARKER_EDGE_PROPS = 'font-size width arrow-scale';//and on an edge
let geoLayerOn = false;
let geoLayer = null;//the canvas behind the graph while the map is on
let geoStyle = 'satellite';//'satellite' or 'outline' — setCytoscapeMapStyle
let geoDetail = 'standard';//'standard' or 'high' — setCytoscapeMapDetail
let geoMarkerSize = 1;//Node size on map, as a multiple of the size a node keeps at every zoom — setCytoscapeMapNodeSize
let geoImages = {};//url → the world image at that size, once loaded
let geoDetailedFailed = false;//the detailed image's download failed; asked for again only by choosing it again
let geoOutline = null;//the coastline rings [[[lon, lat], ...], ...], once loaded
let geoLoading = {};//url → true while a fetch is in flight, so a redraw never starts a second one

//A tile server, when one is set in Settings (MapTilesPreference.cs): the earth is then drawn from its Web Mercator tiles,
//each reprojected in strips onto the equirectangular world (graphGeometry.geoTileStrips), at the tile zoom whose pixels
//match the screen's. A tile still on its way is stood in for by the nearest ancestor that has arrived, cut to its part,
//so a zoom shows the coarser earth sharpening rather than holes. Neither look nor resolution applies to tiles; the
//satellite look's veil does, so nodes read against a bright street map as they do against the image.
const GEO_TILE_MAX_ZOOM = 22;//no server has more; one with fewer fails the deeper tiles, and their ancestors stand in
const GEO_TILE_MOST = 300;//tiles drawn at once at most: past it the zoom steps down, as on a very tall screen
const GEO_TILE_CACHE = 600;//tiles kept, the least recently drawn going first
let geoTiles = null;//{ template, key, viewport, keep } while the earth comes from a tile server: setCytoscapeMapTiles
let geoTileViewSay = null;//says the view drawn while the tiles are Google's: sayGeoTileView
let geoTileCache = new Map();//url → { image, ready, failed }, in the order last drawn
let geoTileShown = { zoom: null, own: 0, standIns: 0 };//what the last draw drew, for tests
let geoTileReport = null;//'ready' or 'failed', last told to Home
let geoTileRedraw = 0;//the frame a redraw for arrived tiles is waiting on

//The plane the Map's nodes stand on: the projection's (GEO_MAP_WIDTH wide) times scale, moved by x and y. Cytoscape draws a
//label in the plane's units at the size the zoom shows it, so a marker's 12 pixels at a city's zoom were a font of a
//hundredth of a pixel, which the browser measures so badly that the label was cut to "…", and past that drew nothing
//(the user, 2026-11-06). So the zoom is kept low: past GEO_PLANE_ZOOM_MOST the plane is made larger and the zoom smaller
//by the same factor, about the middle of the view, and the other way below GEO_PLANE_ZOOM_LEAST, never smaller than the
//projection's own. Nothing on screen moves; the view's middle is the plane's 0, so its numbers stay small at a street.
const GEO_PLANE_ZOOM_MOST = 8;//a label's font no less than an eighth of its pixels: thinner than that it draws pale
const GEO_PLANE_ZOOM_LEAST = 0.5;
const GEO_PLANE_ZOOM_AIM = 2;
let geoPlane = { scale: 1, x: 0, y: 0 };
let geoPlaneChanging = false;
let geoPlaneWaiting = 0;//the timer waiting for an animated zoom to end before the plane follows it

//A point of the projection on the plane.
function geoPlanePoint(p) {
    return { x: p.x * geoPlane.scale + geoPlane.x, y: p.y * geoPlane.scale + geoPlane.y };
}

function geoPosition(n) {
    const lat = n.data('geoLat');
    const lng = n.data('geoLng');

    if (typeof lat !== 'number' || typeof lng !== 'number')
        return undefined;

    return geoPlanePoint(window.graphGeometry.geoProject(lat, lng, GEO_MAP_WIDTH, GEO_MAP_HEIGHT));
}

//Puts the nodes on another plane, the viewport moved with them so nothing on screen changes, and the routes bent for it.
function setGeoPlane(next) {
    if (!cy)
        return;

    const f = next.scale / geoPlane.scale;
    const zoom = cy.zoom();
    const pan = cy.pan();

    if (f === 1 && next.x === geoPlane.x && next.y === geoPlane.y)
        return;

    geoPlaneChanging = true;

    try {
        cy.batch(() => {
            cy.nodes().forEach(n => {
                const p = n.position();

                n.position({ x: (p.x - geoPlane.x) * f + next.x, y: (p.y - geoPlane.y) * f + next.y });
            });
        });

        const before = geoPlane;

        geoPlane = next;

        if (geoLayerOn)
            applyGeoEdgeBends();

        cy.viewport({
            zoom: zoom / f,
            pan: { x: pan.x + before.x * zoom - next.x * zoom / f, y: pan.y + before.y * zoom - next.y * zoom / f }
        });
    }
    finally {
        geoPlaneChanging = false;
    }
}

//The projection's own plane, which a layout places on and the plain view gets back.
function resetGeoPlane() {
    clearTimeout(geoPlaneWaiting);
    setGeoPlane({ scale: 1, x: 0, y: 0 });
}

//Keeps the zoom between GEO_PLANE_ZOOM_LEAST and GEO_PLANE_ZOOM_MOST on every zoom. An animated one, as Find a place's
//glide, is followed once it has ended: changing the plane under it would throw the rest of its way off.
function keepGeoPlaneInRange() {
    if (!cy || !geoLayerOn || geoPlaneChanging)
        return;

    if (cy.animated()) {
        clearTimeout(geoPlaneWaiting);
        geoPlaneWaiting = setTimeout(keepGeoPlaneInRange, 100);
        return;
    }

    const zoom = cy.zoom();
    let scale = geoPlane.scale;

    if (zoom > GEO_PLANE_ZOOM_MOST)
        scale = geoPlane.scale * zoom / GEO_PLANE_ZOOM_AIM;
    else if (zoom < GEO_PLANE_ZOOM_LEAST && geoPlane.scale > 1)
        scale = Math.max(1, geoPlane.scale * zoom / GEO_PLANE_ZOOM_AIM);
    else
        return;

    if (scale === 1) {
        setGeoPlane({ scale: 1, x: 0, y: 0 });
        return;
    }

    //The view's middle, on the plane now and on the projection, becomes the new plane's 0.
    const container = cy.container();
    const pan = cy.pan();
    const middle = {
        x: ((container.clientWidth / 2 - pan.x) / zoom - geoPlane.x) / geoPlane.scale,
        y: ((container.clientHeight / 2 - pan.y) / zoom - geoPlane.y) / geoPlane.scale
    };

    setGeoPlane({ scale: scale, x: -middle.x * scale, y: -middle.y * scale });
}

function geoLayout() {
    resetGeoPlane();

    cy.nodes().forEach(n => {
        if (geoPosition(n))
            n.removeClass('gdbv-unlocated');
        else
            n.addClass('gdbv-unlocated');
    });

    enableGeoLayer();
    applyGeoEdgeBends();
    startGeoMarkerScale();

    runTrackedLayout(cy.layout({
        name: 'preset',
        positions: n => geoPosition(n) || { x: 0, y: 0 },
        padding: 50,
        fit: true,
        animate: true,
        animationDuration: 500
    }));
}

//Every other layout: nothing hidden, no earth behind the graph, edges straight again.
function clearGeoLayout() {
    if (!cy)
        return;

    resetGeoPlane();
    cy.nodes('.gdbv-unlocated').removeClass('gdbv-unlocated');
    clearGeoEdgeBends();
    clearGeoMarkerScale();
    disableGeoLayer();
}

//On the map a node is a marker: its box and label keep their screen size whatever the zoom — the
//stylesheet's size, as at zoom 1 — instead of growing with the earth as the user zooms in, which is
//how the 2D view behaves and is right there and wrong here (the user's call, 2026-09-02). Each
//element's stylesheet values are read once, bypasses off, and kept in its scratch; every zoom writes
//them back divided by the zoom. Edge labels the same. Off again with the map, so the plain view is
//untouched. A node sized to its label keeps that size, measured once (geoMarkerBase) and scaled like a fixed one. Its
//outline and the shade drawn round it on hover and press scale too: left in the plane's units they grew with the zoom,
//a selected node's outline several times its box and the shade a dark square over half the map (the user, 2026-11-06).
function startGeoMarkerScale() {
    cy.off('zoom', keepGeoPlaneInRange);
    cy.on('zoom', keepGeoPlaneInRange);
    cy.off('zoom', applyGeoMarkerScale);
    cy.on('zoom', applyGeoMarkerScale);
    cy.off('select unselect', 'node', geoMarkerBorder);
    cy.on('select unselect', 'node', geoMarkerBorder);
    applyGeoMarkerScale();
}

//A node selected or let go has the stylesheet's outline for that state, measured again and scaled to the zoom.
function geoMarkerBorder(evt) {
    const n = evt.target;
    const base = n.scratch('gdbvMarker');

    if (!cy || !geoLayerOn || !base)
        return;

    n.removeStyle('border-width');
    base.border = n.numericStyle('border-width');
    n.style('border-width', base.border * geoMarkerSize / cy.zoom());
}

//Node size on map (2026-11-06, the user): how large the nodes are drawn on the map, as a percentage of the size they keep
//at every zoom, so they stay that size as the map is zoomed. Their labels, outlines and shades with them, and the edges,
//their labels and arrowheads, and the + and property boxes too (the user, 2026-11-06: "it should all get scaled"). One
//setting with the globe's, and remembered for the next map when none is showing, as the style is.
function setCytoscapeMapNodeSize(percent) {
    geoMarkerSize = Math.max(0.1, Math.min(10, (+percent || 100) / 100));

    if (cy && geoLayerOn) {
        applyGeoMarkerScale();
        updateCytoscapeShowLabels();
    }
}

function applyGeoMarkerScale() {
    if (!cy || !geoLayerOn)
        return;

    const f = 1 / cy.zoom();
    const nodeF = f * geoMarkerSize;

    //Each node's own sizes, read once and outside the batch, so a node sized to its label is measured in its stylesheet's
    //font while that is still the font it has.
    cy.nodes().forEach(n => {
        if (!n.scratch('gdbvMarker'))
            n.scratch('gdbvMarker', geoMarkerBase(n));
    });

    cy.batch(() => {
        cy.nodes().forEach(n => {
            const base = n.scratch('gdbvMarker');

            n.style({
                'font-size': (base.font * nodeF) + 'px',
                'padding': (base.padding * nodeF) + 'px',
                'text-max-width': (base.maxWidth * nodeF) + 'px',
                'width': base.width * nodeF,
                'height': base.height * nodeF,
                'border-width': base.border * nodeF,
                'overlay-padding': base.overlay * nodeF,
                'underlay-padding': base.underlay * nodeF
            });
        });

        //Edges too: a route stays a line, not a band, and its arrowhead an arrowhead. Node size on map sizes them with the
        //nodes, their labels and arrowheads included (the user, 2026-11-06). Cytoscape draws an arrowhead no smaller than
        //29 units whatever the edge's width, so its scale is divided by the zoom as well.
        cy.edges().forEach(e => {
            let base = e.scratch('gdbvMarker');

            if (!base) {
                e.removeStyle(GEO_MARKER_EDGE_PROPS);
                base = { font: e.numericStyle('font-size'), width: e.numericStyle('width'), arrow: e.numericStyle('arrow-scale') };
                e.scratch('gdbvMarker', base);
            }

            e.style({
                'font-size': (base.font * nodeF) + 'px',
                'width': base.width * nodeF,
                'arrow-scale': base.arrow * nodeF
            });
        });
    });
}

//A node's stylesheet sizes, its bypasses off. A node sized to its label gets that size as a number, measured now in its
//stylesheet's font: left as 'label', it was measured again at every zoom in the font divided by the zoom, and Cytoscape
//rounds a label's size to whole units of the graph's plane, so zoomed in to a street the font was a few thousandths of
//a pixel, the label 0 wide, and every node gone (found by the showcase's Find a place, 2026-11-06), and long before
//that the rounding had made a label-sized node several times too wide.
function geoMarkerBase(n) {
    n.removeStyle(GEO_MARKER_PROPS);

    const base = {
        font: n.numericStyle('font-size'),
        padding: n.numericStyle('padding'),
        maxWidth: n.numericStyle('text-max-width'),
        width: n.numericStyle('width'),
        height: n.numericStyle('height'),
        border: n.numericStyle('border-width'),
        overlay: n.numericStyle('overlay-padding'),
        underlay: n.numericStyle('underlay-padding')
    };

    if (n.style('width') === 'label')
        base.width = n.width();

    if (n.style('height') === 'label')
        base.height = n.height();

    return base;
}

function clearGeoMarkerScale() {
    cy.off('zoom', keepGeoPlaneInRange);
    cy.off('zoom', applyGeoMarkerScale);
    cy.off('select unselect', 'node', geoMarkerBorder);

    cy.batch(() => {
        cy.nodes().forEach(n => {
            n.removeStyle(GEO_MARKER_PROPS);
            n.removeScratch('gdbvMarker');
        });

        cy.edges().forEach(e => {
            e.removeStyle(GEO_MARKER_EDGE_PROPS);
            e.removeScratch('gdbvMarker');
        });
    });
}

//A route between two places follows the great circle, which on the flat map is a curve: each edge
//between located nodes gets Cytoscape's `segments` curve through points along the arc, computed by
//graphGeometry.geoEdgeBend from the coordinates — the layout's positions come from the same numbers,
//so the controls are right before the nodes have moved. Applied as a bypass style so the stylesheet's
//own curve comes back untouched when the map goes. Short hops stay straight, and so does a route
//whose arc crosses the antimeridian, where the map ends.
function applyGeoEdgeBends() {
    const geo = window.graphGeometry;

    cy.edges().forEach(e => {
        const s = e.source();
        const t = e.target();
        const lat1 = s.data('geoLat');
        const lng1 = s.data('geoLng');
        const lat2 = t.data('geoLat');
        const lng2 = t.data('geoLng');

        if (typeof lat1 !== 'number' || typeof lng1 !== 'number' || typeof lat2 !== 'number' || typeof lng2 !== 'number')
            return;

        const bend = geo.geoEdgeBend(lat1, lng1, lat2, lng2, GEO_MAP_WIDTH * geoPlane.scale, GEO_MAP_HEIGHT * geoPlane.scale);

        if (!bend)
            return;

        e.style({
            'curve-style': 'segments',
            'segment-weights': bend.weights.join(' '),
            'segment-distances': bend.distances.join(' ')
        });
        e.addClass('gdbv-geodesic');
    });
}

function clearGeoEdgeBends() {
    cy.edges('.gdbv-geodesic')
      .removeStyle('curve-style segment-weights segment-distances')
      .removeClass('gdbv-geodesic');
}

//Which earth the map and the globe draw: 'satellite' (the image) or 'outline' (coastlines). Remembered
//for the next map even when none is showing, so it can be set before the draw.
function setCytoscapeMapStyle(style) {
    if (style === 'outline')
        geoStyle = 'outline';
    else
        geoStyle = 'satellite';

    if (geoLayerOn) {
        loadGeoAssets();
        drawGeoLayer();
    }
}

//How sharp the satellite earth is: 'standard' (the 2048 image, fetched with the map) or 'high' (the 8192
//one, fetched only now). Remembered for the next map even when none is showing, as the style is. `retry`
//is true when the user chose it, which is what tries a failed download again; the same value told again
//before a draw is not, or every redraw of a map without a network would ask for 3.7 MB once more.
function setCytoscapeMapDetail(detail, retry) {
    if (detail === 'high')
        geoDetail = 'high';
    else
        geoDetail = 'standard';

    if (retry)
        geoDetailedFailed = false;

    if (geoLayerOn) {
        loadGeoAssets();
        drawGeoLayer();
    }
}

//What the layer is doing, for tests: whether it is on, its style and detail, whether the graticule is
//drawn and at what spacing, and which assets have arrived — imageWidth being the width of the image it
//paints, so 2048 until a detailed one has arrived.
//Where the earth comes from: null for the built-in images, or { template, key } for a tile server. Remembered for the next
//map even when none is showing, as the style is. A new server forgets the old one's tiles.
function setCytoscapeMapTiles(source) {
    let next = null;

    if (source && source.template)
        next = { template: source.template, key: source.key || '', viewport: !!source.viewport, keep: source.keep !== false };

    if (!next && !geoTiles)
        return;

    //The same server with the memory switch flipped: its tiles stay, and turned off, a draw lets go of all but those on
    //screen at once (trimGeoTiles).
    if (next && geoTiles && next.template === geoTiles.template && next.key === geoTiles.key) {
        if (next.keep !== geoTiles.keep) {
            geoTiles.keep = next.keep;

            if (geoLayerOn)
                drawGeoLayer();
        }

        return;
    }

    clearGeoTiles();
    geoTiles = next;

    if (geoLayerOn) {
        loadGeoAssets();
        drawGeoLayer();
    }
}

function clearGeoTiles() {
    geoTileCache.forEach(entry => dropGeoTile(entry));
    geoTileCache = new Map();
    geoTileShown = { zoom: null, own: 0, standIns: 0 };
    geoTileReport = null;

    if (geoTileViewSay)
        geoTileViewSay.forget();
}

//Google's copyright follows the view (2026-11-07): while the tiles are Google's (a source with viewport), the zoom and
//bounds drawn are said to Home, which asks Google for the copyright to show beside its logo (OnMapTileView).
function sayGeoTileView(z, west, east, north, south) {
    if (!geoTileViewSay)
        geoTileViewSay = window.graphGeometry.geoTileViewReporter(view => {
            if (dotNetRef)
                dotNetRef.invokeMethodAsync('OnMapTileView', view.zoom, view.west, view.east, view.north, view.south).catch(() => { });
        }, 300);

    geoTileViewSay(z, west, east, north, south);
}

//A tile no longer wanted: one still loading is stopped, so a fast pan does not queue a screenful of tiles behind it.
function dropGeoTile(entry) {
    entry.image.onload = null;
    entry.image.onerror = null;

    if (!entry.ready && !entry.failed)
        entry.image.src = '';
}

function getCytoscapeGeoState() {
    let step = null;

    if (geoLayerOn && gridEnabled && cy)
        step = window.graphGeometry.geoGraticuleStep(GEO_MAP_WIDTH * geoPlane.scale * cy.zoom() / 360, GEO_GRATICULE_MIN_SPACING);

    const image = geoEarthImage();
    let imageWidth = 0;

    if (image)
        imageWidth = image.naturalWidth;

    return {
        on: geoLayerOn,
        style: geoStyle,
        detail: geoDetail,
        graticule: geoLayerOn && gridEnabled,
        graticuleStep: step,
        imageReady: !!image,
        imageWidth: imageWidth,
        outlineReady: !!geoOutline,
        tiles: !!geoTiles,
        tileZoom: geoTileShown.zoom,
        tilesDrawn: geoTileShown.own,
        tileStandIns: geoTileShown.standIns,
        place: geoPlaceOnScreen(),
        planeScale: geoPlane.scale
    };
}

function enableGeoLayer() {
    const container = cy.container();

    if (!geoLayer) {
        geoLayer = document.createElement('canvas');
        geoLayer.className = 'gdbv-geo-layer';
        geoLayer.style.position = 'absolute';
        geoLayer.style.inset = '0';
        geoLayer.style.width = '100%';
        geoLayer.style.height = '100%';
        geoLayer.style.pointerEvents = 'none';
    }

    //Behind the graph: an earlier sibling of its container, which is positioned and so paints over it.
    if (geoLayer.parentElement !== container.parentElement)
        container.parentElement.insertBefore(geoLayer, container);

    if (!geoLayerOn) {
        geoLayerOn = true;
        cy.on('zoom pan resize', drawGeoLayer);
    }

    //The square grid would paint over the earth; while the map is on, grid mode is the graticule.
    clearPlainGrid();
    loadGeoAssets();
    drawGeoLayer();
}

function disableGeoLayer() {
    if (!geoLayerOn)
        return;

    geoLayerOn = false;
    cy.off('zoom pan resize', drawGeoLayer);

    if (geoLayer && geoLayer.parentElement)
        geoLayer.parentElement.removeChild(geoLayer);

    //Grid mode outlives the map: the square grid comes back on the plain graph.
    if (gridEnabled)
        updateGridOverlay();
}

//Fetches what the current style needs, once; the layer redraws itself when it arrives. The satellite look
//always takes the standard image, the quick one, and the detailed one as well when the tab wants it.
function loadGeoAssets() {
    //Tiles are fetched by the draw, for the part of the world it shows.
    if (geoTiles)
        return;

    if (geoStyle === 'satellite') {
        loadGeoImage(GEO_MAP_URL);

        if (geoDetail === 'high' && !geoDetailedFailed)
            loadGeoImage(GEO_MAP_DETAILED_URL);
    }

    if (geoStyle === 'outline' && !geoOutline && !geoLoading[GEO_OUTLINE_URL]) {
        geoLoading[GEO_OUTLINE_URL] = true;
        fetch(GEO_OUTLINE_URL)
            .then(r => r.json())
            .then(rings => {
                geoOutline = rings;
                geoLoading[GEO_OUTLINE_URL] = false;
                drawGeoLayer();
            })
            .catch(() => {
                geoLoading[GEO_OUTLINE_URL] = false;
            });
    }
}

//One size of the world image, fetched once. The detailed one also tells Home how its download is going,
//for the line in the canvas corner: 3.7 MB takes long enough on a slow link to need saying, and an
//offline one never arrives at all.
function loadGeoImage(url) {
    if (geoImages[url] || geoLoading[url])
        return;

    const detailed = url === GEO_MAP_DETAILED_URL;
    geoLoading[url] = true;

    if (detailed)
        reportDetailedEarth('loading');

    const image = new Image();
    image.onload = function () {
        geoImages[url] = image;
        geoLoading[url] = false;

        if (detailed)
            reportDetailedEarth('ready');

        drawGeoLayer();
    };
    image.onerror = function () {
        geoLoading[url] = false;

        if (detailed) {
            geoDetailedFailed = true;
            reportDetailedEarth('failed');
        }
    };
    image.src = url;
}

//'loading', 'ready' or 'failed', to Home. By the time 3.7 MB arrives the page may have moved on and
//disposed the reference, which rejects rather than throws.
function reportDetailedEarth(state) {
    if (!dotNetRef)
        return;

    dotNetRef.invokeMethodAsync('OnDetailedEarth', state).catch(() => { });
}

//The world image to paint: the detailed one when the tab wants it and it has arrived, else the standard
//one, which is also what shows while the detailed one is on its way or if it never comes.
function geoEarthImage() {
    if (geoDetail === 'high' && geoImages[GEO_MAP_DETAILED_URL])
        return geoImages[GEO_MAP_DETAILED_URL];

    return geoImages[GEO_MAP_URL] || null;
}

//The colors the layer paints with, by theme. The image gets a veil so nodes read against it; the
//outline draws on the theme's own background, so it needs none.
function geoPalette() {
    if (document.documentElement.getAttribute('data-bs-theme') === 'dark')
        return {
            veil: 'rgba(10, 14, 20, 0.55)',
            coast: 'rgba(186, 198, 214, 0.75)',
            edge: 'rgba(186, 198, 214, 0.35)',
            grid: 'rgba(255, 255, 255, 0.28)',
            label: 'rgba(235, 240, 248, 0.9)',
            halo: 'rgba(10, 14, 20, 0.85)'
        };

    return {
        veil: 'rgba(255, 255, 255, 0.28)',
        coast: 'rgba(58, 70, 90, 0.75)',
        edge: 'rgba(58, 70, 90, 0.35)',
        grid: 'rgba(0, 0, 0, 0.28)',
        label: 'rgba(30, 36, 46, 0.9)',
        halo: 'rgba(255, 255, 255, 0.85)'
    };
}

//Paints the layer for the current viewport: the world plane is model coordinates [0, W] × [0, H], so
//on screen it is W·zoom wide and starts at the pan. Everything is clipped to that rectangle except the
//graticule's labels, which sit along the visible edges of the world.
function drawGeoLayer() {
    if (!cy || !geoLayerOn || !geoLayer)
        return;

    const container = cy.container();
    const width = container.clientWidth;
    const height = container.clientHeight;
    const dpr = window.devicePixelRatio || 1;

    if (geoLayer.width !== Math.round(width * dpr) || geoLayer.height !== Math.round(height * dpr)) {
        geoLayer.width = Math.round(width * dpr);
        geoLayer.height = Math.round(height * dpr);
    }

    const ctx = geoLayer.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    paintGeoScene(ctx, geoWorldRect(cy.zoom(), cy.pan()), width, height, gridEnabled);
    paintGeoPlace(ctx);
}

//── A place found ─────────────────────────────────────────────────────────────────────────────
//The search under the layers button (2026-11-06, the user): an address or a name looked up, the Map taken there and a pin
//stood on it. On the screen's layer only, not in an export, which is of the graph.
const GEO_PLACE_SPAN = 0.02;//degrees across the view for a place the geocoder gave no size, a few streets
const GEO_PLACE_SPAN_LEAST = 0.004;//and the fewest for any place, a house's box being smaller than a street
let geoPlace = null;//{ lat, lng } while a place is shown: showCytoscapePlace

//Where the place found is on screen now, or null.
function geoPlaceOnScreen() {
    if (!geoPlace || !cy)
        return null;

    const world = geoWorldRect(cy.zoom(), cy.pan());

    return {
        x: world.x + (geoPlace.lng + 180) / 360 * world.w,
        y: world.y + (90 - geoPlace.lat) / 180 * world.h
    };
}

//The pin, its point on the place: a round head on a tapering stem, red with a white rim so it reads on the satellite
//earth, a street map and the outline alike.
function paintGeoPlace(ctx) {
    const at = geoPlaceOnScreen();

    if (!at)
        return;

    const x = at.x;
    const y = at.y;

    ctx.save();
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.bezierCurveTo(x - 2, y - 7, x - 9, y - 12, x - 9, y - 20);
    ctx.arc(x, y - 20, 9, Math.PI, 0);
    ctx.bezierCurveTo(x + 9, y - 12, x + 2, y - 7, x, y);
    ctx.closePath();
    ctx.fillStyle = '#dc3545';
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 2;
    ctx.fill();
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(x, y - 20, 3.5, 0, Math.PI * 2);
    ctx.fillStyle = '#ffffff';
    ctx.fill();
    ctx.restore();
}

//Takes the Map to a place and pins it: in the middle, framed by what it spans with room round it, or by a few streets
//when the geocoder did not say. Glides there, as the zoom buttons do not, so the eye follows where it went.
function showCytoscapePlace(lat, lng, span) {
    geoPlace = { lat: lat, lng: lng };

    if (!cy)
        return;

    const container = cy.container();
    const width = container.clientWidth;
    const height = container.clientHeight;
    const point = geoPlanePoint(window.graphGeometry.geoProject(lat, lng, GEO_MAP_WIDTH, GEO_MAP_HEIGHT));
    let degrees = GEO_PLACE_SPAN;

    if (typeof span === 'number' && span > 0)
        degrees = span * 1.5;

    degrees = Math.min(180, Math.max(GEO_PLACE_SPAN_LEAST, degrees));

    const zoom = Math.min(width, height) / (degrees * GEO_MAP_WIDTH * geoPlane.scale / 360);

    cy.stop();
    cy.animate({ zoom: zoom, pan: { x: width / 2 - point.x * zoom, y: height / 2 - point.y * zoom } }, { duration: 600 });
    drawGeoLayer();
}

//Takes the pin away; the view stays where it is.
function clearCytoscapePlace() {
    geoPlace = null;
    drawGeoLayer();
}

//Where the world lands on screen for a zoom and pan, on the plane the nodes stand on now.
function geoWorldRect(zoom, pan) {
    return {
        x: pan.x + geoPlane.x * zoom,
        y: pan.y + geoPlane.y * zoom,
        w: GEO_MAP_WIDTH * geoPlane.scale * zoom,
        h: GEO_MAP_HEIGHT * geoPlane.scale * zoom
    };
}

//Paints the earth — and the graticule when asked — for a world rectangle onto any context of the given
//logical size: the layer on screen, or an export's canvas.
function paintGeoScene(ctx, world, width, height, graticule) {
    const colors = geoPalette();

    ctx.save();
    ctx.beginPath();
    ctx.rect(world.x, world.y, world.w, world.h);
    ctx.clip();

    const image = geoEarthImage();

    if (geoTiles) {
        drawGeoTiles(ctx, world, width, height);
        ctx.fillStyle = colors.veil;
        ctx.fillRect(world.x, world.y, world.w, world.h);
    }
    else if (geoStyle === 'satellite' && image) {
        ctx.drawImage(image, world.x, world.y, world.w, world.h);
        ctx.fillStyle = colors.veil;
        ctx.fillRect(world.x, world.y, world.w, world.h);
    }
    else if (geoStyle === 'outline')
        drawGeoOutline(ctx, world, colors);

    if (graticule)
        drawGeoGraticuleLines(ctx, world, colors, width, height);

    ctx.restore();

    if (graticule)
        drawGeoGraticuleLabels(ctx, world, colors, width, height);
}

//The tiles over the part of the world on screen, at the zoom whose pixels match the context's: its own scale (the
//screen's pixel ratio) times the world's width. Each tile is its own image when it has arrived, else the nearest
//ancestor's quarter, sixteenth... of one; and a tile neither has is left to the background. An export's canvas takes
//the zoom the screen has, whose tiles are here, and neither asks the cache to let go of the screen's nor counts as
//what the screen shows.
function drawGeoTiles(ctx, world, width, height) {
    const geo = window.graphGeometry;
    const left = Math.max(world.x, 0);
    const right = Math.min(world.x + world.w, width);
    const top = Math.max(world.y, 0);
    const bottom = Math.min(world.y + world.h, height);

    if (right <= left || bottom <= top)
        return;

    const west = (left - world.x) / world.w * 360 - 180;
    const east = (right - world.x) / world.w * 360 - 180;
    const north = 90 - (top - world.y) / world.h * 180;
    const south = 90 - (bottom - world.y) / world.h * 180;
    const onScreen = !!geoLayer && ctx.canvas === geoLayer;
    const scale = ctx.getTransform().a || 1;
    let z = geo.geoTileZoom(world.w * scale, GEO_TILE_MAX_ZOOM);

    if (!onScreen && geoTileShown.zoom !== null)
        z = geoTileShown.zoom;

    let tiles = geo.geoTileRange(z, west, east, north, south);

    while (tiles.length > GEO_TILE_MOST && z > 0) {
        z--;
        tiles = geo.geoTileRange(z, west, east, north, south);
    }

    const wanted = new Set();
    let own = 0;
    let standIns = 0;

    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';

    tiles.forEach(t => {
        const url = geo.geoTileUrl(geoTiles.template, t.z, t.x, t.y, geoTiles.key);
        const entry = geoTileEntry(url);
        wanted.add(url);

        if (entry.ready) {
            drawGeoTile(ctx, world, entry.image, t, 0, 0, 1);
            own++;
            return;
        }

        //The nearest ancestor that has arrived, and which part of it this tile is.
        for (let k = 1; k <= t.z; k++) {
            const ax = t.x >> k;
            const ay = t.y >> k;
            const ancestor = geoTileCache.get(geo.geoTileUrl(geoTiles.template, t.z - k, ax, ay, geoTiles.key));

            if (ancestor && ancestor.ready) {
                const part = 1 / (1 << k);
                drawGeoTile(ctx, world, ancestor.image, t, (t.x - (ax << k)) * part, (t.y - (ay << k)) * part, part);
                standIns++;
                return;
            }
        }
    });

    //The lowest zooms are asked for too, once, so there is always an ancestor to stand in while the rest arrive.
    for (let low = 0; low <= Math.min(z - 1, 2); low++)
        geo.geoTileRange(low, west, east, north, south).forEach(t => {
            const url = geo.geoTileUrl(geoTiles.template, t.z, t.x, t.y, geoTiles.key);
            geoTileEntry(url);
            wanted.add(url);
        });

    if (!onScreen)
        return;

    trimGeoTiles(wanted);
    geoTileShown = { zoom: z, own: own, standIns: standIns };

    if (geoTiles.viewport)
        sayGeoTileView(z, west, east, north, south);
}

//Tile t, drawn from the square of `image` starting at (u, v) and `part` of its side wide (all as fractions of the
//image), in the strips its rows need on this world.
function drawGeoTile(ctx, world, image, t, u, v, part) {
    const geo = window.graphGeometry;
    const size = image.naturalWidth || 256;
    const strips = geo.geoTileStrips(t.z, t.x, t.y, world, geo.geoTileStripCount(t.z, world.h));

    //Each strip reaches a little past its neighbors, so the seams between them and between tiles never show the
    //background through a sub-pixel gap.
    strips.forEach(s => {
        const sy = (v + s.f0 * part) * size;
        const sh = (s.f1 - s.f0) * part * size;

        ctx.drawImage(image, u * size, sy, part * size, sh, s.x, s.y, s.w + 0.5, s.h + 0.5);
    });
}

//The tile at a URL, asked for on first sight; drawn again, it moves to the back of the queue the cache is trimmed from.
function geoTileEntry(url) {
    let entry = geoTileCache.get(url);

    if (entry) {
        geoTileCache.delete(url);
        geoTileCache.set(url, entry);
        return entry;
    }

    entry = { image: new Image(), ready: false, failed: false };

    //Read across origins, so the canvas stays exportable: a server that does not allow it sends no tiles here at all,
    //which the corner then says, rather than tiles that make Export fail.
    entry.image.crossOrigin = 'anonymous';
    entry.image.onload = function () {
        entry.ready = true;
        reportGeoTiles('ready');
        redrawGeoForTiles();
    };
    entry.image.onerror = function () {
        entry.failed = true;

        if (geoTileReport !== 'ready')
            reportGeoTiles('failed');
    };
    entry.image.src = url;
    geoTileCache.set(url, entry);

    return entry;
}

//Past the cache's size the least recently drawn tiles go, and at any size a tile still loading that this draw did not
//want: the world has moved on from it.
function trimGeoTiles(wanted) {
    const drop = new Set();

    geoTileCache.forEach((entry, url) => {
        if (!wanted.has(url) && !entry.ready && !entry.failed)
            drop.add(url);
    });

    //Past the cache's size, or with the memory switch off (Map settings, 2026-11-07) past none: only the drawn stay.
    let most = GEO_TILE_CACHE;

    if (geoTiles && geoTiles.keep === false)
        most = 0;

    let excess = geoTileCache.size - drop.size - most;

    for (const url of geoTileCache.keys()) {
        if (excess <= 0)
            break;

        if (!wanted.has(url) && !drop.has(url)) {
            drop.add(url);
            excess--;
        }
    }

    drop.forEach(url => {
        dropGeoTile(geoTileCache.get(url));
        geoTileCache.delete(url);
    });
}

//Arrived tiles are drawn on the next frame, all together.
function redrawGeoForTiles() {
    if (geoTileRedraw)
        return;

    geoTileRedraw = requestAnimationFrame(() => {
        geoTileRedraw = 0;
        drawGeoLayer();
    });
}

//'ready' once a tile has arrived, 'failed' when one has failed and none has arrived: to Home, which says in the canvas
//corner that the server sends nothing, as it does for the detailed image.
function reportGeoTiles(state) {
    if (geoTileReport === state)
        return;

    geoTileReport = state;

    if (!dotNetRef)
        return;

    dotNetRef.invokeMethodAsync('OnMapTiles', state).catch(() => { });
}

//Coastlines and borders as one stroke per ring, and the edge of the world so the map has one.
function drawGeoOutline(ctx, world, colors) {
    ctx.strokeStyle = colors.edge;
    ctx.lineWidth = 1;
    ctx.strokeRect(world.x + 0.5, world.y + 0.5, world.w - 1, world.h - 1);

    if (!geoOutline)
        return;

    const sx = world.w / 360;
    const sy = world.h / 180;

    ctx.strokeStyle = colors.coast;
    ctx.lineJoin = 'round';
    ctx.beginPath();

    geoOutline.forEach(ring => {
        ring.forEach((p, i) => {
            const x = world.x + (p[0] + 180) * sx;
            const y = world.y + (90 - p[1]) * sy;

            if (i === 0)
                ctx.moveTo(x, y);
            else
                ctx.lineTo(x, y);
        });
    });

    ctx.stroke();
}

//Meridians and parallels at the spacing the zoom allows, only across the part of the world on screen.
function drawGeoGraticuleLines(ctx, world, colors, width, height) {
    const step = window.graphGeometry.geoGraticuleStep(world.w / 360, GEO_GRATICULE_MIN_SPACING);
    const sx = world.w / 360;
    const sy = world.h / 180;
    const top = Math.max(world.y, 0);
    const bottom = Math.min(world.y + world.h, height);
    const left = Math.max(world.x, 0);
    const right = Math.min(world.x + world.w, width);

    ctx.strokeStyle = colors.grid;
    ctx.lineWidth = 1;
    ctx.beginPath();

    for (let lon = -180; lon <= 180; lon += step) {
        const x = Math.round(world.x + (lon + 180) * sx) + 0.5;

        if (x < 0 || x > width)
            continue;

        ctx.moveTo(x, top);
        ctx.lineTo(x, bottom);
    }

    for (let lat = -90; lat <= 90; lat += step) {
        const y = Math.round(world.y + (90 - lat) * sy) + 0.5;

        if (y < 0 || y > height)
            continue;

        ctx.moveTo(left, y);
        ctx.lineTo(right, y);
    }

    ctx.stroke();
}

//Degrees along the edges: longitudes across the top and bottom, latitudes down the left and right —
//at the world's edge when it is on screen, else at the canvas's, so the labels never scroll away.
function drawGeoGraticuleLabels(ctx, world, colors, width, height) {
    const geo = window.graphGeometry;
    const step = geo.geoGraticuleStep(world.w / 360, GEO_GRATICULE_MIN_SPACING);
    const sx = world.w / 360;
    const sy = world.h / 180;
    const top = Math.max(world.y, 0);
    const bottom = Math.min(world.y + world.h, height);
    const left = Math.max(world.x, 0);
    const right = Math.min(world.x + world.w, width);
    const pad = 3;

    ctx.font = '11px system-ui, sans-serif';
    ctx.lineWidth = 3;
    ctx.lineJoin = 'round';
    ctx.strokeStyle = colors.halo;
    ctx.fillStyle = colors.label;

    const label = function (text, x, y) {
        ctx.strokeText(text, x, y);
        ctx.fillText(text, x, y);
    };

    ctx.textAlign = 'center';

    for (let lon = -180; lon <= 180; lon += step) {
        const x = world.x + (lon + 180) * sx;

        if (x < left + 12 || x > right - 12)
            continue;

        const text = geo.geoDegreeLabel(lon, 'lon');
        ctx.textBaseline = 'top';
        label(text, x, top + pad);
        ctx.textBaseline = 'bottom';
        label(text, x, bottom - pad);
    }

    ctx.textBaseline = 'middle';

    for (let lat = -90; lat <= 90; lat += step) {
        const y = world.y + (90 - lat) * sy;

        if (y < top + 14 || y > bottom - 14)
            continue;

        const text = geo.geoDegreeLabel(lat, 'lat');
        ctx.textAlign = 'left';
        label(text, left + pad, y);
        ctx.textAlign = 'right';
        label(text, right - pad, y);
    }
}

//Returns a Cytoscape layout config for one of the built-in layout names.
//All of these ship with cytoscape core — no extra layout extensions required.
function cytoscapeLayout(name) {
    const base = { padding: 50, animate: true, animationDuration: 500, fit: true };

    switch (name) {
        case 'userSaved':
            //Hand-pinned "user saved positions": don't run a force layout — the saved coordinates are
            //applied straight after via applyCytoscapePositions, so a preset keeps them from being moved.
            return { name: 'preset', padding: 50, fit: true };
        case 'grid':
            return { name: 'grid', ...base, avoidOverlap: true, spacingFactor: 1.2 };
        case 'circle':
            return { name: 'circle', ...base, avoidOverlap: true, spacingFactor: 1.2 };
        case 'concentric':
            return { name: 'concentric', ...base, minNodeSpacing: 40, concentric: n => n.degree(), levelWidth: () => 1 };
        case 'breadthfirst':
            return { name: 'breadthfirst', ...base, directed: true, spacingFactor: 1.3 };
        case 'random':
            return { name: 'random', ...base };
        case 'cose':
        default:
            return {
                name: 'cose',
                ...base,
                directed: true,
                spacingFactor: 1.5,
                avoidOverlap: true,
                nodeRepulsion: () => 8000, idealEdgeLength: () => 100,
                edgeElasticity: () => 100, gravity: 0.25, numIter: 1000
            };
    }
}

//Cluster-by-label layout: groups nodes by their vertex label, lays each group out as a compact grid,
//and spaces the groups around a ring so same-label nodes sit together. Positions are computed here (no
//clustering layout extension is vendored) and applied via a preset layout.
function clusterByLabelLayout() {
    if (!cyIsLive())
        return;

    const groups = {};
    cy.nodes().forEach(n => {
        const key = n.data('glabel') || n.data('label') || '·';
        (groups[key] = groups[key] || []).push(n);
    });

    const labels = Object.keys(groups).sort();
    const nodeGap = 72;
    const positions = {};

    //Grid each cluster and note its radius, so the ring can be sized to keep clusters from colliding.
    const clusters = labels.map(label => {
        const members = groups[label];
        const cols = Math.max(1, Math.ceil(Math.sqrt(members.length)));
        const rows = Math.ceil(members.length / cols);
        const w = (cols - 1) * nodeGap;
        const h = (rows - 1) * nodeGap;
        const radius = Math.sqrt(w * w + h * h) / 2 + nodeGap;

        return { members, cols, rows, radius };
    });

    const maxRadius = Math.max(nodeGap, ...clusters.map(c => c.radius));
    let ringRadius = 0;
    if (clusters.length > 1)
        ringRadius = Math.max(maxRadius * 2.4, (clusters.length * maxRadius * 2.4) / (2 * Math.PI));

    clusters.forEach((cluster, ci) => {
        const angle = (2 * Math.PI * ci) / clusters.length;
        const cx = ringRadius * Math.cos(angle);
        const cyc = ringRadius * Math.sin(angle);

        cluster.members.forEach((n, i) => {
            const col = i % cluster.cols;
            const row = Math.floor(i / cluster.cols);
            const gx = (col - (cluster.cols - 1) / 2) * nodeGap;
            const gy = (row - (cluster.rows - 1) / 2) * nodeGap;
            positions[n.id()] = { x: cx + gx, y: cyc + gy };
        });
    });

    runTrackedLayout(cy.layout({
        name: 'preset',
        positions: n => positions[n.id()],
        padding: 50,
        fit: true,
        animate: true,
        animationDuration: 500
    }));
}

//Runs the pinned-aware layout for the current graph (shared by a fresh draw and an in-place reload).
//Saved positions (gdbvX/gdbvY → data.px/py) are honored ONLY for the user-saved layout; every other
//layout arranges all nodes and ignores them.
function applyCytoscapeLayout(layoutName) {
    if (layoutName === 'geo') {
        geoLayout();
        return;
    }

    clearGeoLayout();

    if (layoutName === 'cluster') {
        clusterByLabelLayout();
        return;
    }

    let pinned = cy.collection();
    if (layoutName === 'userSaved')
        pinned = cy.nodes().filter(n => n.data('px') !== undefined && n.data('py') !== undefined);

    if (pinned.length > 0 && pinned.length === cy.nodes().length) {
        //All nodes are pinned — they're already at their positions; just fit the view.
        cy.fit(undefined, 50);
        capCytoscapeZoom();
    } else if (pinned.length > 0) {
        //Some nodes are pinned: lock them and force-direct (cose) the rest around the locked pins.
        pinned.lock();
        const layout = cy.layout(cytoscapeLayout('cose'));
        layout.one('layoutstop', () => pinned.unlock());
        runTrackedLayout(layout);
    } else {
        //No pins: run the selected layout on all nodes. The user-saved layout with no pinned nodes
        //has nothing to place, so fall back to a plain force-directed (cose) arrangement.
        let name = layoutName;
        if (name === 'userSaved')
            name = 'cose';

        runTrackedLayout(cy.layout(cytoscapeLayout(name)));
    }
}

//The Cytoscape stylesheet, shared by the live 2D view (drawCytoscapeGraph) and the off-screen
//SVG export (cytoscapeLayoutToSvg) so both render nodes and edges identically.
function cytoscapeStyle(withCards) {
    const style = [
            {
                selector: 'node',
                style: {
                    'label': 'data(label)',
                    'background-color': '#0d6efd',
                    'color': '#fff',
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'width': 'label',
                    'height': 'label',
                    'padding': '8px',
                    'shape': 'roundrectangle',
                    'font-size': '12px',
                    'text-wrap': 'ellipsis',
                    'text-max-width': '120px'
                }
            },
            {
                //A true circle: fixed equal width/height (not sized to the label, which would oval it).
                selector: 'node[shape = "circle"]',
                style: { 'shape': 'ellipse', 'width': 54, 'height': 54, 'text-wrap': 'wrap', 'text-max-width': '46px' }
            },
            {
                //Oval keeps the label-hugging ellipse (grows with the label text).
                selector: 'node[shape = "oval"]',
                style: { 'shape': 'ellipse' }
            },
            {
                selector: 'node[shape = "square"]',
                style: { 'shape': 'rectangle' }
            },
            {
                selector: 'node[shape = "rectangle"]',
                style: { 'shape': 'round-rectangle' }
            },
            {
                selector: 'node[shape = "triangle"]',
                style: { 'shape': 'triangle' }
            },
            {
                selector: 'node[shape = "hexagon"]',
                style: { 'shape': 'hexagon' }
            },
            {
                selector: 'node[image]',
                style: {
                    'background-image': 'data(image)',
                    'background-fit': 'cover',
                    'background-color': '#ffffff',
                    'shape': 'ellipse',
                    'width': 44,
                    'height': 44,
                    'padding': '0px',
                    'border-width': 1,
                    'border-color': '#adb5bd',
                    'text-valign': 'bottom',
                    'text-margin-y': 3
                }
            },
            {
                selector: 'node[bgColor]',
                style: {
                    'background-color': 'data(bgColor)'
                }
            },
            {
                selector: 'node[nodeSize]',
                style: {
                    'width': 'data(nodeSize)',
                    'height': 'data(nodeSize)'
                }
            },
            {
                selector: 'edge',
                style: {
                    'label': 'data(label)',
                    'curve-style': 'bezier',
                    'target-arrow-shape': 'triangle',
                    'line-color': '#6c757d',
                    'target-arrow-color': '#6c757d',
                    'width': 2,
                    'font-size': '10px',
                    'text-rotation': 'autorotate',
                    'text-margin-y': -10,
                    'color': '#495057'
                }
            },
            {
                //Color edges by their label (data.edgeColor from the shared palette); selection still overrides below.
                selector: 'edge[edgeColor]',
                style: {
                    'line-color': 'data(edgeColor)',
                    'target-arrow-color': 'data(edgeColor)'
                }
            },
            {
                selector: 'node:selected',
                style: {
                    'background-color': '#fd7e14',
                    'border-width': 2,
                    'border-color': '#dc3545'
                }
            },
            {
                selector: 'edge:selected',
                style: {
                    'line-color': '#fd7e14',
                    'target-arrow-color': '#fd7e14',
                    'width': 3
                }
            },
            {
                //A node the Map cannot place — no latitude/longitude among its properties. Hidden with
                //its edges, which Cytoscape hides along with either endpoint; back the moment the
                //layout is anything but geographic (clearGeoLayout).
                selector: '.gdbv-unlocated',
                style: {
                    'display': 'none'
                }
            }
    ];

    //Show details on canvas: a carded vertex is the card's size and draws nothing itself; the card over it is its body
    //and shows its selection. Only where cards are drawn: on the live canvas, as HTML over it, and in the SVG (fresh
    //layout) export, painted in (cytoscapeLayoutToSvg). An edge with a card says its label on the card.
    if (withCards)
        style.splice(style.length - 1, 0, {
            selector: 'edge[?details]',
            style: {
                'label': ''
            }
        });

    if (withCards)
        style.splice(style.length - 1, 0, {
            selector: 'node[detailsW]',
            style: {
                'label': '',
                'width': 'data(detailsW)',
                'height': 'data(detailsH)',
                'padding': '0px',
                'shape': 'round-rectangle',
                'background-opacity': 0,
                'background-image': 'none',
                'border-width': 0
            }
        });

    return style;
}

//Whether the browser gives WebGL 2, which the WebGL renderer needs: without it Cytoscape throws as the instance is
//made, so the canvas renderer draws instead. The context it asks for to find out is let go of at once.
function supportsWebgl2() {
    if (cyWebgl2 !== null)
        return cyWebgl2;

    cyWebgl2 = false;

    try {
        const gl = document.createElement('canvas').getContext('webgl2');

        if (gl) {
            cyWebgl2 = true;
            loseWebglContext(gl);
        }
    }
    catch { }

    return cyWebgl2;
}

function loseWebglContext(gl) {
    const lose = gl.getExtension('WEBGL_lose_context');

    if (lose)
        lose.loseContext();
}

//The WebGL renderer draws its frames without the 'render' event the canvas renderer emits after each (Cytoscape
//3.34.3), and the shown-properties boxes follow that event through pan, zoom and drag. So a WebGL instance emits it.
function emitRenderAfterWebglFrames(instance) {
    const renderer = instance.renderer();

    if (!renderer || !renderer.webgl)
        return;

    const render = renderer.render;

    renderer.render = function (options) {
        render.call(renderer, options);

        if (!options || !options.forcedContext)
            instance.emit('render');
    };
}

//The WebGL contexts an instance holds, to let go of when it goes rather than whenever the page collects them: a
//browser keeps sixteen or so and loses the oldest past that, which may be the 3D view's. A WebGL instance has its
//own, and every instance made after one has a spare, since Cytoscape 3.34.3 counts its canvas layers on a prototype
//they all share and never counts back.
function webglContextsOf(instance) {
    const renderer = instance && instance.renderer();
    const contexts = (renderer && renderer.data && renderer.data.contexts) || [];

    if (typeof WebGL2RenderingContext === 'undefined')
        return [];

    return contexts.filter(c => c instanceof WebGL2RenderingContext);
}

//Cytoscape 3.34.3 decides whether an element takes up space while a new instance applies its elements' styles, before
//the instance has a renderer to measure a label with (2026-10-21, found by the 2D walks on the upgrade from 3.30.4,
//which asked later). An edge asks of its source first, so a source node sized by its label, as the viewer's every node
//is, was nothing wide then, and the answer stays cached with its style: every edge from it is taken for hidden, with no
//midpoint, no hit area and nothing drawn. The answers go once the renderer is there to measure.
function forgetEarlySpace(instance) {
    instance.elements().dirtyStyleCache();
}

//Destroys the live instance and lets go of the WebGL contexts it held.
function destroyCy() {
    const contexts = webglContextsOf(cy);

    cy.destroy();
    contexts.forEach(loseWebglContext);
    cy = null;

    //A new instance starts on the projection's own plane.
    clearTimeout(geoPlaneWaiting);
    geoPlane = { scale: 1, x: 0, y: 0 };
}

//Which renderer draws the live instance: 'webgl', 'canvas', or null with none. For the walks.
function cytoscapeRenderer() {
    if (!cy)
        return null;

    if (cyWebglMode)
        return 'webgl';

    return 'canvas';
}

//Puts a selection back after a redraw rebuilt the elements under it. Ids that no longer exist are
//skipped, so a redraw that removed something does not resurrect it as a selection.
function reselect(ids) {
    if (!ids || ids.length === 0)
        return;

    ids.forEach(function (id) {
        const el = cy.getElementById(id);

        if (el && el.length > 0)
            el.select();
    });
}

function drawCytoscapeGraph(elementsJson, containerId, layoutName) {
    const container = document.getElementById(containerId);
    if (!container) {
        console.error("Cytoscape container not found:", containerId);
        return;
    }

    //A full (re)draw is a new graph — drop the temporary "expanded properties" state.
    expandedNodes.clear();
    viewerExpandedNodes.clear();

    //Parsed once here so the element count can pick the render mode (see CY_HEAVY_THRESHOLD).
    const elements = JSON.parse(elementsJson);
    let count = 0;

    if (Array.isArray(elements))
        count = elements.length;
    else {
        if (elements.nodes)
            count += elements.nodes.length;

        if (elements.edges)
            count += elements.edges.length;
    }

    const heavy = count > CY_HEAVY_THRESHOLD;
    const webgl = count > CY_WEBGL_THRESHOLD && supportsWebgl2();

    //Reuse a live instance on the same container instead of destroy+recreate. Spam-clicking Load DB
    //could otherwise tear an instance down while its layout animation is mid-flight, which throws
    //inside Cytoscape (endBatch -> notify on a null renderer). Reusing never destroys, so there's no race.
    //A redraw that crosses the heavy/light threshold, or the WebGL one, can't reuse — the render options
    //are fixed at creation — so it falls through to the layout-stopped, race-safe re-create below.
    if (cy && cy.container() === container && container.isConnected && cyHeavyMode === heavy && cyWebglMode === webgl) {
        //What the user had selected is user state, and a redraw is not a reason to throw it away: a
        //redraw that lands a moment after a shift-select would otherwise leave the canvas looking
        //selected while nothing is, and the next Delete would silently do nothing at all. Only ids that
        //are still here come back, so elements the redraw removed stay removed.
        const wasSelected = cy.$(':selected').map(el => el.id());

        stopCurrentLayout();
        teardownShowLabels();
        cy.elements().remove();
        cy.add(elements);
        reselect(wasSelected);
        setupCytoscapeShowLabels();//first, so a carded vertex has its card's size when the layout places it
        applyCytoscapeLayout(layoutName);
        return;
    }

    if (cy) {
        stopCurrentLayout();
        unobserveCyResize();
        teardownShowLabels();
        //The map layer is a sibling of the container, which outlives the instance — take it down while
        //the instance can still unhook its listener.
        disableGeoLayer();
        destroyCy();
    }
    //Middle-button drag pans from anywhere on the canvas, including from on top of a node.
    //
    //Cytoscape already pans on a left drag, but only from empty background: starting on a node moves the
    //node instead, which on a dense graph means hunting for a gap before you can move the view at all.
    //The middle button has no other job here, so it is given this one.
    //
    //The browser's own middle-click autoscroll has to be refused, or it starts its scroll-wheel cursor
    //over the top of the drag. preventDefault on mousedown stops it beginning; the auxclick handler stops
    //the click that ends the drag from doing anything else on the way out.
    function attachMiddleDragPan(container) {
        if (!container || container.gdbvMiddlePan)
            return;

        //One container at a time: a draw in a new one means the one before has left the page.
        detachMiddleDragPan();

        container.gdbvMiddlePan = true;

        let panning = false;
        let lastX = 0;
        let lastY = 0;

        const stop = () => {
            if (!panning)
                return;

            panning = false;
            container.style.cursor = '';
            window.removeEventListener('mousemove', onMove, true);
            window.removeEventListener('mouseup', onUp, true);
        };

        const onMove = (e) => {
            if (!panning || !cy)
                return;

            cy.panBy({ x: e.clientX - lastX, y: e.clientY - lastY });
            lastX = e.clientX;
            lastY = e.clientY;
            e.preventDefault();
        };

        const onUp = (e) => {
            if (e.button === 1)
                e.preventDefault();

            stop();
        };

        const onDown = (e) => {
            if (e.button !== 1)
                return;

            panning = true;
            lastX = e.clientX;
            lastY = e.clientY;
            container.style.cursor = 'grabbing';

            //Listened for on the window, and in the capture phase: a drag that leaves the canvas — or
            //passes over a node, which stops the event itself — must still pan and must still end.
            window.addEventListener('mousemove', onMove, true);
            window.addEventListener('mouseup', onUp, true);

            e.preventDefault();
        };

        //Nothing else should act on the middle click that ended a pan.
        const onAuxClick = (e) => {
            if (e.button === 1)
                e.preventDefault();
        };

        container.addEventListener('mousedown', onDown);
        container.addEventListener('auxclick', onAuxClick);

        //A drag interrupted by the window losing focus would otherwise pan forever.
        window.addEventListener('blur', stop);

        middlePanDetach = () => {
            stop();
            container.removeEventListener('mousedown', onDown);
            container.removeEventListener('auxclick', onAuxClick);
            window.removeEventListener('blur', stop);
            container.gdbvMiddlePan = false;
        };
    }


    cyHeavyMode = heavy;
    cyWebglMode = webgl;

    const cyOptions = {
        container: container,
        elements: elements,
        style: cytoscapeStyle(true),
        layout: { name: 'preset' }
    };

    if (heavy) {
        //Big-graph render options (see CY_HEAVY_THRESHOLD): hide edges and draw a cached bitmap while the
        //viewport is moving, and stop supersampling on hi-DPI screens. motionBlur is already off by default.
        //A graph past CY_WEBGL_THRESHOLD is drawn by the GPU instead, at the screen's own resolution; the
        //first two serve the canvas renderer it hands back to close up.
        cyOptions.hideEdgesOnViewport = true;
        cyOptions.textureOnViewport = true;

        if (webgl)
            cyOptions.webgl = true;
        else
            cyOptions.pixelRatio = 1;
    }

    cy = cytoscape(cyOptions);
    forgetEarlySpace(cy);

    if (webgl)
        emitRenderAfterWebglFrames(cy);

    attachMiddleDragPan(container);

    cy.on('tap', 'node', function (evt) {
        const d = evt.target.data();
        if (!dotNetRef)
            return;

        //Double-click a node to expand its neighbors; a single click just selects.
        const now = Date.now();
        if (d.id === lastTapNodeId && (now - lastTapTime) < 350) {
            dotNetRef.invokeMethodAsync('OnNodeExpand', String(d.id));
            lastTapNodeId = null;
            lastTapTime = 0;
        } else {
            lastTapNodeId = d.id;
            lastTapTime = now;
        }

        //selectedGroup carries whatever is highlighted (:selected) at click time — captured synchronously
        //here, before a single-select collapses the group to just this node moments later — so delete mode
        //can drop the whole highlighted group when one of its members is clicked.
        dotNetRef.invokeMethodAsync('OnElementSelected', JSON.stringify({
            type: 'node',
            id: d.id,
            label: d.label || '',
            glabel: d.glabel || '',
            properties: d.properties || {},
            selectedGroup: currentSelectionList()
        }));
    });

    //A right-click on a vertex or an edge opens the page's menu for it where the pointer is (2026-11-04, the user).
    //Cytoscape holds the browser's own menu back over its canvas itself.
    cy.on('cxttap', 'node, edge', function (evt) {
        const d = evt.target.data();
        const e = evt.originalEvent;
        if (!dotNetRef || !e)
            return;

        let type = 'edge';
        if (evt.target.isNode())
            type = 'node';

        //What a click on it sends, for the menu's Style, which selects it first.
        const selection = { type: type, id: d.id, label: d.label || '', glabel: d.glabel || '', properties: d.properties || {} };

        if (type == 'edge') {
            selection.source = d.source;
            selection.target = d.target;
        }

        //Whether its properties are shown, for the menu's Show / Hide properties, the + button's twin: 'shown', 'hidden', or
        //null for an edge or a node with none, which has no button.
        let props = null;

        if (type == 'node' && nodeAllPropLines(evt.target).length > 0 && !hasCanvasDetails(evt.target)) {
            if (expandedNodes.has(String(d.id)))
                props = 'shown';
            else
                props = 'hidden';
        }

        dotNetRef.invokeMethodAsync('OnElementContextMenu', type, String(d.id), String(d.label || d.id), e.clientX, e.clientY, JSON.stringify(selection), props);
    });

    //A right-click on the empty space opens the page's menu for the canvas, with where a new component would go, in the
    //same grid as a tap's (2026-11-05, the user).
    cy.on('cxttap', function (evt) {
        const e = evt.originalEvent;

        if (evt.target !== cy || !dotNetRef || !e)
            return;

        let x = evt.position.x;
        let y = evt.position.y;

        if (gridEnabled) {
            x = Math.round(x / gridStep) * gridStep;
            y = Math.round(y / gridStep) * gridStep;
        }

        dotNetRef.invokeMethodAsync('OnCanvasContextMenu', e.clientX, e.clientY, x, y);
    });

    cy.on('tap', 'edge', function (evt) {
        const d = evt.target.data();
        if (dotNetRef)
            dotNetRef.invokeMethodAsync('OnElementSelected', JSON.stringify({
                type: 'edge',
                id: d.id,
                label: d.label || '',
                source: d.source,
                target: d.target,
                properties: d.properties || {},
                selectedGroup: currentSelectionList()
            }));
    });

    cy.on('tap', function (evt) {
        //A tap on empty canvas: report the click position so add-component mode can place a node there;
        //when that mode is off, .NET treats it as a deselect. In grid mode the position snaps to the grid.
        if (evt.target === cy && dotNetRef) {
            let x = evt.position.x;
            let y = evt.position.y;
            if (gridEnabled) {
                x = Math.round(x / gridStep) * gridStep;
                y = Math.round(y / gridStep) * gridStep;
            }
            dotNetRef.invokeMethodAsync('OnCanvasTapped', x, y);
        }
    });

    //Keep the shown-properties extensions pinned under their nodes through pan / zoom / drag / layout. Built before the
    //layout, so a carded vertex has its card's size when the layout places it.
    cy.on('render', updateCytoscapeShowLabels);
    setupCytoscapeShowLabels();
    applyCytoscapeLayout(layoutName);

    observeCyResize(container);
}

function redrawCytoscapeGraph(elementsJson, containerId) {
    if (!cy) {
        drawCytoscapeGraph(elementsJson, containerId);
        return;
    }

    stopCurrentLayout();
    cy.elements().remove();
    cy.add(JSON.parse(elementsJson));
    runTrackedLayout(cy.layout({ name: 'cose', animate: true, animationDuration: 500 }));
}

//Incremental expansion: refresh the elements already drawn, add the not-yet-present ones, and lay out
//just the new nodes (existing nodes are locked so they keep their positions).
function addToCytoscapeGraph(elementsJson) {
    if (!cy)
        return;

    const eles = JSON.parse(elementsJson);

    //An element already on the canvas can still arrive fuller this time: an expand is how a node that was
    //only ever an edge endpoint gets its own data. GUN makes that the common case — it stands in an empty
    //node for a link target it never walked. Only the data is refreshed; position and layout are untouched.
    const toRefresh = eles.filter(e => e.data && e.data.id && !cy.getElementById(e.data.id).empty());

    if (toRefresh.length > 0)
        cy.batch(function () {
            toRefresh.forEach(function (el) {
                const existing = cy.getElementById(el.data.id);

                Object.keys(el.data).forEach(function (key) {
                    if (key === 'id' || key === 'source' || key === 'target')
                        return;

                    if (existing.data(key) !== el.data[key])
                        existing.data(key, el.data[key]);
                });
            });
        });

    const toAdd = eles.filter(e => e.data && e.data.id && cy.getElementById(e.data.id).empty());

    if (toAdd.length === 0) {
        setupCytoscapeShowLabels();

        return;
    }

    //Nodes before edges so an edge's endpoints exist when it is added.
    toAdd.sort((a, b) => {
        let aEdge = 0;
        if (a.data && a.data.source)
            aEdge = 1;

        let bEdge = 0;
        if (b.data && b.data.source)
            bEdge = 1;

        return aEdge - bEdge;
    });

    stopCurrentLayout();
    const added = cy.add(toAdd);
    const existing = cy.nodes().difference(added.nodes());
    existing.lock();

    const layout = cy.layout({ name: 'cose', animate: true, animationDuration: 500, fit: false, randomize: false });
    layout.one('layoutstop', () => existing.unlock());
    runTrackedLayout(layout);

    setupCytoscapeShowLabels();
}

//Reconciles the live 2D graph to match an effective element set (the optimistic/uncommitted view):
//removes elements no longer present, updates changed data/pinned-positions in place (no layout — so a
//property/style edit doesn't reshuffle the graph), and adds genuinely new elements laying out only
//those (existing nodes stay locked). elementsJson is the same shape drawCytoscapeGraph consumes.
function syncCytoscapeGraph(elementsJson) {
    if (!cyIsLive())
        return;

    const eles = JSON.parse(elementsJson);

    const incomingIds = new Set();
    eles.forEach(e => {
        if (e.data && e.data.id !== undefined)
            incomingIds.add(String(e.data.id));
    });

    stopCurrentLayout();

    const toRemove = cy.elements().filter(ele => !incomingIds.has(String(ele.id())));

    cy.batch(() => {
        toRemove.remove();

        eles.forEach(el => {
            if (!el.data || el.data.id === undefined)
                return;

            const existing = cy.getElementById(String(el.data.id));
            if (existing.empty())
                return;

            const next = el.data;
            const cur = existing.data();

            //Drop data keys that disappeared (e.g. a cleared bgColor) so the stylesheet stops using them.
            Object.keys(cur).forEach(k => {
                if (k === 'id' || k === 'source' || k === 'target')
                    return;

                if (!(k in next))
                    existing.removeData(k);
            });

            Object.keys(next).forEach(k => {
                if (k === 'id' || k === 'source' || k === 'target')
                    return;

                existing.data(k, next[k]);
            });

            if (el.position && existing.isNode())
                existing.position(el.position);
        });
    });

    //Rebuild the shown-properties overlay now the node data (incl. showProps) has been reconciled.
    setupCytoscapeShowLabels();

    //Add only the genuinely new elements; lay out just those with the existing nodes locked.
    const toAdd = eles.filter(e => e.data && e.data.id !== undefined && cy.getElementById(String(e.data.id)).empty());
    if (toAdd.length === 0)
        return;

    //Nodes before edges so an edge's endpoints exist when it is added.
    toAdd.sort((a, b) => {
        let aEdge = 0;
        if (a.data && a.data.source)
            aEdge = 1;

        let bEdge = 0;
        if (b.data && b.data.source)
            bEdge = 1;

        return aEdge - bEdge;
    });

    const added = cy.add(toAdd);

    //New nodes given an explicit position (click-to-place / gdbv-pinned) stay exactly where placed;
    //only the position-less new nodes get laid out, with everything else locked so it doesn't move.
    const pinnedAdded = added.nodes().filter(n => n.data('px') !== undefined && n.data('py') !== undefined);
    const toLayout = added.nodes().difference(pinnedAdded);

    if (toLayout.length === 0)
        return;

    const locked = cy.nodes().difference(toLayout);
    locked.lock();

    const layout = cy.layout({ name: 'cose', animate: true, animationDuration: 500, fit: false, randomize: false });
    layout.one('layoutstop', () => locked.unlock());
    runTrackedLayout(layout);
}

//Re-runs a different layout on the already-drawn graph (no rebuild).
function setCytoscapeLayout(layoutName) {
    if (!cy)
        return;

    if (layoutName === 'geo') {
        geoLayout();
        return;
    }

    clearGeoLayout();

    if (layoutName === 'cluster')
        clusterByLabelLayout();
    else
        runTrackedLayout(cy.layout(cytoscapeLayout(layoutName)));
}

//Places nodes at the given saved positions — the "user saved positions" preset layout. json is
//[{id,x,y}]. Any running layout is stopped first so it can't fight the placement.
function applyCytoscapePositions(json) {
    if (!cyIsLive())
        return;

    stopCurrentLayout();

    const byId = {};
    JSON.parse(json).forEach(p => byId[p.id] = p);

    cy.batch(() => {
        cy.nodes().forEach(n => {
            const p = byId[n.id()];
            if (p)
                n.position({ x: p.x, y: p.y });
        });
    });

    cy.fit(undefined, 50);
    capCytoscapeZoom();
}

//Grid mode: draws a grid overlay behind the 2D graph and snaps nodes to it (on drag, and on
//click-to-place via the tap handler above). The overlay is a CSS background on the container, aligned
//to graph coordinates by tracking zoom/pan so the drawn lines line up with the snap positions.
//
//On the Map the same switch means the graticule instead — drawn by the map layer, in degrees — and
//no snapping, since a node's place there is its coordinates, not a grid cell.
function setCytoscapeGridMode(enabled) {
    if (!cyIsLive())
        return;

    gridEnabled = enabled;

    //Detach first so re-enabling (e.g. after a re-render) never stacks duplicate handlers.
    cy.off('zoom pan resize', updateGridOverlay);
    cy.off('dragfree', 'node', snapNodeToGrid);

    if (enabled) {
        cy.on('zoom pan resize', updateGridOverlay);
        cy.on('dragfree', 'node', snapNodeToGrid);
        updateGridOverlay();
    }
    else
        clearPlainGrid();

    if (geoLayerOn)
        drawGeoLayer();
}

//Grid size (2026-11-06, the user): the spacing of the grid's lines and of its snapping, in graph units. Kept for the next
//graph when none is drawn, as the grid's own switch is not; the lines are drawn again at once when the grid is on.
function setCytoscapeGridSize(step) {
    gridStep = Math.max(10, Math.min(200, +step || 50));

    if (cyIsLive() && gridEnabled)
        updateGridOverlay();
}

//Redraws the grid overlay so its lines fall on multiples of gridStep in graph coordinates.
function updateGridOverlay() {
    if (!cyIsLive() || !gridEnabled)
        return;

    //While the map is on, the graticule is the grid; the square one would paint over the earth.
    if (geoLayerOn) {
        clearPlainGrid();
        return;
    }

    const container = cy.container();
    if (!container)
        return;

    const step = gridStep * cy.zoom();
    const pan = cy.pan();

    container.style.backgroundImage = `linear-gradient(to right, rgba(128,128,128,0.3) 1px, transparent 1px),
        linear-gradient(to bottom, rgba(128,128,128,0.3) 1px, transparent 1px)`;
    container.style.backgroundSize = step + 'px ' + step + 'px';
    container.style.backgroundPosition = pan.x + 'px ' + pan.y + 'px';
}

function clearPlainGrid() {
    const container = cy && cy.container();

    if (!container)
        return;

    container.style.backgroundImage = '';
    container.style.backgroundSize = '';
    container.style.backgroundPosition = '';
}

//Snaps a dragged node to the nearest grid intersection — not on the map, where the drag is the
//user's and the grid is only drawn.
function snapNodeToGrid(evt) {
    if (geoLayerOn)
        return;

    const n = evt.target;
    const p = n.position();
    n.position({
        x: Math.round(p.x / gridStep) * gridStep,
        y: Math.round(p.y / gridStep) * gridStep
    });
}

//Current node positions in the 2D view as JSON [{id, x, y}] — used to persist a hand-arranged layout.
function getCytoscapePositions() {
    if (!cy)
        return "[]";

    //On the Map, on the projection's own plane, whatever the zoom has made of it.
    const list = cy.nodes().map(n => ({
        id: n.id(),
        x: Math.round((n.position('x') - geoPlane.x) / geoPlane.scale),
        y: Math.round((n.position('y') - geoPlane.y) / geoPlane.scale)
    }));

    return JSON.stringify(list);
}

//The elements currently highlighted (:selected) on the 2D canvas, as [{type, id}]. Cytoscape's own
//selection is the source of truth for a shift / box-select (which can hold several at once).
function currentSelectionList() {
    if (!cy)
        return [];

    return cy.elements(':selected').map(function (el) {
        let type;
        if (el.isNode())
            type = 'node';
        else
            type = 'edge';

        return { type: type, id: el.id() };
    });
}

//JSON of the current selection — the Delete key uses it to drop every selected component, not just the
//last-clicked one that .NET tracks as selectedElement.
function getCytoscapeSelectedElements() {
    return JSON.stringify(currentSelectionList());
}

//E2E-TEST-ONLY: nothing in the app calls this — it exists solely for the Playwright specs in
//e2e/, which call it via page.evaluate to find, click and assert on canvas elements (the cy
//instance is module-local, so the canvas is otherwise opaque to browser automation). Returns
//one element's data record plus its on-screen (rendered) position relative to the 2D
//container — the midpoint for an edge. Null when nothing matches. Read-only.
function getCytoscapeElementInfo(id) {
    if (!cy)
        return null;

    const el = cy.getElementById(String(id));
    if (el.length === 0)
        return null;

    let pos;
    if (el.isEdge())
        pos = el.renderedMidpoint();
    else
        pos = el.renderedPosition();

    return { data: el.data(), renderedPosition: { x: pos.x, y: pos.y } };
}

function centerCytoscapeGraph() {
    if (cy) {
        cy.fit(undefined, 50);
        capCytoscapeZoom();
    }
}

//Zooms the 2D graph around its center. factor > 1 zooms in, < 1 zooms out.
function zoomCytoscape(factor) {
    if (cy) {
        const container = cy.container();

        cy.zoom({
            level: cy.zoom() * factor,
            renderedPosition: { x: container.clientWidth / 2, y: container.clientHeight / 2 }
        });
    }
}

//A PNG of the 2D graph for Ask AI's picture tool, no longer than most pixels a side, on the view's own background so a
//label drawn light on a dark theme is still seen; or null. The whole graph, as the export has it; on the Map the export's
//viewport with the earth under it, shrunk to fit (2026-11-02).
async function cytoscapePicture(most) {
    if (!cy)
        return null;

    if (geoLayerOn)
        return shrinkPicture(await cytoscapeImage('png'), most);

    return withDetailsCards(cy.png({ full: true, maxWidth: most, maxHeight: most, bg: viewBackground(cy.container()) }), 'png', pageDetailsLook());
}

//The color behind an element as the page paints it: its own, or the nearest ancestor's that is not see-through.
function viewBackground(element) {
    for (let at = element; at != null && at !== document.documentElement; at = at.parentElement) {
        const color = getComputedStyle(at).backgroundColor;

        if (color && color !== 'transparent' && color !== 'rgba(0, 0, 0, 0)')
            return color;
    }

    return '#ffffff';
}

//An image data URL drawn again no longer than most pixels a side, as a PNG; one already small enough comes back as it
//was. For the pictures Ask AI's picture tool sends, which a model scales down past about 1568 pixels anyway.
function shrinkPicture(url, most) {
    if (!url)
        return Promise.resolve(null);

    return new Promise(resolve => {
        const image = new Image();

        image.onload = function () {
            const scale = Math.min(1, most / Math.max(image.width, image.height));

            if (scale === 1 && url.startsWith('data:image/png'))
                return resolve(url);

            const canvas = document.createElement('canvas');

            canvas.width = Math.max(1, Math.round(image.width * scale));
            canvas.height = Math.max(1, Math.round(image.height * scale));
            canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);

            resolve(canvas.toDataURL('image/png'));
        };
        image.onerror = function () {
            resolve(null);
        };
        image.src = url;
    });
}

//── Detailed view in an image ─────────────────────────────────────────────────────────────────────────────────────────
//An image of the 2D canvas has none of the HTML cards on it, and a carded vertex draws nothing itself, so until
//2026-11-07 (the user's "do the loose ends") an export left a hole where each card stood. Each card is painted over the
//image where its vertex stands, as graphGeometry lays it out at the live card's width: on a white export in the light
//theme's colors, over the page's own background in the page's.
const DETAILS_WHITE_LOOK = { bg: '#ffffff', fg: '#212529', muted: 'rgba(33, 37, 41, 0.75)', line: 'rgba(0, 0, 0, 0.175)' };

//The page's colors for a card, as the live card takes them from the theme.
function pageDetailsLook() {
    const style = getComputedStyle(cy.container());

    return {
        bg: style.getPropertyValue('--bs-body-bg').trim() || DETAILS_WHITE_LOOK.bg,
        fg: style.getPropertyValue('--bs-body-color').trim() || DETAILS_WHITE_LOOK.fg,
        muted: style.getPropertyValue('--bs-secondary-color').trim() || DETAILS_WHITE_LOOK.muted,
        line: style.getPropertyValue('--bs-border-color-translucent').trim() || DETAILS_WHITE_LOOK.line
    };
}

//A text's width in a font, as a canvas measures it.
function detailsMeasure() {
    const ctx = document.createElement('canvas').getContext('2d');

    return function (text, font) {
        ctx.font = font;

        return ctx.measureText(text).width;
    };
}

//The carded vertices an image of a graph shows, each laid out as its card: [{ node, layout }]. The live canvas's, or the
//off-screen instance the SVG (fresh layout) export lays out.
function cardsToPaint(look, graph = cy) {
    const geo = window.graphGeometry;
    const measure = detailsMeasure();
    const family = getComputedStyle(graph.container()).fontFamily;

    const carded = graph.nodes().filter(n => hasCanvasDetails(n) && n.visible()).union(graph.edges().filter(e => hasEdgeDetails(e) && e.visible()));

    return carded.map(el => {
        const cardLook = Object.assign({ color: detailsColorOf(el), selected: el.selected(), family: family }, look);
        let width = el.data('detailsW');

        if (el.isEdge())
            width = el.scratch('gdbvCardW');

        return { node: el, layout: geo.detailsCardLayout(detailsContentOf(el), cardLook, measure, width) };
    });
}

//Where a whole-graph image (full: true) puts a vertex's center, and how many image pixels a card pixel takes: the image
//is the elements' bounds at a scale, so the scale is the image's width over the bounds'.
function fullImagePlacer(bounds, imageWidth) {
    const scale = imageWidth / Math.max(1, Math.ceil(bounds.w));

    return function (n) {
        const p = modelPlaceOf(n);

        return { x: (p.x - bounds.x1) * scale, y: (p.y - bounds.y1) * scale, scale: scale };
    };
}

//Where an image of the viewport puts a vertex's center: its place on screen at the image's scale, a card as large as it
//shows (on the map the cards keep their zoom-1 size, updateCytoscapeShowLabels).
function viewportImagePlacer(imageWidth) {
    const scale = imageWidth / Math.max(1, cy.container().clientWidth);
    let z = cy.zoom();

    if (geoLayerOn)
        z = geoMarkerSize;

    return function (n) {
        const p = renderedPlaceOf(n);

        return { x: p.x * scale, y: p.y * scale, scale: z * scale };
    };
}

function paintCards(ctx, cards, place) {
    cards.forEach(card => {
        const at = place(card.node);

        window.graphGeometry.paintDetailsCard(ctx, card.layout, at.x, at.y, at.scale);
    });
}

//A whole-graph image with its cards painted on, as a promise of the image in its format; the image itself, as it was,
//when no vertex is carded.
function withDetailsCards(url, format, look) {
    const cards = cardsToPaint(look);

    if (!url || cards.length === 0)
        return url;

    const bounds = cy.mutableElements().boundingBox();

    return new Promise(resolve => {
        const image = new Image();

        image.onload = function () {
            const canvas = document.createElement('canvas');
            canvas.width = image.width;
            canvas.height = image.height;

            const ctx = canvas.getContext('2d');
            ctx.drawImage(image, 0, 0);
            paintCards(ctx, cards, fullImagePlacer(bounds, image.width));

            if (format === 'jpeg')
                resolve(canvas.toDataURL('image/jpeg', 0.92));
            else
                resolve(canvas.toDataURL('image/png'));
        };
        image.onerror = function () {
            resolve(url);
        };
        image.src = url;
    });
}

//An SVG of a graph with its cards drawn in, as vector shapes: whole (full) or the viewport, which only the live canvas has.
function svgWithDetailsCards(svg, full, look, graph = cy) {
    const cards = cardsToPaint(look, graph);
    const width = /<svg\b[^>]*?\swidth="([\d.]+)/.exec(svg || '');

    if (!svg || cards.length === 0 || !width)
        return svg;

    let place = viewportImagePlacer(Number(width[1]));

    if (full)
        place = fullImagePlacer(graph.mutableElements().boundingBox(), Number(width[1]));

    const family = getComputedStyle(graph.container()).fontFamily;
    const groups = cards.map(card => {
        const at = place(card.node);

        return window.graphGeometry.detailsCardSvg(card.layout, at.x, at.y, at.scale, family);
    });
    const close = svg.lastIndexOf('</svg>');

    return svg.slice(0, close) + groups.join('') + svg.slice(close);
}

//An off-screen instance has no HTML cards to measure: each carded vertex is sized to its card as graphGeometry lays it out
//at its own width, as the live card is, so a layout spaces the cards and the edges end at their edges (node[detailsW]).
function sizeDetailsNodes(graph) {
    const geo = window.graphGeometry;
    const measure = detailsMeasure();
    const family = getComputedStyle(graph.container()).fontFamily;

    graph.nodes().filter(hasCanvasDetails).forEach(n => {
        const look = Object.assign({ color: detailsColorOf(n), family: family }, DETAILS_WHITE_LOOK);
        const layout = geo.detailsCardLayout(detailsContentOf(n), look, measure);

        n.data({ detailsW: Math.ceil(layout.width), detailsH: Math.ceil(layout.height) });
    });
}

//Returns an image data URL of the current 2D graph (white background), or null.
//format 'jpeg' emits a JPEG, otherwise PNG.
//
//On the Map the export is the viewport as seen — the earth, and the graticule when grid mode is on,
//painted under the graph at twice the screen's size — because the earth has no elements for a
//whole-graph crop to find. That path returns a promise: the graph's own render is decoded into an
//image and drawn over the earth.
function cytoscapeImage(format) {
    if (!cy)
        return null;

    if (!geoLayerOn)
        if (format === 'jpeg')
            return withDetailsCards(cy.jpg({ full: true, scale: 2, bg: '#ffffff' }), format, DETAILS_WHITE_LOOK);
        else
            return withDetailsCards(cy.png({ full: true, scale: 2, bg: '#ffffff' }), format, DETAILS_WHITE_LOOK);

    const scale = 2;
    const canvas = mapExportCanvas(scale);
    const graph = cy.png({ full: false, scale: scale });
    const cards = cardsToPaint(pageDetailsLook());

    return new Promise(resolve => {
        const image = new Image();

        image.onload = function () {
            const ctx = canvas.getContext('2d');
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
            paintCards(ctx, cards, viewportImagePlacer(canvas.width));

            if (format === 'jpeg')
                resolve(canvas.toDataURL('image/jpeg', 0.92));
            else
                resolve(canvas.toDataURL('image/png'));
        };
        image.onerror = function () {
            resolve(graph);
        };
        image.src = graph;
    });
}

//The earth as the viewport shows it, on the page's own background, at a scale — what goes under an
//export of the Map.
function mapExportCanvas(scale) {
    const container = cy.container();
    const width = container.clientWidth;
    const height = container.clientHeight;
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);

    const ctx = canvas.getContext('2d');
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    ctx.fillStyle = getComputedStyle(document.body).backgroundColor || '#ffffff';
    ctx.fillRect(0, 0, width, height);
    paintGeoScene(ctx, geoWorldRect(cy.zoom(), cy.pan()), width, height, gridEnabled);

    return canvas;
}

let svgExtRegistered = false;

//Registers the cytoscape-svg extension once (it ships as a separate global).
function ensureSvgExtension() {
    if (svgExtRegistered)
        return true;

    if (typeof cytoscape === 'function' && typeof cytoscapeSvg !== 'undefined') {
        cytoscape.use(cytoscapeSvg);
        keepCanvasPathInSvg();
        svgExtRegistered = true;
        return true;
    }

    return false;
}

//canvas2svg, the drawing context inside cytoscape-svg (window.C2S), keeps the path being drawn in an SVG path element, and
//has none once a clip, a save or a restore has moved it on to a group. A canvas keeps its path across those: a vertex with
//a picture is clipped to its shape, the picture drawn and the clip restored, and its border stroked on the path the canvas
//still holds. canvas2svg had no element to stroke, logged "Attempted to apply path command to node g" and drew no border,
//on every SVG export of such a graph until 2026-11-08. A path filled, stroked or clipped with no element now gets one. Once,
//as the extension is registered (ensureSvgExtension).
function keepCanvasPathInSvg() {
    const apply = C2S.prototype.__applyCurrentDefaultPath;

    C2S.prototype.__applyCurrentDefaultPath = function () {
        if (this.__currentElement.nodeName !== 'path') {
            const path = this.__createElement('path', {}, true);

            this.__closestGroupOrSvg().appendChild(path);
            this.__currentElement = path;
        }

        apply.call(this);
    };
}

//Returns an SVG string of the current 2D graph (white background), or null. On the Map it is the
//viewport, with the earth (and graticule) as one raster image behind the vector graph.
function cytoscapeToSvg() {
    if (!cy || !ensureSvgExtension())
        return null;

    if (!geoLayerOn)
        return svgWithDetailsCards(cy.svg({ full: true, scale: 1, bg: '#ffffff' }), true, DETAILS_WHITE_LOOK);

    const container = cy.container();
    const earth = mapExportCanvas(2).toDataURL('image/jpeg', 0.9);
    const image = '<image x="0" y="0" width="' + container.clientWidth + '" height="' + container.clientHeight + '" preserveAspectRatio="none" href="' + earth + '"/>';
    const svg = svgWithDetailsCards(cy.svg({ full: false, scale: 1 }), false, pageDetailsLook());

    return svg.replace(/<svg\b[^>]*>/, function (tag) {
        return tag + image;
    });
}

//Renders the given elements through a throwaway off-screen Cytoscape instance with a real layout and
//returns an SVG string. This is the Cytoscape-native replacement for the old Graphviz render: it lays the
//graph out fresh (so it works from any view, not just the live 2D canvas) with no external engine.
function cytoscapeLayoutToSvg(elementsJson, layoutName) {
    if (typeof cytoscape !== 'function' || !ensureSvgExtension())
        return '';

    const container = document.createElement('div');
    container.style.cssText = 'position:absolute;left:-99999px;top:0;width:1200px;height:900px;';
    document.body.appendChild(container);

    let svg = '';

    try {
        const temp = cytoscape({
            container: container,
            elements: JSON.parse(elementsJson),
            style: cytoscapeStyle(true)
        });

        forgetEarlySpace(temp);

        //Detailed view: a carded vertex is its card here too, as on the canvas, and not its box (2026-11-08).
        sizeDetailsNodes(temp);

        //Preset/user-saved layouts place nodes by stored coordinates; a fresh export has none, so fall
        //back to a force layout. Force animation off so the layout settles synchronously before we snapshot.
        let name = layoutName || 'cose';
        if (name === 'userSaved' || name === 'preset')
            name = 'cose';

        const cfg = Object.assign({}, cytoscapeLayout(name), { animate: false, fit: true, padding: 30 });
        temp.layout(cfg).run();

        svg = svgWithDetailsCards(temp.svg({ full: true, scale: 1, bg: '#ffffff' }), true, DETAILS_WHITE_LOOK, temp);

        //It has the spare WebGL context too, once a WebGL instance has been made (webglContextsOf).
        const contexts = webglContextsOf(temp);

        temp.destroy();
        contexts.forEach(loseWebglContext);
    }
    catch (e) {
        console.error('Cytoscape SVG export failed:', e);
    }
    finally {
        container.remove();
    }

    return svg;
}

//Selects nodes whose label/id/property values contain the term (case-insensitive) and
//fits the view to them. Returns the match count.
function searchCytoscape(term) {
    if (!cy)
        return 0;

    cy.elements().unselect();

    const q = (term || '').trim().toLowerCase();
    if (q.length === 0)
        return 0;

    const matches = cy.nodes().filter(n => {
        const d = n.data();
        if ((d.label || '').toLowerCase().includes(q))
            return true;

        if (String(d.id || '').toLowerCase().includes(q))
            return true;

        const props = d.properties || {};
        for (const k in props)
            if (String(props[k]).toLowerCase().includes(q))
                return true;

        return false;
    });

    matches.select();
    if (matches.length > 0)
        frameCytoscapeElements(matches);

    return matches.length;
}

//Frames these elements. A lone one would fit to fill the whole viewport (blown up), so the zoom is capped to keep it readable.
function frameCytoscapeElements(matches) {
    const bb = matches.boundingBox();
    const padding = 80;
    const fitZoom = Math.min((cy.width() - 2 * padding) / bb.w, (cy.height() - 2 * padding) / bb.h);
    const targetZoom = Math.min(fitZoom, 2);
    const mx = (bb.x1 + bb.x2) / 2;
    const my = (bb.y1 + bb.y2) / 2;
    const pan = { x: cy.width() / 2 - targetZoom * mx, y: cy.height() / 2 - targetZoom * my };
    cy.animate({ zoom: targetZoom, pan: pan, duration: 400 });
}

//Selects these vertices and frames them, as a search's matches are: what an algorithm found, its path or a vertex of
//its top list. Returns how many of them are drawn.
function focusCytoscapeNodes(idsJson) {
    if (!cy)
        return 0;

    const ids = new Set(JSON.parse(idsJson || '[]').map(String));
    const matches = cy.nodes().filter(n => ids.has(String(n.id())));

    cy.elements().unselect();
    matches.select();

    if (matches.length > 0)
        frameCytoscapeElements(matches);

    return matches.length;
}

//Hides nodes whose type label (glabel) is in the hidden list; Cytoscape hides their edges with them.
function setCytoscapeLabelVisibility(hiddenJson) {
    if (!cy)
        return;

    const hidden = JSON.parse(hiddenJson);
    cy.nodes().forEach(n => {
        if (hidden.indexOf(n.data('glabel')) !== -1)
            n.style('display', 'none');
        else
            n.style('display', 'element');
    });
}

//Applies per-label color/size live by writing each node's bgColor/nodeSize data so the
//data-driven stylesheet repaints it (keeps the :selected highlight winning). stylesJson maps
//type label -> { color, size }. Display-property and icon need a full re-render instead.
function applyCytoscapeLabelStyles(stylesJson) {
    if (!cy)
        return;

    const styles = JSON.parse(stylesJson);
    cy.nodes().forEach(n => {
        const s = styles[n.data('glabel')];

        if (s && s.color)
            n.data('bgColor', s.color);
        else
            n.removeData('bgColor');

        if (s && s.size > 0)
            n.data('nodeSize', s.size);
        else
            n.removeData('nodeSize');
    });

    //The shown-properties extension boxes bake in the node color, so refresh them after a recolor.
    setupCytoscapeShowLabels();
}

//A label's 2D shape changed in Styling: its nodes take it where they are, the rest of the graph untouched, where drawing
//the graph again laid it all out anew (2026-11-05, the user). A node with a shape of its own (gdbvShape) keeps it, as the
//converter has it, and no shape is the converter's rectangle. False with no graph, for the caller to draw one.
function setCytoscapeLabelShape(label, shape) {
    if (!cy)
        return false;

    let next = 'rectangle';

    if (shape && shape.trim())
        next = shape;

    cy.nodes().filter(n => n.data('glabel') === label).forEach(n => {
        const own = (n.data('properties') || {}).gdbvShape;

        if (own && own.trim())
            return;

        n.data('shape', next);
    });

    //The shown-properties boxes stand beside each node's outline, which a new shape moves.
    setupCytoscapeShowLabels();

    return true;
}

//Takes the 2D view down and everything this file put on the page for it, as the viewer is left (Home's dispose). The
//instance has to be destroyed by hand: Cytoscape destroys itself only when its container is taken out of its own
//parent, which switching views does and leaving the page does not, and until then its renderer runs an animation
//frame loop, listens on the window and observes its container. Then the observer and frame keeping it sized, the
//overlays, the map layer and the middle-button pan's listeners. And what the page set here goes with it: its .NET
//reference, which it disposes as it goes, so a late answer such as the detailed earth arriving does not call into
//it; and its grid mode, which would otherwise snap the next page's taps while its button says off. Until 2026-10-04
//nothing called this: a viewer left in 2D kept the rest until it was next drawn in 2D, its grid mode until that was
//next switched, and the pan's window listener for good, one for every container the view had been drawn in.
function destroyCytoscapeGraph() {
    if (cy) {
        stopCurrentLayout();
        unobserveCyResize();
        teardownShowLabels();
        //The map layer is a sibling of the container, which outlives the instance — take it down while
        //the instance can still unhook its listener.
        disableGeoLayer();
        destroyCy();
    }

    detachMiddleDragPan();
    dotNetRef = null;
    gridEnabled = false;
}
