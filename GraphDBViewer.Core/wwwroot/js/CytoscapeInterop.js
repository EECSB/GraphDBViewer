let cy = null;
let currentLayout = null;
//Above this many elements (nodes + edges) the "big graph" render options are switched on: they hide edges
//and render a cached bitmap while panning/zooming and drop hi-DPI supersampling, trading a little fidelity
//during interaction for speed. Below it they stay off so small graphs render crisp. Tunable.
const CY_HEAVY_THRESHOLD = 500;
//Whether the live instance was built in heavy mode — a redraw that crosses the threshold must re-create
//the instance, since these are init-time render options that can't be toggled on a live graph.
let cyHeavyMode = false;
let cyResizeObserver = null;
let cyResizeFrameId = null;
let dotNetRef = null;
let lastTapNodeId = null;
let lastTapTime = 0;
let gridEnabled = false;
const GRID_STEP = 50;//spacing (graph units) for grid-mode snapping and the grid overlay

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
            toggleRow.textContent = (viewerOpen ? '− ' : '+ ') + 'Additional Viewer Properties';
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
    btn.textContent = expanded ? '−' : '+';
    btn.title = expanded ? 'Hide properties' : 'Show all properties';

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
        return nodeAllPropLines(n).length > 0;
    });

    if (propNodes.length === 0)
        return;

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

    propNodes.forEach(function (n) {
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

    updateCytoscapeShowLabels();
}

//Pins each toggle to its node's bottom-right corner and each box just under its node, in rendered
//(screen) space; called on every cy render.
function updateCytoscapeShowLabels() {
    if (!showButtonLayer || !cyIsLive())
        return;

    //Scale the boxes with the zoom so they stay proportional to the (also-scaling) nodes.
    const z = cy.zoom();

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
        const p = n.renderedPosition();
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

function setCytoscapeDotNetRef(ref) {
    dotNetRef = ref;
}

//Stops any in-flight (possibly still-animating) layout before we mutate or tear down the graph.
//A layout animates for ~500ms after run(); if cy.destroy() lands during that window (e.g. the user
//clicks Load DB again the moment it re-enables) the old layout's next animation frame runs against a
//destroyed core and throws inside Cytoscape (endBatch -> notify on a null internal).
function stopCurrentLayout() {
    if (currentLayout) {
        try {
            currentLayout.stop();
        }
        catch { }
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
const GEO_MARKER_PROPS = 'font-size padding text-max-width width height';//what the marker scale bypasses on a node
const GEO_MARKER_EDGE_PROPS = 'font-size width arrow-scale';//and on an edge
let geoLayerOn = false;
let geoLayer = null;//the canvas behind the graph while the map is on
let geoStyle = 'satellite';//'satellite' or 'outline' — setCytoscapeMapStyle
let geoDetail = 'standard';//'standard' or 'high' — setCytoscapeMapDetail
let geoImages = {};//url → the world image at that size, once loaded
let geoDetailedFailed = false;//the detailed image's download failed; asked for again only by choosing it again
let geoOutline = null;//the coastline rings [[[lon, lat], ...], ...], once loaded
let geoLoading = {};//url → true while a fetch is in flight, so a redraw never starts a second one

function geoPosition(n) {
    const lat = n.data('geoLat');
    const lng = n.data('geoLng');

    if (typeof lat !== 'number' || typeof lng !== 'number')
        return undefined;

    return window.graphGeometry.geoProject(lat, lng, GEO_MAP_WIDTH, GEO_MAP_HEIGHT);
}

function geoLayout() {
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
//untouched. Nodes sized to their label keep that: only a fixed width/height is scaled.
function startGeoMarkerScale() {
    cy.off('zoom', applyGeoMarkerScale);
    cy.on('zoom', applyGeoMarkerScale);
    applyGeoMarkerScale();
}

function applyGeoMarkerScale() {
    if (!cy || !geoLayerOn)
        return;

    const f = 1 / cy.zoom();

    cy.batch(() => {
        cy.nodes().forEach(n => {
            let base = n.scratch('gdbvMarker');

            if (!base) {
                n.removeStyle(GEO_MARKER_PROPS);
                base = {
                    font: n.numericStyle('font-size'),
                    padding: n.numericStyle('padding'),
                    maxWidth: n.numericStyle('text-max-width'),
                    width: null,
                    height: null
                };

                if (n.style('width') !== 'label') {
                    base.width = n.numericStyle('width');
                    base.height = n.numericStyle('height');
                }

                n.scratch('gdbvMarker', base);
            }

            const style = {
                'font-size': (base.font * f) + 'px',
                'padding': (base.padding * f) + 'px',
                'text-max-width': (base.maxWidth * f) + 'px'
            };

            if (base.width !== null) {
                style.width = base.width * f;
                style.height = base.height * f;
            }

            n.style(style);
        });

        //Edges too: a route stays a line, not a band, and its arrowhead an arrowhead.
        cy.edges().forEach(e => {
            let base = e.scratch('gdbvMarker');

            if (!base) {
                e.removeStyle(GEO_MARKER_EDGE_PROPS);
                base = { font: e.numericStyle('font-size'), width: e.numericStyle('width'), arrow: e.numericStyle('arrow-scale') };
                e.scratch('gdbvMarker', base);
            }

            e.style({
                'font-size': (base.font * f) + 'px',
                'width': base.width * f,
                'arrow-scale': base.arrow * f
            });
        });
    });
}

function clearGeoMarkerScale() {
    cy.off('zoom', applyGeoMarkerScale);

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

        const bend = geo.geoEdgeBend(lat1, lng1, lat2, lng2, GEO_MAP_WIDTH, GEO_MAP_HEIGHT);

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
function getCytoscapeGeoState() {
    let step = null;

    if (geoLayerOn && gridEnabled && cy)
        step = window.graphGeometry.geoGraticuleStep(GEO_MAP_WIDTH * cy.zoom() / 360, GEO_GRATICULE_MIN_SPACING);

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
        outlineReady: !!geoOutline
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
    if (document.documentElement.getAttribute('data-bs-theme') === 'dark') {
        return {
            veil: 'rgba(10, 14, 20, 0.55)',
            coast: 'rgba(186, 198, 214, 0.75)',
            edge: 'rgba(186, 198, 214, 0.35)',
            grid: 'rgba(255, 255, 255, 0.28)',
            label: 'rgba(235, 240, 248, 0.9)',
            halo: 'rgba(10, 14, 20, 0.85)'
        };
    }

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
}

//Where the world plane lands on screen for a zoom and pan.
function geoWorldRect(zoom, pan) {
    return { x: pan.x, y: pan.y, w: GEO_MAP_WIDTH * zoom, h: GEO_MAP_HEIGHT * zoom };
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

    if (geoStyle === 'satellite' && image) {
        ctx.drawImage(image, world.x, world.y, world.w, world.h);
        ctx.fillStyle = colors.veil;
        ctx.fillRect(world.x, world.y, world.w, world.h);
    } else if (geoStyle === 'outline') {
        drawGeoOutline(ctx, world, colors);
    }

    if (graticule)
        drawGeoGraticuleLines(ctx, world, colors, width, height);

    ctx.restore();

    if (graticule)
        drawGeoGraticuleLabels(ctx, world, colors, width, height);
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
    const ringRadius = clusters.length > 1 ? Math.max(maxRadius * 2.4, (clusters.length * maxRadius * 2.4) / (2 * Math.PI)) : 0;

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
function cytoscapeStyle() {
    return [
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
    const count = Array.isArray(elements)
        ? elements.length
        : ((elements.nodes ? elements.nodes.length : 0) + (elements.edges ? elements.edges.length : 0));
    const heavy = count > CY_HEAVY_THRESHOLD;

    //Reuse a live instance on the same container instead of destroy+recreate. Spam-clicking Load DB
    //could otherwise tear an instance down while its layout animation is mid-flight, which throws
    //inside Cytoscape (endBatch -> notify on a null renderer). Reusing never destroys, so there's no race.
    //A redraw that crosses the heavy/light threshold can't reuse — the render options are fixed at
    //creation — so it falls through to the layout-stopped, race-safe re-create below.
    if (cy && cy.container() === container && container.isConnected && cyHeavyMode === heavy) {
        stopCurrentLayout();
        teardownShowLabels();
        cy.elements().remove();
        cy.add(elements);
        applyCytoscapeLayout(layoutName);
        setupCytoscapeShowLabels();
        return;
    }

    if (cy) {
        stopCurrentLayout();
        unobserveCyResize();
        teardownShowLabels();
        //The map layer is a sibling of the container, which outlives the instance — take it down while
        //the instance can still unhook its listener.
        disableGeoLayer();
        cy.destroy();
        cy = null;
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

        container.addEventListener('mousedown', (e) => {
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
        });

        //Nothing else should act on the middle click that ended a pan.
        container.addEventListener('auxclick', (e) => {
            if (e.button === 1)
                e.preventDefault();
        });

        //A drag interrupted by the window losing focus would otherwise pan forever.
        window.addEventListener('blur', stop);
    }


    cyHeavyMode = heavy;

    const cyOptions = {
        container: container,
        elements: elements,
        style: cytoscapeStyle(),
        layout: { name: 'preset' }
    };

    if (heavy) {
        //Big-graph render options (see CY_HEAVY_THRESHOLD): hide edges and draw a cached bitmap while the
        //viewport is moving, and stop supersampling on hi-DPI screens. motionBlur is already off by default.
        cyOptions.hideEdgesOnViewport = true;
        cyOptions.textureOnViewport = true;
        cyOptions.pixelRatio = 1;
    }

    cy = cytoscape(cyOptions);

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

    cy.on('tap', 'edge', function (evt) {
        const d = evt.target.data();
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnElementSelected', JSON.stringify({
                type: 'edge',
                id: d.id,
                label: d.label || '',
                source: d.source,
                target: d.target,
                properties: d.properties || {},
                selectedGroup: currentSelectionList()
            }));
        }
    });

    cy.on('tap', function (evt) {
        //A tap on empty canvas: report the click position so add-component mode can place a node there;
        //when that mode is off, .NET treats it as a deselect. In grid mode the position snaps to the grid.
        if (evt.target === cy && dotNetRef) {
            let x = evt.position.x;
            let y = evt.position.y;
            if (gridEnabled) {
                x = Math.round(x / GRID_STEP) * GRID_STEP;
                y = Math.round(y / GRID_STEP) * GRID_STEP;
            }
            dotNetRef.invokeMethodAsync('OnCanvasTapped', x, y);
        }
    });

    applyCytoscapeLayout(layoutName);

    //Keep the shown-properties extensions pinned under their nodes through pan / zoom / drag / layout.
    cy.on('render', updateCytoscapeShowLabels);
    setupCytoscapeShowLabels();

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
    } else {
        clearPlainGrid();
    }

    if (geoLayerOn)
        drawGeoLayer();
}

//Redraws the grid overlay so its lines fall on multiples of GRID_STEP in graph coordinates.
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

    const step = GRID_STEP * cy.zoom();
    const pan = cy.pan();

    container.style.backgroundImage =
        'linear-gradient(to right, rgba(128,128,128,0.3) 1px, transparent 1px),' +
        'linear-gradient(to bottom, rgba(128,128,128,0.3) 1px, transparent 1px)';
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
        x: Math.round(p.x / GRID_STEP) * GRID_STEP,
        y: Math.round(p.y / GRID_STEP) * GRID_STEP
    });
}

//Current node positions in the 2D view as JSON [{id, x, y}] — used to persist a hand-arranged layout.
function getCytoscapePositions() {
    if (!cy)
        return "[]";

    const list = cy.nodes().map(n => ({ id: n.id(), x: Math.round(n.position('x')), y: Math.round(n.position('y')) }));

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

    if (!geoLayerOn) {
        if (format === 'jpeg')
            return cy.jpg({ full: true, scale: 2, bg: '#ffffff' });
        else
            return cy.png({ full: true, scale: 2, bg: '#ffffff' });
    }

    const scale = 2;
    const canvas = mapExportCanvas(scale);
    const graph = cy.png({ full: false, scale: scale });

    return new Promise(resolve => {
        const image = new Image();

        image.onload = function () {
            const ctx = canvas.getContext('2d');
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.drawImage(image, 0, 0, canvas.width, canvas.height);

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
        svgExtRegistered = true;
        return true;
    }

    return false;
}

//Returns an SVG string of the current 2D graph (white background), or null. On the Map it is the
//viewport, with the earth (and graticule) as one raster image behind the vector graph.
function cytoscapeToSvg() {
    if (!cy || !ensureSvgExtension())
        return null;

    if (!geoLayerOn)
        return cy.svg({ full: true, scale: 1, bg: '#ffffff' });

    const container = cy.container();
    const earth = mapExportCanvas(2).toDataURL('image/jpeg', 0.9);
    const image = '<image x="0" y="0" width="' + container.clientWidth + '" height="' + container.clientHeight + '" preserveAspectRatio="none" href="' + earth + '"/>';
    const svg = cy.svg({ full: false, scale: 1 });

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
            style: cytoscapeStyle()
        });

        //Preset/user-saved layouts place nodes by stored coordinates; a fresh export has none, so fall
        //back to a force layout. Force animation off so the layout settles synchronously before we snapshot.
        let name = layoutName || 'cose';
        if (name === 'userSaved' || name === 'preset')
            name = 'cose';

        const cfg = Object.assign({}, cytoscapeLayout(name), { animate: false, fit: true, padding: 30 });
        temp.layout(cfg).run();

        svg = temp.svg({ full: true, scale: 1, bg: '#ffffff' });
        temp.destroy();
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
        for (const k in props) {
            if (String(props[k]).toLowerCase().includes(q))
                return true;
        }

        return false;
    });

    matches.select();
    if (matches.length > 0) {
        //A lone match would fit to fill the whole viewport (blown up); cap the zoom so it stays readable.
        const bb = matches.boundingBox();
        const padding = 80;
        const fitZoom = Math.min((cy.width() - 2 * padding) / bb.w, (cy.height() - 2 * padding) / bb.h);
        const targetZoom = Math.min(fitZoom, 2);
        const mx = (bb.x1 + bb.x2) / 2;
        const my = (bb.y1 + bb.y2) / 2;
        const pan = { x: cy.width() / 2 - targetZoom * mx, y: cy.height() / 2 - targetZoom * my };
        cy.animate({ zoom: targetZoom, pan: pan, duration: 400 });
    }

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

function destroyCytoscapeGraph() {
    if (cy) {
        stopCurrentLayout();
        unobserveCyResize();
        teardownShowLabels();
        //The map layer is a sibling of the container, which outlives the instance — take it down while
        //the instance can still unhook its listener.
        disableGeoLayer();
        cy.destroy();
        cy = null;
    }
}
