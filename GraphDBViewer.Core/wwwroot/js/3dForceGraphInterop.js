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

        //The Globe is a layout too — nodes pinned to a sphere by their coordinates, the earth drawn
        //beneath — and every other layout takes the sphere and the arcs away again.
        if (dagMode === 'globe') {
            this.applyGlobe(graph);
            return graph;
        }

        this.clearGlobe(graph);

        graph.dagMode(mode)
             .dagLevelDistance(60)
             .onDagError(() => { });

        return graph;
    },

    //── The Globe: the 3D view under a geographic layout ─────────────────────────────────────────
    //Located nodes (geoLat / geoLng, read from their properties by GeoCoordinates.cs) are pinned to
    //their place on a sphere, the earth is a sphere just beneath them, links between located nodes
    //bow outward over the surface, and nodes with no place are hidden with their links. The math is
    //graphGeometry.geoToCartesian / geoArc, pure and tested under Node.
    //
    //The satellite earth is the world image as a texture on the sphere; the outline earth is the
    //coastlines as line segments over a plain one. Both assets load on first use and are kept. The
    //texture works because the page has one three.js: 3d-force-graph is bundled against the same
    //module the interop builds these objects with (tools/vendor-three.mjs). It once had a private
    //copy, and a texture made by one three.js uploaded by another samples black — the globe was
    //black until the two became one.
    //
    //The image comes in the Map's two sizes, for the reason CytoscapeInterop.js gives: the standard
    //2048 × 1024 one with the globe, the detailed 8192 × 4096 one only once the tab asks for high
    //resolution (setGlobeDetail), with the standard one on the sphere until it arrives.
    GLOBE_RADIUS: 220,
    GLOBE_FRAMING: 3.4,//the camera's distance from the center, in radii, when the globe is framed whole
    globeNodeFactor: 1,//how far the nodes are scaled down as the camera closes in (applyGlobeNodeScale)
    globeNodeSize: 1,//Node size on map, as a multiple of the size the nodes keep at every height (setGlobeNodeSize)
    GLOBE_TEXTURE_WIDTH: 4096,//the detailed image is 8192 wide, for the 2D map's zoom; the globe never
    //shows more than half of it at once, and a texture that size is a fair share of a GPU
    EARTH_IMAGE_URL: '_content/GraphDBViewer.Core/img/earth-2048.jpg',
    EARTH_DETAILED_URL: '_content/GraphDBViewer.Core/img/earth-8192.jpg',
    EARTH_OUTLINE_URL: '_content/GraphDBViewer.Core/img/earth-outline.json',
    globeOn: false,
    globeStyle: 'satellite',//'satellite' or 'outline' — setGlobeStyle
    globeDetail: 'standard',//'standard' or 'high' — setGlobeDetail
    globeMesh: null,
    earthTextures: {},//url → the world image at that size as a texture, once it has loaded
    earthDetailedFailed: false,//the detailed image's download failed; asked for again only by choosing it again
    earthOutline: null,//the coastline rings [[[lon, lat], ...], ...], once fetched
    earthLoading: {},

    //The globe's own zoom (2026-11-05, the user: it was jagged). The library's controls move the camera by a share of its
    //distance from the earth's center, so near the surface one notch of the wheel leapt across a country, and each notch
    //landed at once. On the globe the wheel sets a height above the surface instead, by a share of that height, and the
    //camera eases to it over a few frames (stepGlobeZoom, from the label loop), so a turn of the wheel is one glide.
    GLOBE_ZOOM_STEP: 0.0015,//per pixel of the wheel: the height is multiplied by e to the wheel's deltaY times this
    GLOBE_MIN_HEIGHT: 0.00008,//the closest the camera comes, in radii above the surface: about 500 m, a street's height
    //(2026-11-05, the user; it was 0.02, some 130 km). What makes that close work: the nodes shrink with the height
    //(applyGlobeNodeScale), the earth rises to meet them as they do (GLOBE_SURFACE_GAP), and the camera's near plane
    //comes in with it so the ground is not cut away.
    GLOBE_SURFACE_GAP: 0.015,//how far under the nodes' sphere the earth is drawn at full node size, in radii: a node's radius,
    //so a node sits on the ground rather than half in it. It shrinks with the nodes, so near the ground the earth is
    //where the nodes are.
    globeNearBase: null,//the camera's own near plane while the globe has changed it, to put back when it goes
    GLOBE_MAX_HEIGHT: 9,//the farthest, in radii above the surface
    GLOBE_ZOOM_EASE: 0.2,//the share of the way to the height asked for that each frame covers
    globeZoomTarget: null,//the height the camera is easing to, in radii above the surface, or null at rest
    globeWheel: null,//the wheel listener while the globe is on, and the element it is on
    globeWheelOn: null,

    //The globe's drag turns it by less the lower the camera (2026-11-05, the user: close in it was too sensitive to
    //steer). The library turns it by a fixed angle per pixel, which close to the ground swept the view across a
    //continent; the turning speed goes with the height, so the ground under the pointer keeps about the same pace
    //at every height. It is the library's own speed at the framing height and above.
    globeRotateBase: null,//the controls' own rotateSpeed while the globe has changed it, to put back when it goes

    //A tile server, when one is set in Settings (MapTilesPreference.cs): the earth is drawn from its Web Mercator tiles
    //instead of the images, reprojected in strips onto equirectangular canvases (graphGeometry.geoTileStrips) that the
    //sphere wears as textures. Two of them: the whole world at a low zoom, on the sphere itself, and a patch around the
    //point the camera looks at, at the zoom the camera's distance calls for, on a piece of sphere of the same radius
    //drawn over it. The patch follows the camera, rebuilt when the look moves off it or the zoom changes; while its tiles
    //arrive it shows the world texture's part, then the nearest ancestors that have arrived, then its own.
    GLOBE_TILE_BASE_ZOOM: 2,//the world texture: 16 tiles, 1024 × 512
    GLOBE_TILE_MAX_ZOOM: 22,
    GLOBE_TILE_PATCH_MOST: 4096,//the patch canvas's longest side; past it the patch takes a lower zoom
    GLOBE_TILE_CACHE: 600,
    globeTiles: null,//{ template, key, viewport, keep } while the earth comes from a tile server: setGlobeTiles
    globeTileViewSay: null,//says the view shown while the tiles are Google's: reportGlobeTileView
    globeTileCache: new Map(),//url → { image, ready, failed }, in the order last wanted
    globeTileBase: null,//{ canvas, texture } of the whole world
    globeTilePatch: null,//{ canvas, texture, mesh, z, west, east, north, south, centerLat, centerLon, span }
    globeTileChecked: 0,//when the frame loop last asked whether the patch should move
    globeTilePaint: 0,//the timer a repaint for arrived tiles is waiting on
    globeTileReport: null,//'ready' or 'failed', last told to Home
    globeTilePatchBuilds: 0,//patches built since the server was set, for tests

    isLocated: function (node) {
        return !!node && typeof node.geoLat === 'number' && typeof node.geoLng === 'number';
    },

    //The camera's height above the globe's surface, in radii.
    globeHeight: function (graph) {
        const cam = graph.camera();

        return cam.position.length() / this.GLOBE_RADIUS - 1;
    },

    //Asks for a height, from where the camera is going if it is on its way, times factor, kept within reach.
    zoomGlobeBy: function (graph, factor) {
        let from = this.globeZoomTarget;

        if (from === null)
            from = this.globeHeight(graph);

        this.globeZoomTarget = Math.min(this.GLOBE_MAX_HEIGHT, Math.max(this.GLOBE_MIN_HEIGHT, from * factor));
    },

    //One frame of the glide: the camera covers a share of the way to the height asked for, measured in proportion, so
    //it slows as it closes in and arrives in the same few frames from any height.
    stepGlobeZoom: function () {
        if (this.globeZoomTarget === null || !this.instance)
            return;

        const cam = this.instance.camera();

        if (!cam)
            return;

        const target = this.globeZoomTarget;
        const height = Math.max(this.GLOBE_MIN_HEIGHT, this.globeHeight(this.instance));
        let next = Math.exp(Math.log(height) + (Math.log(target) - Math.log(height)) * this.GLOBE_ZOOM_EASE);

        if (Math.abs(Math.log(target / next)) < 0.002) {
            next = target;
            this.globeZoomTarget = null;
        }

        cam.position.setLength(this.GLOBE_RADIUS * (1 + next));
    },

    //Each frame: the drag's turning speed, the library's own times the height over the framing height, never above it.
    applyGlobeRotateSpeed: function () {
        if (this.globeRotateBase === null || !this.instance)
            return;

        const controls = this.instance.controls();

        if (!controls)
            return;

        const share = Math.min(1, this.globeHeight(this.instance) / (this.GLOBE_FRAMING - 1));

        controls.rotateSpeed = this.globeRotateBase * share;
    },

    enableGlobeZoom: function (graph) {
        this.disableGlobeZoom(graph);

        const controls = graph.controls();

        if (controls) {
            controls.noZoom = true;
            this.globeRotateBase = controls.rotateSpeed;
        }

        const element = graph.renderer().domElement;
        const self = this;

        this.globeWheel = function (e) {
            e.preventDefault();

            let delta = e.deltaY;

            if (e.deltaMode === 1)
                delta *= 33;//lines
            else if (e.deltaMode === 2)
                delta *= 400;//pages

            self.zoomGlobeBy(graph, Math.exp(delta * self.GLOBE_ZOOM_STEP));
        };

        this.globeWheelOn = element;
        element.addEventListener('wheel', this.globeWheel, { passive: false });
    },

    disableGlobeZoom: function (graph) {
        if (this.globeWheel && this.globeWheelOn)
            this.globeWheelOn.removeEventListener('wheel', this.globeWheel);

        this.globeWheel = null;
        this.globeWheelOn = null;
        this.globeZoomTarget = null;

        const controls = graph && graph.controls();

        if (controls) {
            controls.noZoom = false;

            if (this.globeRotateBase !== null)
                controls.rotateSpeed = this.globeRotateBase;
        }

        this.globeRotateBase = null;
    },

    //Where on the earth the globe is under a point of the window, { lat, lng }, or null where the pointer misses it.
    globePlaceAt: function (clientX, clientY) {
        const THREE = window.THREE;

        if (!THREE || !this.instance || !this.globeMesh)
            return null;

        const rect = this.instance.renderer().domElement.getBoundingClientRect();
        const pointer = new THREE.Vector2((clientX - rect.left) / rect.width * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
        const ray = new THREE.Raycaster();
        ray.setFromCamera(pointer, this.instance.camera());

        //The ray meets the sphere itself: the mesh takes no raycast of its own, so that it never stands in front of a
        //node's click.
        const mesh = this.globeMesh;
        const center = mesh.getWorldPosition(new THREE.Vector3());
        const sphere = new THREE.Sphere(center, mesh.geometry.parameters.radius * mesh.scale.x);
        const hit = ray.ray.intersectSphere(sphere, new THREE.Vector3());

        if (!hit)
            return null;

        const p = mesh.worldToLocal(hit);

        return window.graphGeometry.cartesianToGeo(p.x, p.y, p.z);
    },

    //Pins a located node to the globe's sphere, where its place on the earth is.
    pinToGlobe: function (n) {
        const p = window.graphGeometry.geoToCartesian(n.geoLat, n.geoLng, this.GLOBE_RADIUS);

        n.fx = p.x;
        n.fy = p.y;
        n.fz = p.z;
        n.x = p.x;
        n.y = p.y;
        n.z = p.z;
    },

    applyGlobe: function (graph) {
        const geo = window.graphGeometry;
        const R = this.GLOBE_RADIUS;
        const data = graph.graphData();
        const self = this;

        const byId = {};
        (data.nodes || []).forEach(n => { byId[n.id] = n; });

        //A link's ends are ids until the simulation resolves them into the node objects.
        const nodeOf = function (end) {
            if (end && typeof end === 'object')
                return end;

            return byId[end];
        };

        const place = n => geo.geoToCartesian(n.geoLat, n.geoLng, R);

        //Before the pins, not after: the library's dagMode setter clears every node's fx/fy/fz when
        //the mode goes to null (its own tidy-up when leaving a DAG layout), which is exactly what the
        //first cut of this did one line too late — the globe drew, and every node floated off it.
        graph.dagMode(null)
             .dagLevelDistance(60)
             .onDagError(() => { });

        (data.nodes || []).forEach(n => {
            if (self.isLocated(n))
                self.pinToGlobe(n);
        });

        //The flat curvature (a fan's spread, a self-loop's size — assignLinkCurves) is kept aside and
        //restored by clearGlobe. A fanned pair keeps its spread: the fan's own rotation is added to the
        //outward one, so the arcs stay apart over the surface.
        (data.links || []).forEach(l => {
            const s = nodeOf(l.source);
            const t = nodeOf(l.target);

            if (!self.isLocated(s) || !self.isLocated(t))
                return;

            if (!l.__flatCurve)
                l.__flatCurve = { curvature: l.curvature || 0, curveRotation: l.curveRotation || 0 };

            const arc = geo.geoArc(place(s), place(t), R);
            l.curvature = Math.max(l.__flatCurve.curvature, arc.curvature);
            l.curveRotation = arc.curveRotation + l.__flatCurve.curveRotation;
        });

        this.globeOn = true;
        this.refreshVisibility(graph);
        this.ensureGlobeMesh(graph);

        //Re-set so the renderer rebuilds the links with their new curves; the node objects are the same
        //ones the simulation holds, so the pins are already in force.
        graph.graphData(data);

        //A camera the whole earth fits in, straight on. The library frames a fresh graph for a cloud of
        //nodes on its next tick, so this is said again after that tick has had its say.
        const frame = function () {
            graph.cameraPosition({ x: 0, y: 0, z: R * 3.4 }, { x: 0, y: 0, z: 0 }, 0);
        };

        frame();
        setTimeout(frame, 0);

        this.enableGlobeZoom(graph);

        //The node distance's slider gives way to Node size on map here, so nothing hidden by it stays hidden.
        if (this.nodeDrawPercent < 100)
            this.restoreNodeVisibility();
    },

    //Node size on map (2026-11-06, the user): how large the nodes are drawn on the globe, as a percentage of the size they
    //keep as the camera moves (applyGlobeNodeScale), so they stay that size at every height. Their labels, images and
    //arrowheads with them (the label loop). One setting with the map's.
    setGlobeNodeSize: function (percent) {
        this.globeNodeSize = Math.max(0.1, Math.min(10, (+percent || 100) / 100));

        if (this.globeOn)
            this.applyGlobeNodeScale();
    },

    //Every other layout: the flat curves back, the sphere gone, every node visible again (the label
    //filter still applies). The pins were already cleared by applyLayout.
    clearGlobe: function (graph) {
        if (!this.globeOn)
            return;

        this.globeOn = false;
        this.disableGlobeZoom(graph);

        const data = graph.graphData();

        (data.links || []).forEach(l => {
            if (!l.__flatCurve)
                return;

            l.curvature = l.__flatCurve.curvature;
            l.curveRotation = l.__flatCurve.curveRotation;
            delete l.__flatCurve;
        });

        this.removeGlobeMesh();
        this.refreshVisibility(graph);
        this.resetGlobeNodeScale(graph);
        graph.graphData(data);
    },

    //One visibility policy for both accessors, since the library holds one of each: the label filter
    //and, on the globe, whether the node has a place. Both hide a link with a hidden end.
    refreshVisibility: function (graph) {
        const g = graph || this.instance;

        if (!g)
            return;

        const self = this;
        const hidden = this.hiddenLabelSet;

        const shown = function (node) {
            if (hidden && hidden.has(node.glabel))
                return false;

            if (self.globeOn && !self.isLocated(node))
                return false;

            return true;
        };

        g.nodeVisibility(n => shown(n));
        g.linkVisibility(l => {
            const s = l.source;
            const t = l.target;

            if (s && typeof s === 'object' && !shown(s))
                return false;

            if (t && typeof t === 'object' && !shown(t))
                return false;

            return true;
        });
    },

    //Which earth the globe draws; rebuilt in place when one is showing.
    setGlobeStyle: function (style) {
        if (style === 'outline')
            this.globeStyle = 'outline';
        else
            this.globeStyle = 'satellite';

        if (this.globeOn && this.instance)
            this.ensureGlobeMesh(this.instance);
    },

    //How sharp the satellite earth is; rebuilt in place when one is showing. `retry` is true when the user
    //chose it, which is what tries a failed download of the detailed image again — as on the map, the
    //same value told again before a draw is not.
    setGlobeDetail: function (detail, retry) {
        if (detail === 'high')
            this.globeDetail = 'high';
        else
            this.globeDetail = 'standard';

        if (retry)
            this.earthDetailedFailed = false;

        if (this.globeOn && this.instance && this.globeStyle === 'satellite')
            this.ensureGlobeMesh(this.instance);
    },

    //Where the earth comes from: null for the built-in images, or { template, key } for a tile server. Remembered for the
    //next globe, as the style is; a new server forgets the old one's tiles and textures.
    setGlobeTiles: function (source) {
        let next = null;

        if (source && source.template)
            next = { template: source.template, key: source.key || '', viewport: !!source.viewport, keep: source.keep !== false };

        if (!next && !this.globeTiles)
            return;

        //The same server with the memory switch flipped: its tiles stay, and turned off, all but those the globe shows
        //are let go of at once (trimGlobeTiles).
        if (next && this.globeTiles && next.template === this.globeTiles.template && next.key === this.globeTiles.key) {
            if (next.keep !== this.globeTiles.keep) {
                this.globeTiles.keep = next.keep;
                this.trimGlobeTilesShown();
            }

            return;
        }

        this.clearGlobeTiles();
        this.globeTiles = next;

        if (this.globeOn && this.instance)
            this.ensureGlobeMesh(this.instance);
    },

    clearGlobeTiles: function () {
        const self = this;

        this.globeTileCache.forEach(entry => self.dropGlobeTile(entry));
        this.globeTileCache = new Map();
        this.removeGlobeTilePatch();

        if (this.globeTileBase) {
            this.globeTileBase.texture.dispose();
            this.globeTileBase = null;
        }

        this.globeTileReport = null;
        this.globeTilePatchBuilds = 0;

        if (this.globeTileViewSay)
            this.globeTileViewSay.forget();
    },

    dropGlobeTile: function (entry) {
        entry.image.onload = null;
        entry.image.onerror = null;

        if (!entry.ready && !entry.failed)
            entry.image.src = '';
    },

    //The tile at a URL, asked for on first sight. Read across origins, as WebGL needs: a server that does not allow
    //it sends no tiles here, which the corner says.
    globeTileEntry: function (url) {
        let entry = this.globeTileCache.get(url);

        if (entry) {
            this.globeTileCache.delete(url);
            this.globeTileCache.set(url, entry);
            return entry;
        }

        const self = this;
        entry = { image: new Image(), ready: false, failed: false };
        entry.image.crossOrigin = 'anonymous';
        entry.image.onload = function () {
            entry.ready = true;
            self.reportGlobeTiles('ready');
            self.repaintGlobeTilesSoon();
        };
        entry.image.onerror = function () {
            entry.failed = true;

            if (self.globeTileReport !== 'ready')
                self.reportGlobeTiles('failed');
        };
        entry.image.src = url;
        this.globeTileCache.set(url, entry);

        return entry;
    },

    reportGlobeTiles: function (state) {
        if (this.globeTileReport === state)
            return;

        this.globeTileReport = state;

        if (!this.dotNetRef)
            return;

        this.dotNetRef.invokeMethodAsync('OnMapTiles', state).catch(() => { });
    },

    //Paints the tiles of `tiles` (from geoTileRange, at one zoom) that have arrived, or their nearest arrived ancestors'
    //parts, onto ctx, whose equirectangular world is `world`. Says how many were their own and how many stood in.
    paintGlobeTiles: function (ctx, world, tiles) {
        const geo = window.graphGeometry;
        const source = this.globeTiles;
        const self = this;
        let own = 0;
        let standIns = 0;

        const draw = function (image, t, u, v, part) {
            const size = image.naturalWidth || 256;
            const strips = geo.geoTileStrips(t.z, t.col, t.y, world, geo.geoTileStripCount(t.z, world.h));

            strips.forEach(st => {
                ctx.drawImage(image, u * size, (v + st.f0 * part) * size, part * size, (st.f1 - st.f0) * part * size, st.x, st.y, st.w + 0.5, st.h + 0.5);
            });
        };

        tiles.forEach(t => {
            const entry = self.globeTileEntry(geo.geoTileUrl(source.template, t.z, t.x, t.y, source.key));

            if (entry.ready) {
                draw(entry.image, t, 0, 0, 1);
                own++;
                return;
            }

            for (let k = 1; k <= t.z; k++) {
                const ax = t.x >> k;
                const ay = t.y >> k;
                const ancestor = self.globeTileCache.get(geo.geoTileUrl(source.template, t.z - k, ax, ay, source.key));

                if (ancestor && ancestor.ready) {
                    const part = 1 / (1 << k);
                    draw(ancestor.image, t, (t.x - (ax << k)) * part, (t.y - (ay << k)) * part, part);
                    standIns++;
                    return;
                }
            }
        });

        return { own: own, standIns: standIns };
    },

    //The whole world at the base zoom, on a canvas the sphere wears: the theme's ocean where Mercator ends at the poles.
    paintGlobeTileBase: function () {
        const THREE = window.THREE;
        const geo = window.graphGeometry;

        if (!this.globeTileBase) {
            const canvas = document.createElement('canvas');
            canvas.width = 256 * Math.pow(2, this.GLOBE_TILE_BASE_ZOOM);
            canvas.height = canvas.width / 2;

            const texture = new THREE.CanvasTexture(canvas);
            texture.colorSpace = THREE.SRGBColorSpace;

            if (this.instance)
                texture.anisotropy = this.instance.renderer().capabilities.getMaxAnisotropy();

            this.globeTileBase = { canvas: canvas, texture: texture, own: 0 };
        }

        const base = this.globeTileBase;
        const ctx = base.canvas.getContext('2d');
        const world = { x: 0, y: 0, w: base.canvas.width, h: base.canvas.height };

        ctx.fillStyle = this.globeOceanCss();
        ctx.fillRect(0, 0, world.w, world.h);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = 'high';

        //The base's own zoom, with zoom 0 asked for first so something stands in for it at once.
        this.globeTileEntry(geo.geoTileUrl(this.globeTiles.template, 0, 0, 0, this.globeTiles.key));
        base.own = this.paintGlobeTiles(ctx, world, geo.geoTileRange(this.GLOBE_TILE_BASE_ZOOM, -180, 180, 90, -90)).own;
        base.texture.needsUpdate = true;

        return base.texture;
    },

    globeOceanCss: function () {
        if (document.documentElement.getAttribute('data-bs-theme') === 'dark')
            return '#121a2a';
        else
            return '#e4ebf3';
    },

    //Arrived tiles are painted a moment later, all that arrived meanwhile together: the patch's canvas can be 4096 on a
    //side, and uploading it again for every tile would be a frame's whole budget each time.
    repaintGlobeTilesSoon: function () {
        if (this.globeTilePaint)
            return;

        const self = this;

        this.globeTilePaint = setTimeout(() => {
            self.globeTilePaint = 0;

            if (!self.globeTiles || !self.globeOn)
                return;

            self.paintGlobeTileBase();

            if (self.globeTilePatch)
                self.paintGlobeTilePatch();
        }, 120);
    },

    //Where the camera looks and how much of the earth a pixel covers there: the point where the ray through the middle
    //of the screen meets the sphere (or the point nearest it, looking past the edge), as latitude and longitude, and
    //the zoom whose tile pixels match the screen's at that latitude. Null when not on the globe.
    globeTileView: function () {
        const THREE = window.THREE;

        if (!this.instance || !THREE)
            return null;

        const cam = this.instance.camera();
        const renderer = this.instance.renderer();

        if (!cam || !renderer)
            return null;

        const R = this.globeDrawnRadius();
        const dir = new THREE.Vector3();
        cam.getWorldDirection(dir);

        const o = cam.position;
        const b = o.x * dir.x + o.y * dir.y + o.z * dir.z;
        const c = o.x * o.x + o.y * o.y + o.z * o.z - R * R;
        const disc = b * b - c;
        let point;
        let along;

        if (disc >= 0 && -b - Math.sqrt(disc) > 0) {
            along = -b - Math.sqrt(disc);
            point = { x: o.x + dir.x * along, y: o.y + dir.y * along, z: o.z + dir.z * along };
        } else {
            //Past the edge: the point on the sphere nearest the ray.
            const t = Math.max(0, -b);
            const near = { x: o.x + dir.x * t, y: o.y + dir.y * t, z: o.z + dir.z * t };
            const len = Math.sqrt(near.x * near.x + near.y * near.y + near.z * near.z) || 1;
            point = { x: near.x / len * R, y: near.y / len * R, z: near.z / len * R };
            along = Math.sqrt((point.x - o.x) ** 2 + (point.y - o.y) ** 2 + (point.z - o.z) ** 2);
        }

        const lat = Math.asin(Math.max(-1, Math.min(1, point.y / R))) * 180 / Math.PI;
        const lon = Math.atan2(-point.z, point.x) * 180 / Math.PI;
        const height = renderer.domElement.clientHeight || 600;
        const width = renderer.domElement.clientWidth || 800;
        const dpr = window.devicePixelRatio || 1;
        const unitsPerPixel = 2 * along * Math.tan(cam.fov * Math.PI / 360) / (height * dpr);
        const cosLat = Math.max(0.05, Math.cos(lat * Math.PI / 180));
        const z = window.graphGeometry.geoTileZoom(2 * Math.PI * R * cosLat / unitsPerPixel, this.GLOBE_TILE_MAX_ZOOM);

        //How far from that point the screen reaches, in degrees of arc, at most the horizon.
        const reach = Math.max(width, height) / 2 * dpr * unitsPerPixel / R * 180 / Math.PI;

        return { lat: lat, lon: lon, z: z, reach: Math.min(reach, 90) };
    },

    //Asked on the frame loop, a few times a second: whether the patch should be built, moved or taken away.
    updateGlobeTiles: function () {
        if (!this.globeTiles || !this.globeOn || !this.globeMesh)
            return;

        const now = performance.now();

        if (now - this.globeTileChecked < 150)
            return;

        this.globeTileChecked = now;

        const view = this.globeTileView();

        if (!view)
            return;

        const patch = this.globeTilePatch;

        if (patch && patch.wantedZ === view.z) {
            const dLat = Math.abs(view.lat - patch.centerLat);
            const dLon = Math.abs(((view.lon - patch.centerLon + 540) % 360) - 180) * Math.cos(view.lat * Math.PI / 180);

            if (Math.max(dLat, dLon) + view.reach <= patch.span * 0.9) {
                this.reportGlobeTileView(view);
                return;
            }
        }

        this.buildGlobeTilePatch(view);
        this.reportGlobeTileView(view);
    },

    //Google's copyright follows the view (2026-11-07): while the tiles are Google's, what the screen reaches around the
    //point looked at, at the zoom its tiles are drawn at there (the patch's, or the world texture's), is said to Home,
    //which asks Google for the copyright to show beside its logo (OnMapTileView).
    reportGlobeTileView: function (view) {
        if (!this.globeTiles || !this.globeTiles.viewport)
            return;

        const self = this;

        if (!this.globeTileViewSay)
            this.globeTileViewSay = window.graphGeometry.geoTileViewReporter(v => {
                if (self.dotNetRef)
                    self.dotNetRef.invokeMethodAsync('OnMapTileView', v.zoom, v.west, v.east, v.north, v.south).catch(() => { });
            }, 300);

        //The longitudes the screen reaches widen toward the poles; reaching past half the world, it is all of it.
        const lonReach = view.reach / Math.max(0.05, Math.cos(view.lat * Math.PI / 180));
        let west = -180;
        let east = 180;

        if (lonReach < 179) {
            west = view.lon - lonReach;
            east = view.lon + lonReach;

            if (west < -180)
                west += 360;

            if (east > 180)
                east -= 360;
        }

        let z = this.GLOBE_TILE_BASE_ZOOM;

        if (this.globeTilePatch)
            z = this.globeTilePatch.z;

        this.globeTileViewSay(z, west, east, view.lat + view.reach, view.lat - view.reach);
    },

    //A piece of sphere around the view's point, half again as far as the screen reaches each way, with its own
    //equirectangular canvas at the view's zoom (or lower, to keep the canvas within GLOBE_TILE_PATCH_MOST).
    buildGlobeTilePatch: function (view) {
        const THREE = window.THREE;
        const geo = window.graphGeometry;
        const span = Math.min(view.reach * 1.5, 80);
        const cosLat = Math.max(0.05, Math.cos(view.lat * Math.PI / 180));
        const north = Math.min(geo.TILE_MAX_LAT, view.lat + span);
        const south = Math.max(-geo.TILE_MAX_LAT, view.lat - span);
        //Short of the whole way round, so a patch's two edges are never the same meridian.
        const lonSpan = Math.min(358, 2 * span / cosLat);
        let west = view.lon - lonSpan / 2;

        if (west < -180)
            west += 360;

        const east = west + lonSpan;
        let z = view.z;
        let worldW = 256 * Math.pow(2, z);

        //Pixels about square at the patch's middle: a degree of latitude is 1/cos of a degree of longitude.
        while (z > this.GLOBE_TILE_BASE_ZOOM && Math.max(lonSpan / 360 * worldW, (north - south) / 180 * worldW / 2 / cosLat) > this.GLOBE_TILE_PATCH_MOST) {
            z--;
            worldW = 256 * Math.pow(2, z);
        }

        //Too far out for a patch: the world texture alone, and with the memory switch off the patch's tiles go with it.
        if (z <= this.GLOBE_TILE_BASE_ZOOM || north <= south) {
            this.removeGlobeTilePatch();

            if (this.globeTiles.keep === false)
                this.trimGlobeTiles([]);

            return;
        }

        const worldH = worldW / 2 / cosLat;
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.ceil(lonSpan / 360 * worldW));
        canvas.height = Math.max(1, Math.ceil((north - south) / 180 * worldH));

        const texture = new THREE.CanvasTexture(canvas);
        texture.colorSpace = THREE.SRGBColorSpace;

        if (this.instance)
            texture.anisotropy = this.instance.renderer().capabilities.getMaxAnisotropy();

        const phiStart = (west + 180) * Math.PI / 180;
        const thetaStart = (90 - north) * Math.PI / 180;
        const geometry = new THREE.SphereGeometry(this.GLOBE_RADIUS * 0.985, 64, 64, phiStart, lonSpan * Math.PI / 180, thetaStart, (north - south) * Math.PI / 180);

        //Drawn over the sphere it lies on: the same radius, pulled toward the camera in the depth test.
        const material = new THREE.MeshBasicMaterial({ map: texture, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -4 });
        material.color.copy(this.globeMesh.material.color);

        const mesh = new THREE.Mesh(geometry, material);
        mesh.raycast = function () { };
        mesh.userData.tilePatch = true;

        this.removeGlobeTilePatch();
        this.globeTilePatchBuilds++;
        this.globeTilePatch = {
            canvas: canvas,
            texture: texture,
            mesh: mesh,
            z: z,
            wantedZ: view.z,
            west: west,
            east: east,
            north: north,
            south: south,
            centerLat: view.lat,
            centerLon: view.lon,
            span: span,
            own: 0,
            standIns: 0,
            tiles: 0
        };
        this.globeMesh.add(mesh);
        this.paintGlobeTilePatch();
    },

    //The patch's canvas: the world texture's part of it first, then the tiles at its zoom, each its own or an
    //ancestor's part.
    paintGlobeTilePatch: function () {
        const geo = window.graphGeometry;
        const patch = this.globeTilePatch;
        const ctx = patch.canvas.getContext('2d');
        const cw = patch.canvas.width;
        const ch = patch.canvas.height;
        const lonSpan = patch.east - patch.west;

        //The equirectangular world the canvas is a window on: [west, east] × [north, south] filling it.
        const world = {
            w: cw * 360 / lonSpan,
            h: ch * 180 / (patch.north - patch.south)
        };
        world.x = -(patch.west + 180) / 360 * world.w;
        world.y = -(90 - patch.north) / 180 * world.h;

        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = 'high';

        if (this.globeTileBase) {
            const base = this.globeTileBase.canvas;
            const bx = (patch.west + 180) / 360 * base.width;
            const by = (90 - patch.north) / 180 * base.height;
            const bw = lonSpan / 360 * base.width;
            const bh = (patch.north - patch.south) / 180 * base.height;
            const firstW = Math.min(bw, base.width - bx);

            //A patch over the antimeridian takes its two halves from the two ends of the world texture.
            ctx.drawImage(base, bx, by, firstW, bh, 0, 0, firstW / bw * cw, ch);

            if (firstW < bw)
                ctx.drawImage(base, 0, by, bw - firstW, bh, firstW / bw * cw, 0, (bw - firstW) / bw * cw, ch);
        }

        let east = patch.east;

        if (east > 180)
            east -= 360;

        const tiles = geo.geoTileRange(patch.z, patch.west, east, patch.north, patch.south);
        const painted = this.paintGlobeTiles(ctx, world, tiles);

        patch.own = painted.own;
        patch.standIns = painted.standIns;
        patch.tiles = tiles.length;
        patch.texture.needsUpdate = true;
        this.trimGlobeTiles(tiles);
    },

    //What the globe shows, its patch's tiles or the world texture's alone, with all the rest let go of when the memory
    //switch is off.
    trimGlobeTilesShown: function () {
        if (this.globeTilePatch)
            this.paintGlobeTilePatch();
        else
            this.trimGlobeTiles([]);
    },

    //Past the cache's size the least recently wanted tiles go, and a tile still loading for a patch that has moved on.
    trimGlobeTiles: function (tiles) {
        const geo = window.graphGeometry;
        const source = this.globeTiles;
        const wanted = new Set(tiles.map(t => geo.geoTileUrl(source.template, t.z, t.x, t.y, source.key)));
        const baseZoom = this.GLOBE_TILE_BASE_ZOOM;
        const self = this;
        const drop = new Set();

        //The world texture's tiles and their ancestors stay.
        for (let low = 0; low <= baseZoom; low++)
            geo.geoTileRange(low, -180, 180, 90, -90).forEach(t => wanted.add(geo.geoTileUrl(source.template, t.z, t.x, t.y, source.key)));

        this.globeTileCache.forEach((entry, url) => {
            if (!wanted.has(url) && !entry.ready && !entry.failed)
                drop.add(url);
        });

        //Past the cache's size, or with the memory switch off (Map settings, 2026-11-07) past none: only the shown stay.
        let most = this.GLOBE_TILE_CACHE;

        if (source.keep === false)
            most = 0;

        let excess = this.globeTileCache.size - drop.size - most;

        for (const url of this.globeTileCache.keys()) {
            if (excess <= 0)
                break;

            if (!wanted.has(url) && !drop.has(url)) {
                drop.add(url);
                excess--;
            }
        }

        drop.forEach(url => {
            self.dropGlobeTile(self.globeTileCache.get(url));
            self.globeTileCache.delete(url);
        });
    },

    removeGlobeTilePatch: function () {
        const patch = this.globeTilePatch;

        if (!patch)
            return;

        if (patch.mesh.parent)
            patch.mesh.parent.remove(patch.mesh);

        patch.mesh.geometry.dispose();
        patch.mesh.material.dispose();
        patch.texture.dispose();
        this.globeTilePatch = null;
    },

    //What the tiles on the globe are doing, for tests: the world texture's own tiles, and the patch's zoom and tiles.
    getGlobeTileState: function () {
        const patch = this.globeTilePatch;
        const state = { on: !!this.globeTiles, baseOwn: 0, patchZoom: null, patchOwn: 0, patchStandIns: 0, patchTiles: 0, patchBuilds: this.globeTilePatchBuilds };

        if (this.globeTileBase)
            state.baseOwn = this.globeTileBase.own;

        if (patch) {
            state.patchZoom = patch.z;
            state.patchOwn = patch.own;
            state.patchStandIns = patch.standIns;
            state.patchTiles = patch.tiles;
        }

        return state;
    },

    ensureGlobeMesh: function (graph) {
        const THREE = window.THREE;

        if (!THREE)
            return;

        this.removeGlobeMesh();

        const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark';
        //Just under the node radius, so nodes sit on the surface rather than half inside it.
        const radius = this.GLOBE_RADIUS * 0.985;
        let ocean;
        let coast;
        let tint;

        if (dark) {
            ocean = 0x121a2a;
            coast = 0xb4c0d0;
            tint = 0x8c96a3;
        } else {
            ocean = 0xe4ebf3;
            coast = 0x3a4658;
            tint = 0xffffff;
        }

        let mesh;

        //Unlit on purpose: a data view has no night side.
        if (this.globeTiles) {
            const material = new THREE.MeshBasicMaterial({ color: tint });
            material.map = this.paintGlobeTileBase();
            mesh = new THREE.Mesh(new THREE.SphereGeometry(radius, 128, 64), material);
        } else if (this.globeStyle === 'outline') {
            mesh = new THREE.Mesh(new THREE.SphereGeometry(radius, 96, 48), new THREE.MeshBasicMaterial({ color: ocean }));

            if (this.earthOutline)
                mesh.add(this.buildGlobeLines(THREE, this.earthOutline, coast));
            else
                this.loadEarthOutline();
        } else {
            const geometry = new THREE.SphereGeometry(radius, 128, 64);
            const material = new THREE.MeshBasicMaterial({ color: ocean });
            const texture = this.currentEarthTexture();

            if (texture) {
                material.map = texture;
                material.color.setHex(tint);
            }

            this.loadEarthTextures();
            mesh = new THREE.Mesh(geometry, material);
        }

        //Not a thing to click or hover: the picker walks the whole scene, and a hit here would swallow
        //the deselect a click on empty space means.
        mesh.raycast = function () { };
        mesh.userData.style = this.globeStyle;

        this.globeMesh = mesh;
        graph.scene().add(mesh);
    },

    removeGlobeMesh: function () {
        if (!this.globeMesh)
            return;

        this.removeGlobeTilePatch();

        const mesh = this.globeMesh;

        if (mesh.parent)
            mesh.parent.remove(mesh);

        mesh.children.slice().forEach(child => {
            mesh.remove(child);
            child.geometry.dispose();
            child.material.dispose();
        });

        mesh.geometry.dispose();
        mesh.material.dispose();
        this.globeMesh = null;
    },

    //The texture to draw: the detailed one when the tab wants it and it has arrived, else the standard one,
    //which is also what shows while the detailed one is on its way or if it never comes.
    currentEarthTexture: function () {
        if (this.globeDetail === 'high' && this.earthTextures[this.EARTH_DETAILED_URL])
            return this.earthTextures[this.EARTH_DETAILED_URL];

        return this.earthTextures[this.EARTH_IMAGE_URL] || null;
    },

    //What the satellite earth needs and does not have yet: the standard image always, the detailed one as
    //well when the tab wants it and its download has not failed.
    loadEarthTextures: function () {
        this.loadEarthTexture(this.EARTH_IMAGE_URL);

        if (this.globeDetail === 'high' && !this.earthDetailedFailed)
            this.loadEarthTexture(this.EARTH_DETAILED_URL);
    },

    //The image is the same equirectangular one the 2D map draws, no wider than GLOBE_TEXTURE_WIDTH for the
    //GPU. Loaded once per size; when it arrives the globe that asked for it is rebuilt with it on. A new
    //renderer uploads the kept texture again on its own. The detailed one tells Home how its download is
    //going, as it does from the map.
    loadEarthTexture: function (url) {
        if (this.earthTextures[url] || this.earthLoading[url])
            return;

        const self = this;
        const detailed = url === this.EARTH_DETAILED_URL;
        this.earthLoading[url] = true;

        if (detailed)
            this.reportDetailedEarth('loading');

        const image = new Image();
        image.onload = function () {
            const THREE = window.THREE;
            const width = Math.min(image.naturalWidth, self.GLOBE_TEXTURE_WIDTH);
            const canvas = document.createElement('canvas');
            canvas.width = width;
            canvas.height = width / 2;

            const ctx = canvas.getContext('2d');
            ctx.imageSmoothingEnabled = true;
            ctx.imageSmoothingQuality = 'high';
            ctx.drawImage(image, 0, 0, width, width / 2);

            const texture = new THREE.CanvasTexture(canvas);
            texture.colorSpace = THREE.SRGBColorSpace;

            //Sharper where the sphere turns away from the camera; the renderer says how far it can go.
            if (self.instance)
                texture.anisotropy = self.instance.renderer().capabilities.getMaxAnisotropy();

            self.earthTextures[url] = texture;
            self.earthLoading[url] = false;

            if (detailed)
                self.reportDetailedEarth('ready');

            if (self.globeOn && self.instance && self.globeStyle === 'satellite')
                self.ensureGlobeMesh(self.instance);
        };
        image.onerror = function () {
            self.earthLoading[url] = false;

            if (detailed) {
                self.earthDetailedFailed = true;
                self.reportDetailedEarth('failed');
            }
        };
        image.src = url;
    },

    //'loading', 'ready' or 'failed', to Home. By the time 3.7 MB arrives the page may have moved on and
    //disposed the reference, which rejects rather than throws.
    reportDetailedEarth: function (state) {
        if (!this.dotNetRef)
            return;

        this.dotNetRef.invokeMethodAsync('OnDetailedEarth', state).catch(() => { });
    },

    loadEarthOutline: function () {
        if (this.earthLoading[this.EARTH_OUTLINE_URL])
            return;

        const self = this;
        this.earthLoading[this.EARTH_OUTLINE_URL] = true;

        fetch(this.EARTH_OUTLINE_URL)
            .then(r => r.json())
            .then(rings => {
                self.earthOutline = rings;
                self.earthLoading[self.EARTH_OUTLINE_URL] = false;

                if (self.globeOn && self.instance && self.globeStyle === 'outline')
                    self.ensureGlobeMesh(self.instance);
            })
            .catch(() => {
                self.earthLoading[self.EARTH_OUTLINE_URL] = false;
            });
    },

    //Coastlines and borders as line segments just above the plain sphere, so they neither z-fight
    //with it nor show through from the far side.
    buildGlobeLines: function (THREE, rings, color) {
        const geo = window.graphGeometry;
        const r = this.GLOBE_RADIUS * 0.992;
        const positions = [];

        rings.forEach(ring => {
            for (let i = 1; i < ring.length; i++) {
                const a = geo.geoToCartesian(ring[i - 1][1], ring[i - 1][0], r);
                const b = geo.geoToCartesian(ring[i][1], ring[i][0], r);
                positions.push(a.x, a.y, a.z, b.x, b.y, b.z);
            }
        });

        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));

        const lines = new THREE.LineSegments(geometry, new THREE.LineBasicMaterial({ color: color }));
        lines.raycast = function () { };

        return lines;
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

    //How far a fanned edge bows away from the straight line between its two nodes, as a fraction of that
    //line's length. Big enough to separate the arcs at a glance; small enough that a pair does not swing
    //out past the nodes it joins.
    FAN_CURVATURE: 0.28,

    //A self-loop has no length to bow across, so its curvature is what gives it any size at all. Each
    //further loop on the same node gets a wider one, or they would all draw on top of each other.
    SELF_LOOP_CURVATURE: 0.6,
    SELF_LOOP_STEP: 0.35,

    //Gives every link the curvature and curve rotation it will be drawn with, once, from the whole set
    //of links -- which is the only place the answer is knowable, since it depends on how many other
    //links join the same two nodes.
    //
    //It replaced a linkCurvature accessor that counted links as the renderer asked about them. That
    //could not work: the renderer asks again on every refresh, the count carried over between passes,
    //and the value was clamped, so after a few refreshes every parallel edge returned the same clamped
    //curvature and N edges drew as one arc. Seven edges between two nodes rendered as two.
    //
    //Grouping is undirected on purpose. A->B and B->A are one fan, not two: counted separately they
    //each start at the same offset and draw in the same place, which is the other half of why a
    //reciprocal pair looked like a single edge.
    assignLinkCurves: function (links) {
        if (!Array.isArray(links))
            return;

        const idOf = end => {
            if (end && typeof end === 'object')
                return String(end.id);

            return String(end);
        };

        const groups = new Map();

        links.forEach(link => {
            const a = idOf(link.source);
            const b = idOf(link.target);
            let key;

            //Sorted, so the pair keys the same whichever way the edge points. JSON rather than joining
            //on a separator character: ids come from whatever database is connected and are free to
            //contain any character that might have been picked as one, which would put two unrelated
            //pairs in the same fan.
            if (a <= b)
                key = JSON.stringify([a, b]);
            else
                key = JSON.stringify([b, a]);

            if (!groups.has(key))
                groups.set(key, []);

            groups.get(key).push(link);
        });

        groups.forEach(group => {
            const n = group.length;

            group.forEach((link, i) => {
                const from = idOf(link.source);
                const to = idOf(link.target);

                if (from === to) {
                    link.curvature = this.SELF_LOOP_CURVATURE + i * this.SELF_LOOP_STEP;
                    link.curveRotation = (2 * Math.PI * i) / n;

                    return;
                }

                if (n === 1) {
                    //The ordinary case, and the one worth keeping straight: a single edge between two
                    //nodes is a line, not an arc.
                    link.curvature = 0;
                    link.curveRotation = 0;

                    return;
                }

                //Every edge in a fan bows by the same amount and they are spread evenly around the axis
                //joining the two nodes. Spreading by rotation rather than by curvature is what makes
                //this read as N edges from any camera angle: curvature alone fans them within a single
                //plane, which vanishes to one line as soon as you look along it.
                link.curvature = this.FAN_CURVATURE;

                //The rotation frame is built from the source-to-target axis, so a link running the other
                //way has a mirrored frame and the same angle would put it back on top of its opposite
                //number. Negating cancels the mirror, so the angle means the same place in space for
                //every link in the fan regardless of which way it points.
                const angle = (2 * Math.PI * i) / n;

                if (from <= to)
                    link.curveRotation = angle;
                else
                    link.curveRotation = -angle;
            });
        });
    },

    drawGraph: function (containerId, graphDataJson, dagMode) {
        const container = document.getElementById(containerId);
        if (!container) {
            console.error("3D graph container not found:", containerId);
            return;
        }

        const graphData = JSON.parse(graphDataJson);
        this.assignLinkCurves(graphData.links);

        //A fresh draw is a fresh scene: the previous globe, if any, goes with the previous instance, its wheel listener
        //with its canvas.
        this.removeGlobeMesh();
        this.globeOn = false;
        this.globeWheel = null;
        this.globeWheelOn = null;
        this.globeZoomTarget = null;

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
            .nodeLabel(node => {
                if (self.showNodeLabels)
                    return '';
                else
                    return node.label || node.id;
            })
            .linkLabel(link => {
                if (self.showLinkLabels)
                    return '';
                else
                    return link.label || '';
            })
            .linkColor(link => link.color || '#999999')
            .linkDirectionalArrowLength(() => self.arrowLength())
            .linkDirectionalArrowRelPos(link => self.arrowRelPos(link))
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
            .onBackgroundRightClick(event => {
                //The page's menu for the canvas; the 3D force layout places a new component (2026-11-05, the user), and on
                //the globe the place on the earth under the pointer does.
                if (!self.dotNetRef)
                    return;

                if (self.globeOn) {
                    const place = self.globePlaceAt(event.clientX, event.clientY);

                    self.dotNetRef.invokeMethodAsync('OnEarthContextMenu', event.clientX, event.clientY, place && place.lat, place && place.lng);
                }
                else
                    self.dotNetRef.invokeMethodAsync('OnCanvasContextMenu', event.clientX, event.clientY, null, null);
            })
            .onBackgroundClick(event => {
                //Empty-space click: add-component mode drops a new node here (the 3D force layout
                //positions it, the globe by the place on the earth under the click); otherwise .NET treats it as a deselect.
                if (!self.dotNetRef)
                    return;

                if (self.globeOn) {
                    const place = self.globePlaceAt(event.clientX, event.clientY);

                    self.dotNetRef.invokeMethodAsync('OnEarthTapped', place && place.lat, place && place.lng);
                }
                else
                    self.dotNetRef.invokeMethodAsync('OnCanvasTapped3d');
            })
            //A right-click on a vertex or a link opens the page's menu for it where the pointer is, as on the 2D
            //canvas (2026-11-04, the user).
            .onNodeRightClick((node, event) => {
                if (!self.dotNetRef)
                    return;

                //What a click on it sends, for the menu's Style, which selects it first.
                const selection = { type: 'node', id: node.id, label: node.label || '', glabel: node.glabel || '', properties: node.properties || {} };

                self.dotNetRef.invokeMethodAsync('OnElementContextMenu', 'node', String(node.id), String(node.label || node.id), event.clientX, event.clientY, JSON.stringify(selection), null);
            })
            .onLinkRightClick((link, event) => {
                if (!self.dotNetRef)
                    return;

                let srcId = link.source;
                if (typeof link.source === 'object')
                    srcId = link.source.id;

                let tgtId = link.target;
                if (typeof link.target === 'object')
                    tgtId = link.target.id;

                const id = link.id || (srcId + '->' + tgtId);
                const selection = { type: 'edge', id: id, label: link.label || '', source: srcId, target: tgtId, properties: link.properties || {} };

                self.dotNetRef.invokeMethodAsync('OnElementContextMenu', 'edge', String(id), String(link.label || id), event.clientX, event.clientY, JSON.stringify(selection), null);
            });

        //The browser's own menu never opens over the 3D view.
        if (!container.gdbvMenuHooked) {
            container.gdbvMenuHooked = true;
            container.addEventListener('contextmenu', e => e.preventDefault());
        }

        //Both read a value assignLinkCurves has already put on the link. They are deliberately property
        //names rather than functions: an accessor that counted as it went would be called again on every
        //refresh, and anything it accumulated would keep accumulating.
        graph.linkCurvature('curvature');
        graph.linkCurveRotation('curveRotation');

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

        //Recomputed over the whole set, not just what arrived: an expansion that adds a second edge
        //between two nodes changes the curve of the one already drawn.
        this.assignLinkCurves(cur.links);

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

        const displayKeys = ['label', 'group', 'glabel', 'properties', 'bgColor', 'nodeSize', 'shape', 'shape3d', 'image', 'model', 'showProps', 'details'];
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
            }
            else {
                //On the globe a new node with a place on the earth goes there, as every other does.
                if (this.globeOn && this.isLocated(inc))
                    this.pinToGlobe(inc);

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
                existing.details = inc.details;

                //An algorithm's path is drawn in the links' colors, so a reconcile carries them too.
                existing.color = inc.color;
                links.push(existing);
            }
            else
                links.push(inc);
        });

        //Did the set of nodes / links change (add or remove)? Check both counts (catches removals) and
        //that every incoming element already existed (catches additions, incl. a simultaneous add+remove).
        const sameNodeCount = nodes.length === cur.nodes.length;
        const nodesAllKnown = incoming.nodes.every(n => curNodeById[n.id] !== undefined);
        const sameLinkCount = links.length === (cur.links || []).length;
        const curLinkKeys = new Set((cur.links || []).map(linkKey));
        const linksAllKnown = incoming.links.every(l => curLinkKeys.has(linkKey(l)));
        const topologyChanged = !sameNodeCount || !nodesAllKnown || !sameLinkCount || !linksAllKnown;

        //Only the topology can change a fan, so recomputing rides with the branch that re-sets the data.
        if (topologyChanged) {
            this.assignLinkCurves(links);
            this.instance.graphData({ nodes: nodes, links: links });
        }
        else if (positionChanged)
            this.instance.d3ReheatSimulation();

        //Repaint restyled nodes and links and rebuild the label / image / model overlays.
        this.instance.nodeColor(this.instance.nodeColor());
        this.instance.nodeVal(this.instance.nodeVal());
        this.instance.linkColor(this.instance.linkColor());
        this.setupLabels(this.container);
        this.setupCustomNodes();
    },

    centerGraph: function () {
        if (this.instance)
            this.instance.zoomToFit(400, 50);
    },

    //Frames these vertices, as the 2D view's focus does: what an algorithm found. 3D has no selection, so it frames
    //them only. Returns how many of them are drawn.
    focusNodes: function (idsJson) {
        if (!this.instance)
            return 0;

        const ids = new Set(JSON.parse(idsJson || '[]').map(String));
        const count = this.instance.graphData().nodes.filter(n => ids.has(String(n.id))).length;

        if (count > 0)
            this.instance.zoomToFit(400, 50, n => ids.has(String(n.id)));

        return count;
    },

    //Dollies the 3D camera toward/away from the center. factor < 1 zooms in, > 1 zooms out.
    zoom: function (factor) {
        //On the globe, the height above the surface, gliding, so a press never takes the camera into the earth.
        if (this.instance && this.globeOn) {
            this.zoomGlobeBy(this.instance, factor);
            return;
        }

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
                model = await this.loadModel(modelUrl, modelCache);

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
        else if (fmt === 'glb')
            //Binary, so it crosses to .NET as base64, which exportInterop.downloadBase64 turns back into bytes.
            return graphGeometry.bytesToBase64(graphGeometry.meshToGlb(mesh));
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

    //Fetches and parses an external model, caching by URL: an .obj, a .gltf, a .glb, or a .zip holding one. A .gltf's
    //buffers that are files of their own are fetched from beside it. Returns the parsed model ({ verts, faces }) or
    //null on any failure (network, CORS, empty, parse).
    loadModel: async function (url, cache) {
        if (cache[url] !== undefined)
            return cache[url];

        let parsed = null;
        const fetchUrl = this.normalizeModelUrl(url);
        const beside = async uri => {
            let target;

            try {
                target = new URL(uri, new URL(fetchUrl, document.baseURI)).href;
            }
            catch (e) {
                return null;
            }

            const resp = await fetch(target);

            if (!resp.ok)
                return null;

            return new Uint8Array(await resp.arrayBuffer());
        };

        try
        {
            const resp = await fetch(fetchUrl);
            if (resp.ok) {
                const buf = await resp.arrayBuffer();
                parsed = await graphGeometry.parseModelData(buf, beside);
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

        this.setupLinkCards(data.links || []);

        //A carded node (Detailed view) is its card, whatever its shape or model.
        const objById = this.detailsCardObjects(data.nodes);
        const custom = data.nodes.filter(n => !objById[n.id] && this.nodeCustomKind(n) !== 'sphere');

        //The others with a shape or a model of their own get it, once three.js can be captured from a default node mesh;
        //without it they stay spheres. None, and nothing is waited for.
        if (custom.length > 0) {
            let three = null;
            for (let attempt = 0; attempt < 12 && !three; attempt++) {
                await new Promise(resolve => requestAnimationFrame(resolve));
                three = this.captureThree();
            }

            if (three) {
                let baseR = 4;
                if (typeof this.instance.nodeRelSize === 'function')
                    baseR = this.instance.nodeRelSize();

                const cache = {};

                for (const node of custom) {
                    const kind = this.nodeCustomKind(node);
                    const color = node.bgColor || node.color;
                    let mesh = null;

                    if (kind === 'model') {
                        const url = node.model;
                        const parsed = await this.loadModel(url, cache);
                        if (parsed)
                            mesh = this.buildModelMesh(parsed, three, baseR, color);
                    }
                    else
                        mesh = this.buildShapeMesh(kind, three, this.nodeRadius(node, baseR), color);

                    if (mesh)
                        objById[node.id] = mesh;
                }
            }
        }

        this.useNodeObjects(objById);
    },

    //Hands the library the objects to draw nodes as, by id, a node not among them being its default sphere; it lets go of
    //the ones it drew before, their pictures with them. With none to hand over and none handed over before, the library is
    //left as it is; with none now but some before (Detailed view turned off by a sync), it is handed the empty set, or it
    //would keep drawing the old ones.
    useNodeObjects: function (objById) {
        const before = this.modelObjById || {};

        if (Object.keys(objById).length === 0 && Object.keys(before).length === 0)
            return;

        this.modelObjById = objById;

        const self = this;
        this.instance.nodeThreeObjectExtend(false);
        this.instance.nodeThreeObject(node => {
            return self.modelObjById[node.id];//undefined => library keeps the default sphere
        });
    },

    //── Detailed view in 3D ───────────────────────────────────────────────────────────────────────────────────────────
    //A carded node (Show details on canvas, 2026-11-06) is drawn in 3D as its card, a picture that always faces the camera,
    //in place of its sphere, solid or model (2026-11-07, the user's "do the loose ends"), as the 2D canvas draws it in place
    //of its box. graphGeometry lays the card out as the 2D canvas's HTML card is, in the page's colors, and it is painted
    //at DETAILS_CARD_SHARPNESS times its size so it stays sharp up close. Its name, image and shown properties are on the
    //card, so the labels' layer leaves them out (setupLabels).
    DETAILS_CARD_SHARPNESS: 3,
    DETAILS_CARD_WORLD: 0.2,//world units a card pixel takes: a 2D box of 40 pixels is a sphere 8 across

    //The cards of the carded nodes, by id: three.js sprites, each painted with its card.
    detailsCardObjects: function (nodes) {
        const objects = {};

        nodes.filter(n => n.details === true).forEach(node => {
            const content = window.graphGeometry.detailsCardContent({ kind: node.glabel || 'vertex', id: node.id, label: node.label, properties: node.properties });
            const sprite = this.detailsCardSprite(content, node.bgColor || '#0d6efd');

            if (sprite)
                objects[node.id] = sprite;
        });

        return objects;
    },

    //A card as a sprite: laid out by graphGeometry in the page's colors, painted DETAILS_CARD_SHARPNESS times its size, and
    //DETAILS_CARD_WORLD world units a card pixel. Null without three.js.
    detailsCardSprite: function (content, color) {
        const THREE = window.THREE;
        const geo = window.graphGeometry;

        if (!THREE)
            return null;

        if (!this.detailsMeasureCtx)
            this.detailsMeasureCtx = document.createElement('canvas').getContext('2d');

        const measureCtx = this.detailsMeasureCtx;
        const measure = function (text, font) {
            measureCtx.font = font;

            return measureCtx.measureText(text).width;
        };
        const style = getComputedStyle(document.documentElement);
        const look = {
            color: color,
            selected: false,
            bg: style.getPropertyValue('--bs-body-bg').trim() || '#ffffff',
            fg: style.getPropertyValue('--bs-body-color').trim() || '#212529',
            muted: style.getPropertyValue('--bs-secondary-color').trim() || 'rgba(33, 37, 41, 0.75)',
            line: style.getPropertyValue('--bs-border-color-translucent').trim() || 'rgba(0, 0, 0, 0.175)',
            family: getComputedStyle(document.body).fontFamily
        };
        const layout = geo.detailsCardLayout(content, look, measure);
        const sharp = this.DETAILS_CARD_SHARPNESS;
        const canvas = document.createElement('canvas');

        canvas.width = Math.ceil(layout.width * sharp);
        canvas.height = Math.ceil(layout.height * sharp);
        geo.paintDetailsCard(canvas.getContext('2d'), layout, canvas.width / 2, canvas.height / 2, sharp);

        const texture = new THREE.CanvasTexture(canvas);
        texture.colorSpace = THREE.SRGBColorSpace;

        const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: texture, transparent: true }));
        sprite.scale.set(layout.width * this.DETAILS_CARD_WORLD, layout.height * this.DETAILS_CARD_WORLD, 1);
        sprite.userData.detailsCard = { width: layout.width, height: layout.height, title: content.title, kind: content.kind };

        return sprite;
    },

    //A carded link (Detailed view on edges, 2026-11-07) has its card at its middle, beside its line, where the labels'
    //layer would say its label; on the globe lifted to just over the surface, as that label is, where the arc runs. With
    //none now and none before, the library is left as it is.
    setupLinkCards: function (links) {
        const before = this.linkCards || new Map();
        const carded = links.filter(l => l.details === true);

        this.linkCards = new Map();

        if (carded.length === 0 && before.size === 0)
            return;

        carded.forEach(link => {
            const content = window.graphGeometry.detailsCardContent({ kind: this.linkEndId(link.source) + ' → ' + this.linkEndId(link.target), id: '', label: link.label, properties: link.properties });
            content.id = null;

            const sprite = this.detailsCardSprite(content, link.color || '#6c757d');

            if (sprite)
                this.linkCards.set(link, sprite);
        });

        const self = this;
        this.instance.linkThreeObjectExtend(true);
        this.instance.linkThreeObject(link => self.linkCards.get(link));
        this.instance.linkPositionUpdate((obj, ends, link) => {
            if (!obj || self.linkCards.get(link) !== obj)
                return false;

            let mx = (ends.start.x + ends.end.x) / 2;
            let my = (ends.start.y + ends.end.y) / 2;
            let mz = (ends.start.z + ends.end.z) / 2;

            if (self.globeOn) {
                const chord = Math.sqrt(mx * mx + my * my + mz * mz);

                if (chord > 0) {
                    const lift = self.GLOBE_RADIUS * 1.02 / chord;
                    mx *= lift;
                    my *= lift;
                    mz *= lift;
                }
            }

            obj.position.set(mx, my, mz);

            return false;
        });
    },

    //A link's end's id: the library swaps the id for the node once the graph is laid out.
    linkEndId: function (end) {
        if (end && typeof end === 'object')
            return String(end.id);

        return String(end);
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

    //On the globe a node keeps its apparent size as the camera closes in — the user's call,
    //2026-09-02: zoomed in on a country, the plain view's spheres are balls the size of the country.
    //Every frame (the label loop) the node objects are scaled in place by the camera's height over
    //the framing height, never above 1, so at the framing the globe shows the plain view's sizes and
    //nothing is rebuilt. An object's own scale (a fitted model) is kept as the base. By the height rather than the distance
    //from the earth's center since 2026-11-05, when the camera came down to street level: the center's distance never
    //falls below a radius, and the nodes stayed the size of a county there. The earth's drawn radius and the camera's near
    //plane follow in the same frame (applyGlobeSurface).
    applyGlobeNodeScale: function () {
        if (!this.globeOn || !this.instance)
            return;

        const cam = this.instance.camera();

        if (!cam)
            return;

        const height = Math.max(0, this.globeHeight(this.instance));
        const factor = Math.max(1e-6, Math.min(1, height / (this.GLOBE_FRAMING - 1))) * this.globeNodeSize;

        this.globeNodeFactor = factor;
        this.scaleNodeObjects(this.instance.graphData().nodes, factor);
        this.applyGlobeSurface(cam, height);
    },

    //── A place found ─────────────────────────────────────────────────────────────────────────
    //The search under the layers button (2026-11-06, the user): an address or a name looked up, the camera flown there and
    //a pin stood on it. The pin is drawn over the canvas, the labels' way, so it is the same size at any height, and
    //hidden while the earth is in front of it.
    GLOBE_PLACE_SPAN: 0.02,//degrees across the view for a place the geocoder gave no size, a few streets
    GLOBE_PLACE_SPAN_LEAST: 0.004,//and the fewest for any place, a house's box being smaller than a street
    GLOBE_FLY_MS: 1400,
    globePlace: null,//{ lat, lng } while a place is shown: showGlobePlace
    globePlacePin: null,//its element, in the labels' layer
    globeFly: null,//{ from, to, angle, h0, h1, start } while the camera is on its way there

    //Flies the camera to look straight down on a place, from the height its size calls for, and pins it. Kept when the
    //globe is not up, for the next one.
    showGlobePlace: function (lat, lng, span) {
        this.globePlace = { lat: lat, lng: lng };

        if (!this.instance || !this.globeOn)
            return;

        const cam = this.instance.camera();

        if (!cam)
            return;

        let degrees = this.GLOBE_PLACE_SPAN;

        if (typeof span === 'number' && span > 0)
            degrees = span * 1.5;

        degrees = Math.min(180, Math.max(this.GLOBE_PLACE_SPAN_LEAST, degrees));

        //The ground across the view's shorter side, at a height h in radii, is about 2·h·tan(half the field of view) radii,
        //and an arc of d degrees is d·π/180 of them.
        const half = Math.tan(cam.fov * Math.PI / 360) * Math.min(1, cam.aspect);
        const height = Math.min(this.GLOBE_FRAMING - 1, Math.max(this.GLOBE_MIN_HEIGHT, degrees * Math.PI / 180 / (2 * half)));
        const p = cam.position;
        const length = p.length();
        const from = { x: p.x / length, y: p.y / length, z: p.z / length };
        const to = window.graphGeometry.geoToCartesian(lat, lng, 1);
        const dot = Math.min(1, Math.max(-1, from.x * to.x + from.y * to.y + from.z * to.z));

        this.globeZoomTarget = null;
        this.globeFly = { from: from, to: to, angle: Math.acos(dot), h0: this.globeHeight(this.instance), h1: height, start: performance.now() };
    },

    //One frame of the flight: along the great circle from where the camera looked to the place, easing in and out, the
    //height going from the one to the other by proportion, and lifted on the way over by as much of the earth as it
    //crosses, so a long flight rises to show where it is going and never cuts through the earth.
    stepGlobeFly: function () {
        const fly = this.globeFly;

        if (!fly || !this.instance)
            return;

        const cam = this.instance.camera();

        if (!cam)
            return;

        const t = Math.min(1, (performance.now() - fly.start) / this.GLOBE_FLY_MS);
        let e;

        if (t < 0.5)
            e = 2 * t * t;
        else
            e = 1 - 2 * (1 - t) * (1 - t);

        let a = 1 - e;
        let b = e;

        if (fly.angle > 1e-6) {
            a = Math.sin((1 - e) * fly.angle) / Math.sin(fly.angle);
            b = Math.sin(e * fly.angle) / Math.sin(fly.angle);
        }

        const dir = {
            x: fly.from.x * a + fly.to.x * b,
            y: fly.from.y * a + fly.to.y * b,
            z: fly.from.z * a + fly.to.z * b
        };
        const length = Math.hypot(dir.x, dir.y, dir.z) || 1;
        const lift = Math.max(0, fly.angle * 0.6 - Math.max(fly.h0, fly.h1)) * Math.sin(Math.PI * e);
        const height = Math.exp(Math.log(fly.h0) + (Math.log(fly.h1) - Math.log(fly.h0)) * e) + lift;
        const r = this.GLOBE_RADIUS * (1 + height) / length;

        cam.position.set(dir.x * r, dir.y * r, dir.z * r);
        cam.lookAt(0, 0, 0);

        if (t >= 1)
            this.globeFly = null;
    },

    //The pin over the place, its point on it, in the labels' layer; out of sight while the earth is in front of it.
    placeGlobePin: function (camera, drawnRadius) {
        if (!this.globePlace || !this.globeOn || !this.labelLayer || !camera) {
            if (this.globePlacePin)
                this.globePlacePin.style.display = 'none';

            return;
        }

        let pin = this.globePlacePin;

        if (!pin) {
            pin = document.createElement('div');
            pin.className = 'gdbv-place-pin';
            pin.setAttribute('data-testid', 'globe-place-pin');
            pin.innerHTML = '<svg width="22" height="31" viewBox="0 0 22 31" aria-hidden="true"><path d="M11 30C10 23 2 18 2 11a9 9 0 0 1 18 0c0 7-8 12-9 19z" fill="#dc3545" stroke="#ffffff" stroke-width="2"/><circle cx="11" cy="11" r="3.5" fill="#ffffff"/></svg>';
            this.globePlacePin = pin;
        }

        if (pin.parentNode !== this.labelLayer)
            this.labelLayer.appendChild(pin);

        const p = window.graphGeometry.geoToCartesian(this.globePlace.lat, this.globePlace.lng, this.GLOBE_RADIUS);
        const c = this.instance.graph2ScreenCoords(p.x, p.y, p.z);
        const behind = window.graphGeometry.sphereOccludes(camera, p, drawnRadius);

        pin.style.display = '';
        pin.style.left = c.x + 'px';
        pin.style.top = c.y + 'px';

        if (behind)
            pin.style.visibility = 'hidden';
        else
            pin.style.visibility = '';
    },

    //Takes the pin away; the camera stays where it is.
    clearGlobePlace: function () {
        this.globePlace = null;
        this.globeFly = null;

        if (this.globePlacePin && this.globePlacePin.parentNode)
            this.globePlacePin.parentNode.removeChild(this.globePlacePin);

        this.globePlacePin = null;
    },

    //Where the pin is on screen, and whether the earth hides it, for tests; null with no place shown.
    getGlobePlace: function () {
        const pin = this.globePlacePin;

        if (!this.globePlace || !pin || pin.style.display === 'none')
            return null;

        return { x: parseFloat(pin.style.left), y: parseFloat(pin.style.top), hidden: pin.style.visibility === 'hidden', flying: !!this.globeFly, height: this.instance && this.globeHeight(this.instance) };
    },

    //The radius the earth is drawn at now: just under the nodes by GLOBE_SURFACE_GAP of their size.
    globeDrawnRadius: function () {
        return this.GLOBE_RADIUS * (1 - this.GLOBE_SURFACE_GAP * this.globeNodeFactor);
    },

    //The earth raised to just under the nodes as they shrink (its meshes are built at the full gap's radius and scaled
    //from there), and the camera's near plane brought in to a fifth of its height, so the ground a few hundred meters
    //below is drawn rather than clipped. At the framing height both are as they were.
    applyGlobeSurface: function (cam, height) {
        const built = this.GLOBE_RADIUS * (1 - this.GLOBE_SURFACE_GAP);
        const scale = this.globeDrawnRadius() / built;

        if (this.globeMesh && Math.abs(this.globeMesh.scale.x - scale) > 1e-9)
            this.globeMesh.scale.setScalar(scale);

        if (this.globeNearBase === null)
            this.globeNearBase = cam.near;

        const near = Math.min(this.globeNearBase, Math.max(0.0005, height * this.GLOBE_RADIUS * 0.2));

        if (Math.abs(cam.near - near) > near * 0.05) {
            cam.near = near;
            cam.updateProjectionMatrix();
        }
    },

    resetGlobeNodeScale: function (graph) {
        this.globeNodeFactor = 1;
        this.scaleNodeObjects(graph.graphData().nodes, 1);

        const cam = graph.camera();

        if (cam && this.globeNearBase !== null) {
            cam.near = this.globeNearBase;
            cam.updateProjectionMatrix();
        }

        this.globeNearBase = null;
    },

    //An arrowhead's length, the library's 4, scaled on the globe with the nodes (Node size on map and the camera's height),
    //so it stays an arrowhead on a node rather than a cone the size of a country (the user, 2026-11-06). The library reads
    //this and arrowRelPos every frame to place each arrowhead; its cone is built once at the length it had then, and
    //fitArrowObjects scales it to the length now.
    ARROW_LENGTH: 4,

    arrowLength: function () {
        if (this.globeOn)
            return this.ARROW_LENGTH * this.globeNodeFactor;
        else
            return this.ARROW_LENGTH;
    },

    //Where along its link the library puts an arrowhead's tip. It measures to the target's edge by the node's unscaled
    //radius, so on the globe, where the nodes are scaled, the tip would stop short of a smaller node. This puts the tip on
    //the scaled node's edge instead.
    arrowRelPos: function (link) {
        if (!this.globeOn || !this.instance)
            return 1;

        const s = link.source;
        const t = link.target;

        if (typeof s !== 'object' || typeof t !== 'object' || !s || !t)
            return 1;

        let length;

        if (link.__curve)
            length = link.__curve.getLength();
        else
            length = Math.hypot((t.x || 0) - (s.x || 0), (t.y || 0) - (s.y || 0), (t.z || 0) - (s.z || 0));

        const baseR = this.instance.nodeRelSize();
        const from = this.nodeRadius(s, baseR);
        const to = this.nodeRadius(t, baseR);
        const arrow = this.arrowLength();
        const room = length - from - to - arrow;

        if (room <= 0)
            return 1;

        return Math.max(0, (length - to * this.globeNodeFactor - from - arrow) / room);
    },

    //Each arrowhead's cone scaled to the length it has now; run every frame, since the library may build a cone again.
    fitArrowObjects: function () {
        if (!this.instance)
            return;

        const length = this.arrowLength();

        (this.instance.graphData().links || []).forEach(l => {
            const obj = l.__arrowObj;

            if (!obj || !obj.geometry || !obj.geometry.parameters || !obj.geometry.parameters.height)
                return;

            const target = length / obj.geometry.parameters.height;

            if (Math.abs(obj.scale.x - target) > 1e-9)
                obj.scale.setScalar(target);
        });
    },

    scaleNodeObjects: function (nodes, factor) {
        (nodes || []).forEach(n => {
            const obj = n.__threeObj;

            if (!obj)
                return;

            if (obj.userData.gdbvBaseScale === undefined)
                obj.userData.gdbvBaseScale = obj.scale.x;

            const target = obj.userData.gdbvBaseScale * factor;

            if (obj.scale.x !== target)
                obj.scale.setScalar(target);
        });
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

            for (let i = 0; i < stacks; i++)
                for (let j = 0; j < slices; j++)
                    quad(at(i, j), at(i + 1, j), at(i + 1, j + 1), at(i, j + 1));
        }
        else
            return null;

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
        if (data && data.nodes)
            for (const n of data.nodes) {
                if (!n.__threeObj)
                    continue;

                mesh = this.findMesh(n.__threeObj);
                if (mesh)
                    break;
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

        if (obj.children)
            for (const c of obj.children) {
                const found = this.findMesh(c);
                if (found)
                    return found;
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
                for (let i = 1; i + 1 < face.length; i++)
                    indices.push(face[0] - 1, face[i] - 1, face[i + 1] - 1);
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

        //A carded node's name, image and shown properties are on its card (detailsCardObjects).
        this.nodeLabels = [];
        data.nodes.forEach(node => {
            const text = node.label || node.id;
            if (!text || node.details === true)
                return;

            const el = this.makeLabelEl(layer, text, false);
            this.nodeLabels.push({ el: el, node: node });
        });

        this.linkLabels = [];
        (data.links || []).forEach(link => {
            if (!link.label || link.details === true)
                return;

            const el = this.makeLabelEl(layer, link.label, true);
            this.linkLabels.push({ el: el, link: link });
        });

        this.nodeImages = [];
        data.nodes.forEach(node => {
            //data.image is gated by the node's "show" state in the converter — unshown images are absent.
            const url = node.image;
            if (!url || node.details === true)
                return;

            const img = this.makeImageEl(layer, url);
            this.nodeImages.push({ el: img, node: node });
        });

        //Shown-properties (gdbvShow) floating text pinned just below each node that carries a list.
        this.nodeShowLabels = [];
        data.nodes.forEach(node => {
            const lines = node.showProps;
            if (!lines || !lines.length || node.details === true)
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
        }
        else
            el.style.fontSize = '11px';

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
        if (!this.instance || this.nodeDrawPercent >= 100 || this.globeOn)
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

        //On the globe, the overlay must not show what the earth hides: a label whose point is behind
        //the drawn sphere goes invisible (visibility, so the display toggles above stay in charge).
        let globeCamera = null;

        if (this.globeOn) {
            const cam = this.instance.camera();
            if (cam)
                globeCamera = cam.position;

            this.stepGlobeFly();
            this.stepGlobeZoom();
            this.applyGlobeRotateSpeed();
            this.applyGlobeNodeScale();
            this.updateGlobeTiles();
        }

        this.fitArrowObjects();

        //The labels and images keep their size on screen; on the globe Node size on map sizes them with the nodes.
        let overlayPlace = 'translate(-50%, -50%)';

        if (this.globeOn && this.globeNodeSize !== 1)
            overlayPlace = 'translate(-50%, -50%) scale(' + this.globeNodeSize + ')';

        const drawnRadius = this.globeDrawnRadius();

        const behindGlobe = function (x, y, z) {
            if (!globeCamera)
                return false;

            return window.graphGeometry.sphereOccludes(globeCamera, { x: x, y: y, z: z }, drawnRadius);
        };

        //And what the globe does not draw at all — a node with no place, a link with such an end — has
        //no label either; the geometry's visibility policy (refreshVisibility) says the same.
        const offGlobe = function (node) {
            return !!globeCamera && !self.isLocated(node);
        };

        this.placeGlobePin(globeCamera, drawnRadius);

        const setBehind = function (el, behind) {
            let visibility;

            if (behind)
                visibility = 'hidden';
            else
                visibility = '';

            if (el.style.visibility !== visibility)
                el.style.visibility = visibility;
        };

        this.nodeLabels.forEach(item => {
            if (item.el.style.display === 'none')
                return;

            const nx = item.node.x || 0;
            const ny = item.node.y || 0;
            const nz = item.node.z || 0;

            const c = self.instance.graph2ScreenCoords(nx, ny, nz);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';

            if (item.el.style.transform !== overlayPlace)
                item.el.style.transform = overlayPlace;

            setBehind(item.el, offGlobe(item.node) || behindGlobe(nx, ny, nz));

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

            let mx = ((s.x || 0) + (t.x || 0)) / 2;
            let my = ((s.y || 0) + (t.y || 0)) / 2;
            let mz = ((s.z || 0) + (t.z || 0)) / 2;

            //A globe link bows over the surface, and its chord's middle lies under it: lift the label to
            //just above the surface, where the arc is, so it sits on the earth and hides with it.
            if (globeCamera) {
                const chord = Math.sqrt(mx * mx + my * my + mz * mz);

                if (chord > 0) {
                    const lift = self.GLOBE_RADIUS * 1.02 / chord;
                    mx *= lift;
                    my *= lift;
                    mz *= lift;
                }
            }

            const c = self.instance.graph2ScreenCoords(mx, my, mz);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';

            if (item.el.style.transform !== overlayPlace)
                item.el.style.transform = overlayPlace;

            setBehind(item.el, offGlobe(s) || offGlobe(t) || behindGlobe(mx, my, mz));

            if (cull && camPos)
                item.el.style.opacity = self.labelOpacityForDistance(self.dist3(camPos, mx, my, mz), threshold, 0.85);
        });

        (this.nodeImages || []).forEach(item => {
            const c = self.instance.graph2ScreenCoords(item.node.x || 0, item.node.y || 0, item.node.z || 0);
            item.el.style.left = c.x + 'px';
            item.el.style.top = c.y + 'px';

            if (item.el.style.transform !== overlayPlace)
                item.el.style.transform = overlayPlace;

            setBehind(item.el, offGlobe(item.node) || behindGlobe(item.node.x || 0, item.node.y || 0, item.node.z || 0));
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
            //On the globe the node itself is scaled down as the camera closes in; so is this.
            let r = self.nodeRadius(n, baseR);

            if (globeCamera)
                r *= self.globeNodeFactor;
            const edge = self.instance.graph2ScreenCoords((n.x || 0) + r, n.y || 0, n.z || 0);
            const apparent = Math.hypot(edge.x - c.x, edge.y - c.y);
            const scale = Math.max(0.25, Math.min(3, apparent / 8));

            item.el.style.left = c.x + 'px';
            item.el.style.top = (c.y + apparent + 14) + 'px';
            item.el.style.transform = 'translate(-50%, 0) scale(' + scale + ')';
            setBehind(item.el, offGlobe(n) || behindGlobe(n.x || 0, n.y || 0, n.z || 0));
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
            for (const k in props)
                if (String(props[k]).toLowerCase().includes(q))
                    return true;

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
            }
            else
                camPos = { x: x, y: y, z: z + 120 };

            this.instance.cameraPosition(camPos, { x: x, y: y, z: z }, 800);
        }

        return matches.length;
    },

    //Hides nodes whose type label (glabel) is in the hidden list, and any incident links.
    setLabelFilter: function (hiddenJson) {
        if (!this.instance)
            return;

        const hidden = JSON.parse(hiddenJson);
        //Kept so node distance culling can respect the same filter (see isLabelHidden) — and so the
        //globe's own hiding composes with it rather than replacing it (refreshVisibility).
        this.hiddenLabelSet = new Set(hidden);
        this.refreshVisibility(this.instance);
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
        this.removeGlobeMesh();
        this.globeOn = false;

        if (this.instance) {
            this.instance._destructor && this.instance._destructor();
            this.instance = null;
        }
    }
};
