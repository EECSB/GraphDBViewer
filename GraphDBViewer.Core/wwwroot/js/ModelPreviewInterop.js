//Standalone three.js viewer for previewing a linked .obj / .zip model in the property panel, separate
//from the force-graph 3D view (which bundles its own three.js and exposes no WebXR). Built on the
//vendored global THREE so this same scene can later host WebXR (AR / VR). One live scene per container id.
window.modelPreviewInterop = {
    scenes: {},//containerId -> live scene state
    modelCache: {},//url -> parsed model, shared across previews so re-selecting a node doesn't re-fetch

    //Renders (or re-renders) the model at url into the element with id containerId. color is an optional
    //hex string for the mesh. Reuses graph3DInterop.loadObjModel for fetch + parse (and .zip handling).
    render: async function (containerId, url, color) {
        const el = document.getElementById(containerId);
        if (!el || typeof THREE === 'undefined' || !url)
            return;

        //A new url tears down the previous scene first.
        this.dispose(containerId);

        const state = { url: url, disposed: false, w: 0, h: 0, userRotated: false };
        this.scenes[containerId] = state;

        const scene = new THREE.Scene();
        const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 1000);
        camera.position.set(0, 0, 8);

        const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        renderer.setPixelRatio(window.devicePixelRatio || 1);
        el.innerHTML = '';
        el.appendChild(renderer.domElement);
        renderer.domElement.style.width = '100%';
        renderer.domElement.style.height = '100%';
        renderer.domElement.style.display = 'block';
        renderer.domElement.style.cursor = 'grab';

        scene.add(new THREE.AmbientLight(0xffffff, 0.75));
        const key = new THREE.DirectionalLight(0xffffff, 0.9);
        key.position.set(3, 5, 4);
        scene.add(key);
        const fill = new THREE.DirectionalLight(0xffffff, 0.35);
        fill.position.set(-4, -2, -3);
        scene.add(fill);

        const group = new THREE.Group();
        scene.add(group);

        state.renderer = renderer;
        state.camera = camera;
        state.scene = scene;
        state.group = group;

        //Load + build the mesh (async). Bail if the scene was disposed meanwhile (selection changed).
        let parsed = null;
        try {
            parsed = await graph3DInterop.loadObjModel(url, this.modelCache);
        }
        catch (e) { }

        if (state.disposed)
            return;

        if (parsed && parsed.verts && parsed.verts.length) {
            const mesh = this.buildMesh(parsed, color);
            if (mesh)
                group.add(mesh);
        }

        //Manual orbit: drag to rotate, wheel to zoom (no OrbitControls dependency).
        let dragging = false, lastX = 0, lastY = 0;
        const onDown = e => { dragging = true; lastX = e.clientX; lastY = e.clientY; state.userRotated = true; renderer.domElement.style.cursor = 'grabbing'; };
        const onUp = () => { dragging = false; renderer.domElement.style.cursor = 'grab'; };
        const onMove = e => {
            if (!dragging)
                return;
            const dx = e.clientX - lastX, dy = e.clientY - lastY;
            lastX = e.clientX;
            lastY = e.clientY;
            group.rotation.y += dx * 0.01;
            group.rotation.x += dy * 0.01;
        };
        const onWheel = e => {
            e.preventDefault();
            let z = camera.position.z + (e.deltaY > 0 ? 0.8 : -0.8);
            camera.position.z = Math.max(2.5, Math.min(30, z));
        };
        renderer.domElement.addEventListener('mousedown', onDown);
        window.addEventListener('mouseup', onUp);
        window.addEventListener('mousemove', onMove);
        renderer.domElement.addEventListener('wheel', onWheel, { passive: false });
        state.handlers = { onUp, onMove };

        //Animation loop: gently auto-rotate until the user drags, and keep the renderer sized to the box
        //(the preview starts inside a collapsed <details>, so it may be zero-size until the user expands it).
        const animate = () => {
            if (state.disposed)
                return;

            state.raf = requestAnimationFrame(animate);

            const w = el.clientWidth, h = el.clientHeight;
            if (w > 0 && h > 0 && (w !== state.w || h !== state.h)) {
                state.w = w;
                state.h = h;
                renderer.setSize(w, h, false);
                camera.aspect = w / h;
                camera.updateProjectionMatrix();
            }

            if (!state.userRotated)
                group.rotation.y += 0.006;

            renderer.render(scene, camera);
        };
        animate();
    },

    //Centers + scales the parsed model ({ verts:[[x,y,z]..], faces:[[i,j,k..] 1-based] }) to fit a small
    //box, then wraps it in a lit standard material. Mirrors buildModelMesh in the 3D interop.
    buildMesh: function (model, color) {
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

        const cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, cz = (minZ + maxZ) / 2;
        let maxDim = Math.max(maxX - minX, maxY - minY, maxZ - minZ);
        if (maxDim <= 0)
            maxDim = 1;

        const scale = 4 / maxDim;

        const positions = new Float32Array(model.verts.length * 3);
        for (let i = 0; i < model.verts.length; i++) {
            const v = model.verts[i];
            positions[i * 3] = (v[0] - cx) * scale;
            positions[i * 3 + 1] = (v[1] - cy) * scale;
            positions[i * 3 + 2] = (v[2] - cz) * scale;
        }

        const indices = [];
        model.faces.forEach(face => {
            for (let i = 1; i + 1 < face.length; i++)
                indices.push(face[0] - 1, face[i] - 1, face[i + 1] - 1);
        });

        const geom = new THREE.BufferGeometry();
        geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geom.setIndex(indices);
        geom.computeVertexNormals();

        let col = 0x4c8bf5;
        if (color) {
            try { col = new THREE.Color(color).getHex(); }
            catch (e) { }
        }

        const mat = new THREE.MeshStandardMaterial({ color: col, roughness: 0.6, metalness: 0.1 });
        return new THREE.Mesh(geom, mat);
    },

    //Tears down the scene for a container: stops the loop, frees the WebGL context, drops the canvas.
    dispose: function (containerId) {
        const state = this.scenes[containerId];
        if (!state)
            return;

        state.disposed = true;
        if (state.raf)
            cancelAnimationFrame(state.raf);

        if (state.handlers) {
            window.removeEventListener('mouseup', state.handlers.onUp);
            window.removeEventListener('mousemove', state.handlers.onMove);
        }

        if (state.renderer) {
            try { state.renderer.dispose(); }
            catch (e) { }
        }

        const el = document.getElementById(containerId);
        if (el)
            el.innerHTML = '';

        delete this.scenes[containerId];
    }
};
