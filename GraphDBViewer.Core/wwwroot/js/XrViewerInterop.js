//WebXR (VR / AR) viewer for the current graph, built on the vendored standalone three.js. 3d-force-graph
//has no WebXR, so this is a separate scene: it snapshots the live 3D layout (node positions + colors +
//linked models) and rebuilds it here, where renderer.xr can drive an immersive session. Non-immersive it
//falls back to drag / scroll orbit for the on-screen preview. One live scene at a time.
window.xrViewerInterop = {
    state: null,

    //Whether this browser / device can start immersive VR and AR sessions (both need a secure context).
    isSupported: async function () {
        let vr = false, ar = false;
        const hasXR = !!navigator.xr;
        if (hasXR) {
            try { vr = await navigator.xr.isSessionSupported('immersive-vr'); }
            catch (e) { }
            try { ar = await navigator.xr.isSessionSupported('immersive-ar'); }
            catch (e) { }
        }
        return { hasXR: hasXR, vr: vr, ar: ar, secure: window.isSecureContext === true };
    },

    //Builds the scene from the live 3D graph and starts the render loop. Returns support info so the UI
    //can enable / disable the Enter VR / Enter AR buttons. Errors (no 3D graph yet) come back as ok:false.
    open: async function (containerId) {
        if (typeof THREE === 'undefined')
            return { ok: false, error: 'three.js is not loaded.' };

        //The overlay may still be rendering when Blazor calls this — wait a few frames for its container.
        let el = document.getElementById(containerId);
        for (let i = 0; i < 30 && !el; i++) {
            await new Promise(r => requestAnimationFrame(r));
            el = document.getElementById(containerId);
        }
        if (!el)
            return { ok: false, error: 'Viewer unavailable.' };

        this.close();

        const gi = window.graph3DInterop;
        if (!gi || !gi.instance)
            return { ok: false, error: 'Open the 3D view first, then try again.' };

        const data = gi.instance.graphData();
        const nodes = (data && data.nodes) || [];
        const links = (data && data.links) || [];
        if (!nodes.length)
            return { ok: false, error: 'Nothing to show — the graph is empty.' };

        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(60, 1, 0.01, 1000);
        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        renderer.setPixelRatio(window.devicePixelRatio || 1);
        renderer.xr.enabled = true;

        el.innerHTML = '';
        el.appendChild(renderer.domElement);
        renderer.domElement.style.width = '100%';
        renderer.domElement.style.height = '100%';
        renderer.domElement.style.display = 'block';
        renderer.domElement.style.cursor = 'grab';

        scene.add(new THREE.AmbientLight(0xffffff, 0.8));
        const key = new THREE.DirectionalLight(0xffffff, 0.8);
        key.position.set(3, 5, 4);
        scene.add(key);

        //Center the graph on its bounding box and scale it down to a comfortable ~1.4 m so it sits in front
        //of the viewer at eye height (works the same for the desktop preview and an immersive session).
        let min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
        nodes.forEach(n => {
            const p = [n.x || 0, n.y || 0, n.z || 0];
            for (let i = 0; i < 3; i++) {
                min[i] = Math.min(min[i], p[i]);
                max[i] = Math.max(max[i], p[i]);
            }
        });
        const center = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2];
        let span = Math.max(max[0] - min[0], max[1] - min[1], max[2] - min[2]);
        if (span <= 0)
            span = 1;

        const root = new THREE.Group();
        root.position.set(0, 1.4, -1.6);
        root.scale.setScalar(1.4 / span);
        scene.add(root);

        const pos = n => new THREE.Vector3((n.x || 0) - center[0], (n.y || 0) - center[1], (n.z || 0) - center[2]);
        const nodeR = span * 0.02;

        const nodeMeshes = {};
        nodes.forEach(n => {
            let col = 0x4c8bf5;
            try { col = new THREE.Color(n.bgColor || n.color || '#4c8bf5').getHex(); }
            catch (e) { }

            const mesh = new THREE.Mesh(
                new THREE.SphereGeometry(nodeR, 16, 12),
                new THREE.MeshStandardMaterial({ color: col, roughness: 0.6, metalness: 0.1 }));
            mesh.position.copy(pos(n));
            root.add(mesh);
            nodeMeshes[n.id] = mesh;
        });

        const lineMat = new THREE.LineBasicMaterial({ color: 0x888888, transparent: true, opacity: 0.5 });
        links.forEach(l => {
            let s = l.source, t = l.target;
            if (typeof s !== 'object')
                s = nodes.find(n => n.id === s);
            if (typeof t !== 'object')
                t = nodes.find(n => n.id === t);
            if (!s || !t)
                return;

            const g = new THREE.BufferGeometry().setFromPoints([pos(s), pos(t)]);
            root.add(new THREE.Line(g, lineMat));
        });

        camera.position.set(0, 1.4, 0.7);
        const orbitTarget = new THREE.Vector3(0, 1.4, -1.6);
        camera.lookAt(orbitTarget);

        this.state = {
            el: el, renderer: renderer, scene: scene, camera: camera, root: root,
            orbitTarget: orbitTarget, span: span, disposed: false, session: null, w: 0, h: 0
        };

        //Swap in linked .obj / .zip models for nodes that carry one, reusing the preview's mesh builder.
        this.loadModels(nodes, nodeMeshes, nodeR);

        this.setupOrbit();

        //setAnimationLoop (not requestAnimationFrame) so WebXR can take over the loop inside a session.
        renderer.setAnimationLoop(() => {
            const st = this.state;
            if (!st || st.disposed)
                return;

            const w = el.clientWidth, h = el.clientHeight;
            if (w > 0 && h > 0 && (w !== st.w || h !== st.h)) {
                st.w = w;
                st.h = h;
                renderer.setSize(w, h, false);
                camera.aspect = w / h;
                camera.updateProjectionMatrix();
            }

            renderer.render(scene, camera);
        });

        const sup = await this.isSupported();
        return { ok: true, nodeCount: nodes.length, linkCount: links.length, hasXR: sup.hasXR, vr: sup.vr, ar: sup.ar, secure: sup.secure };
    },

    //Fetches + builds each node's linked model and swaps it in for the placeholder sphere (async, so the
    //graph shows immediately and models pop in). Reuses modelPreviewInterop.buildMesh and loadObjModel.
    loadModels: async function (nodes, nodeMeshes, nodeR) {
        const gi = window.graph3DInterop;
        const mpi = window.modelPreviewInterop;
        if (!gi || !mpi)
            return;

        const cache = {};
        for (const n of nodes) {
            if (!n.model)
                continue;

            let parsed = null;
            try { parsed = await gi.loadObjModel(n.model, cache); }
            catch (e) { }

            if (!this.state || this.state.disposed)
                return;

            if (!parsed || !parsed.verts || !parsed.verts.length)
                continue;

            const mesh = mpi.buildMesh(parsed, n.bgColor || n.color);
            if (!mesh)
                continue;

            //buildMesh normalizes the model to a ~radius-2 box; bring it to the node's placeholder size.
            mesh.scale.setScalar(nodeR / 2);

            const old = nodeMeshes[n.id];
            if (old) {
                mesh.position.copy(old.position);
                this.state.root.remove(old);
            }
            this.state.root.add(mesh);
            nodeMeshes[n.id] = mesh;
        }
    },

    //Drag to orbit the camera around the graph, scroll to dolly in / out — only affects the on-screen
    //preview; inside an immersive session the headset drives the camera and these are ignored.
    setupOrbit: function () {
        const st = this.state;
        if (!st)
            return;

        const dom = st.renderer.domElement;
        let dragging = false, lastX = 0, lastY = 0;
        let theta = 0, phi = 0, radius = 2.3;
        const target = st.orbitTarget;

        const apply = () => {
            const x = target.x + radius * Math.sin(theta) * Math.cos(phi);
            const y = target.y + radius * Math.sin(phi);
            const z = target.z + radius * Math.cos(theta) * Math.cos(phi);
            st.camera.position.set(x, y, z);
            st.camera.lookAt(target);
        };
        apply();

        const onDown = e => { dragging = true; lastX = e.clientX; lastY = e.clientY; dom.style.cursor = 'grabbing'; };
        const onUp = () => { dragging = false; dom.style.cursor = 'grab'; };
        const onMove = e => {
            if (!dragging)
                return;
            theta -= (e.clientX - lastX) * 0.01;
            phi = Math.max(-1.4, Math.min(1.4, phi + (e.clientY - lastY) * 0.01));
            lastX = e.clientX;
            lastY = e.clientY;
            apply();
        };
        const onWheel = e => {
            e.preventDefault();
            radius = Math.max(0.4, Math.min(8, radius + (e.deltaY > 0 ? 0.25 : -0.25)));
            apply();
        };

        dom.addEventListener('mousedown', onDown);
        window.addEventListener('mouseup', onUp);
        window.addEventListener('mousemove', onMove);
        dom.addEventListener('wheel', onWheel, { passive: false });
        st.orbitHandlers = { onUp, onMove };
    },

    enterVR: function () {
        return this.enterSession('immersive-vr', { optionalFeatures: ['local-floor', 'bounded-floor', 'hand-tracking'] });
    },

    enterAR: function () {
        return this.enterSession('immersive-ar', { optionalFeatures: ['local-floor', 'hit-test', 'hand-tracking'] });
    },

    //Requests and attaches an immersive session on the viewer's renderer. Reports the failure reason (most
    //commonly: not a secure context, or no device) so the UI can surface it instead of failing silently.
    enterSession: async function (mode, opts) {
        if (!this.state || !navigator.xr)
            return { ok: false, error: 'WebXR is not available here.' };

        try {
            const session = await navigator.xr.requestSession(mode, opts);
            await this.state.renderer.xr.setSession(session);
            this.state.session = session;
            const self = this;
            session.addEventListener('end', () => {
                if (self.state)
                    self.state.session = null;
            });
            return { ok: true };
        }
        catch (e) {
            return { ok: false, error: e && e.message ? e.message : String(e) };
        }
    },

    close: function () {
        const st = this.state;
        if (!st)
            return;

        st.disposed = true;

        if (st.session) {
            try { st.session.end(); }
            catch (e) { }
        }

        if (st.orbitHandlers) {
            window.removeEventListener('mouseup', st.orbitHandlers.onUp);
            window.removeEventListener('mousemove', st.orbitHandlers.onMove);
        }

        if (st.renderer) {
            try { st.renderer.setAnimationLoop(null); }
            catch (e) { }
            try { st.renderer.dispose(); }
            catch (e) { }
        }

        if (st.el)
            st.el.innerHTML = '';

        this.state = null;
    }
};
