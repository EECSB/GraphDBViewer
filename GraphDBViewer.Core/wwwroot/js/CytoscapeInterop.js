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
function setCytoscapeGridMode(enabled) {
    if (!cyIsLive())
        return;

    gridEnabled = enabled;
    const container = cy.container();

    //Detach first so re-enabling (e.g. after a re-render) never stacks duplicate handlers.
    cy.off('zoom pan resize', updateGridOverlay);
    cy.off('dragfree', 'node', snapNodeToGrid);

    if (enabled) {
        cy.on('zoom pan resize', updateGridOverlay);
        cy.on('dragfree', 'node', snapNodeToGrid);
        updateGridOverlay();
    } else if (container) {
        container.style.backgroundImage = '';
    }
}

//Redraws the grid overlay so its lines fall on multiples of GRID_STEP in graph coordinates.
function updateGridOverlay() {
    if (!cyIsLive() || !gridEnabled)
        return;

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

//Snaps a dragged node to the nearest grid intersection.
function snapNodeToGrid(evt) {
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
function cytoscapeImage(format) {
    if (cy) {
        if (format === 'jpeg')
            return cy.jpg({ full: true, scale: 2, bg: '#ffffff' });
        else
            return cy.png({ full: true, scale: 2, bg: '#ffffff' });
    }

    return null;
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

//Returns an SVG string of the current 2D graph (white background), or null.
function cytoscapeToSvg() {
    if (cy && ensureSvgExtension())
        return cy.svg({ full: true, scale: 1, bg: '#ffffff' });

    return null;
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
        cy.destroy();
        cy = null;
    }
}
