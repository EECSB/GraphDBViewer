const test = require('node:test');
const assert = require('node:assert');
const geo = require('../GraphDBViewer.Core/wwwroot/js/graphGeometry.js');

test('normalizeModelUrl rewrites OneDrive share links to the content endpoint', () => {
    const out = geo.normalizeModelUrl('https://1drv.ms/u/c/abc/def');
    assert.ok(out.startsWith('https://api.onedrive.com/v1.0/shares/u!'));
    assert.ok(out.endsWith('/root/content'));
});

test('normalizeModelUrl rewrites Dropbox to the direct-download host', () => {
    const out = geo.normalizeModelUrl('https://www.dropbox.com/s/x/model.obj?dl=0');
    assert.ok(out.includes('dl.dropboxusercontent.com'));
    assert.ok(!out.includes('?dl=0'));
});

test('normalizeModelUrl leaves same-origin/plain URLs unchanged', () => {
    assert.strictEqual(geo.normalizeModelUrl('models/m.obj'), 'models/m.obj');
});

test('parseObj reads vertices and faces', () => {
    const m = geo.parseObj('v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3');
    assert.strictEqual(m.verts.length, 3);
    assert.strictEqual(m.faces.length, 1);
    assert.deepStrictEqual(m.faces[0], [1, 2, 3]);
});

test('parseObj ignores comments/blank lines and skips texture/normal refs', () => {
    const m = geo.parseObj('# c\n\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1/1/1 2/2/2 3/3/3');
    assert.strictEqual(m.verts.length, 3);
    assert.deepStrictEqual(m.faces[0], [1, 2, 3]);
});

test('parseObj returns null when there are no vertices', () => {
    assert.strictEqual(geo.parseObj('# nothing here'), null);
});

test('addSphere appends vertices and triangles', () => {
    const mesh = { verts: [], tris: [] };
    geo.addSphere(mesh, 0, 0, 0, 4);
    assert.ok(mesh.verts.length > 0);
    assert.ok(mesh.tris.length > 0);
});

test('addTube appends two rings of vertices and side faces', () => {
    const mesh = { verts: [], tris: [] };
    geo.addTube(mesh, { x: 0, y: 0, z: 0 }, { x: 0, y: 10, z: 0 }, 1);
    assert.strictEqual(mesh.verts.length, 16);
    assert.strictEqual(mesh.tris.length, 16);
});

test('addModel scales/centers a model and fan-triangulates faces (0-based)', () => {
    const mesh = { verts: [], tris: [] };
    const model = { verts: [[0, 0, 0], [2, 0, 0], [2, 2, 0], [0, 2, 0]], faces: [[1, 2, 3, 4]] };
    geo.addModel(mesh, model, 0, 0, 0, 4);
    assert.strictEqual(mesh.verts.length, 4);
    assert.strictEqual(mesh.tris.length, 2);
});

test('meshToObj emits v and 1-based f lines', () => {
    const mesh = { verts: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], tris: [[0, 1, 2]] };
    const out = geo.meshToObj(mesh);
    assert.ok(out.includes('v 0.0000 0.0000 0.0000'));
    assert.ok(out.includes('f 1 2 3'));
});

test('meshToPly emits header with element counts and a 0-based face row', () => {
    const mesh = { verts: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], tris: [[0, 1, 2]] };
    const out = geo.meshToPly(mesh);
    assert.ok(out.startsWith('ply'));
    assert.ok(out.includes('element vertex 3'));
    assert.ok(out.includes('element face 1'));
    assert.ok(out.includes('3 0 1 2'));
});

test('meshToStl emits facet/loop/vertex lines', () => {
    const mesh = { verts: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], tris: [[0, 1, 2]] };
    const out = geo.meshToStl(mesh);
    assert.ok(out.startsWith('solid graph'));
    assert.ok(out.includes('facet normal'));
    assert.ok(out.includes('vertex 1.0000 0.0000 0.0000'));
    assert.ok(out.trimEnd().endsWith('endsolid graph'));
});

test('meshToGltf emits valid glTF 2.0 JSON with position/index accessors', () => {
    const mesh = { verts: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], tris: [[0, 1, 2]] };
    const g = JSON.parse(geo.meshToGltf(mesh));

    assert.strictEqual(g.asset.version, '2.0');
    assert.strictEqual(g.meshes[0].primitives[0].mode, 4);
    assert.strictEqual(g.accessors.length, 2);
    assert.strictEqual(g.accessors[0].count, 3);
    assert.strictEqual(g.accessors[0].type, 'VEC3');
    assert.strictEqual(g.accessors[1].count, 3);
    assert.deepStrictEqual(g.accessors[0].max, [1, 1, 0]);
    assert.ok(g.buffers[0].uri.startsWith('data:application/octet-stream;base64,'));
});

test('isZip detects the PK local-file-header magic', () => {
    assert.strictEqual(geo.isZip(new Uint8Array([0x50, 0x4b, 0x03, 0x04])), true);
    assert.strictEqual(geo.isZip(new Uint8Array([1, 2, 3, 4])), false);
});

test('parseModelData reads a plain (non-zip) OBJ arraybuffer', async () => {
    const buf = new TextEncoder().encode('v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3').buffer;
    const m = await geo.parseModelData(buf);
    assert.strictEqual(m.verts.length, 3);
    assert.strictEqual(m.faces.length, 1);
});

test('parseModelData reads the .obj from a stored ZIP', async () => {
    const zip = makeStoredZip('model.obj', 'v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3');
    const m = await geo.parseModelData(zip);
    assert.strictEqual(m.verts.length, 3);
    assert.strictEqual(m.faces.length, 1);
});

//── glTF: a linked model as glTF or GLB, read for its geometry alone ──────────────────────────

const TRIANGLE = { verts: [[0, 0, 0], [1, 0, 0], [0, 1, 0]], tris: [[0, 1, 2]] };

test('meshToGlb writes the GLB container: its header, a JSON chunk padded with spaces and the BIN chunk', () => {
    const glb = geo.meshToGlb(TRIANGLE);
    const view = new DataView(glb.buffer);

    assert.strictEqual(view.getUint32(0, true), 0x46546C67);
    assert.strictEqual(view.getUint32(4, true), 2);
    assert.strictEqual(view.getUint32(8, true), glb.length);

    const jsonLength = view.getUint32(12, true);
    const json = JSON.parse(new TextDecoder().decode(glb.subarray(20, 20 + jsonLength)));
    const binAt = 20 + jsonLength;

    assert.strictEqual(jsonLength % 4, 0);
    assert.strictEqual(view.getUint32(16, true), 0x4E4F534A);
    assert.strictEqual(view.getUint32(binAt + 4, true), 0x004E4942);

    //The buffer is the BIN chunk, so it names no file: three positions of twelve bytes and three indices of four.
    assert.deepStrictEqual(json.buffers, [{ byteLength: 48 }]);
    assert.strictEqual(view.getUint32(binAt, true), 48);
    assert.strictEqual(glb.length, binAt + 8 + 48);
    assert.ok(geo.isGlb(glb));
});

test('a mesh written as GLB or glTF reads back as the same model', async () => {
    const fromGlb = await geo.parseModelData(geo.meshToGlb(TRIANGLE).buffer);
    const fromGltf = await geo.parseModelData(new TextEncoder().encode(geo.meshToGltf(TRIANGLE)).buffer);

    for (const model of [fromGlb, fromGltf]) {
        assert.deepStrictEqual(model.verts, TRIANGLE.verts);
        assert.deepStrictEqual(model.faces, [[1, 2, 3]]);
    }
});

test('a glTF is placed by its nodes: a parent turned and moved, its child scaled, a mesh used twice', async () => {
    const half = Math.SQRT1_2;
    const gltf = gltfWith(
        [{ positions: [1, 0, 0, 0, 0, 0, 0, 0, 1], indices: [0, 1, 2] }],
        [
            { children: [1, 2], translation: [10, 0, 0], rotation: [0, 0, half, half] },
            { mesh: 0, scale: [2, 2, 2] },
            { mesh: 0, matrix: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 5, 1] }
        ]);
    const model = await geo.parseGltf(gltf);

    //A quarter turn about z takes x to y, after the child's scale, and the parent's move comes last.
    assertNear(model.verts[0], [10, 2, 0]);
    assertNear(model.verts[2], [10, 0, 2]);

    //The second use sits five above, by its own matrix, turned and moved by the same parent.
    assertNear(model.verts[3], [10, 1, 5]);
    assert.deepStrictEqual(model.faces, [[1, 2, 3], [4, 5, 6]]);
});

test('a glTF strip and fan are read as their triangles, and lines and points are left out', async () => {
    const square = [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0];
    const model = await geo.parseGltf(gltfWith(
        [
            { positions: square, indices: [0, 1, 2, 3], mode: 5 },
            { positions: square, indices: [0, 1, 3, 2], mode: 6 },
            { positions: square, indices: [0, 1, 1, 2], mode: 1 }
        ],
        [{ mesh: 0 }]));

    //The strip's second triangle turns the other way round; the fan keeps its first vertex.
    assert.deepStrictEqual(model.faces, [[1, 2, 3], [3, 2, 4], [5, 6, 8], [5, 8, 7]]);
    assert.strictEqual(model.verts.length, 8);

    assert.strictEqual(await geo.parseGltf(gltfWith([{ positions: square, indices: [0, 1], mode: 1 }], [{ mesh: 0 }])), null);
});

test('a glTF whose buffer is a file beside it reads it from beside it, on the web or in its zip', async () => {
    const gltf = gltfWith([{ positions: [0, 0, 0, 1, 0, 0, 0, 1, 0] }], [{ mesh: 0 }]);
    const bin = new Uint8Array(dataBytes(gltf.buffers[0].uri));

    gltf.buffers[0] = { byteLength: bin.length, uri: 'parts/tri%20angle.bin' };

    const asked = [];
    const beside = async uri => {
        asked.push(uri);

        return bin;
    };
    const fetched = await geo.parseModelData(new TextEncoder().encode(JSON.stringify(gltf)).buffer, beside);

    assert.deepStrictEqual(asked, ['parts/tri%20angle.bin']);
    assert.strictEqual(fetched.faces.length, 1);

    //Without a way to read it, the model cannot be read at all.
    assert.strictEqual(await geo.parseModelData(new TextEncoder().encode(JSON.stringify(gltf)).buffer), null);

    //In a zip, the buffer is found beside the .gltf by its own name, escapes undone.
    const zip = makeStoredZipOf([
        ['model/scene.gltf', new TextEncoder().encode(JSON.stringify(gltf))],
        ['model/parts/tri angle.bin', bin]
    ]);
    const zipped = await geo.parseModelData(zip);

    assert.deepStrictEqual(zipped.verts, [[0, 0, 0], [1, 0, 0], [0, 1, 0]]);
});

test('a .glb inside a zip is read before anything else the zip holds', async () => {
    const zip = makeStoredZipOf([
        ['readme.obj', new TextEncoder().encode('v 9 9 9\nv 8 8 8\nv 7 7 7\nf 1 2 3')],
        ['model.glb', geo.meshToGlb(TRIANGLE)]
    ]);
    const model = await geo.parseModelData(zip);

    assert.deepStrictEqual(model.verts, TRIANGLE.verts);
});

test('a glTF accessor is read as its type says: normalized integers scaled, sparse values substituted', async () => {
    //Quantized positions, as KHR_mesh_quantization writes them: unsigned shorts read as 0..1.
    const quantized = gltfWith([{ positions: [0, 0, 0, 65535, 0, 0, 0, 65535, 0], componentType: 5123, normalized: true }], [{ mesh: 0 }]);

    quantized.extensionsRequired = ['KHR_mesh_quantization'];
    assert.deepStrictEqual((await geo.parseGltf(quantized)).verts, [[0, 0, 0], [1, 0, 0], [0, 1, 0]]);

    //A sparse accessor over no data of its own: zeros, but for the second vertex, which is moved to (3, 4, 5).
    const sparse = gltfWith([{ positions: [0, 0, 0, 0, 0, 0, 0, 0, 0] }], [{ mesh: 0 }]);
    const extra = new Uint8Array(4 + 12);
    const view = new DataView(extra.buffer);

    view.setUint32(0, 1, true);
    view.setFloat32(4, 3, true);
    view.setFloat32(8, 4, true);
    view.setFloat32(12, 5, true);
    sparse.buffers.push({ byteLength: extra.length, uri: 'data:application/octet-stream;base64,' + geo.bytesToBase64(extra) });
    sparse.bufferViews.push({ buffer: 1, byteOffset: 0, byteLength: 4 }, { buffer: 1, byteOffset: 4, byteLength: 12 });
    delete sparse.accessors[0].bufferView;
    sparse.accessors[0].sparse = { count: 1, indices: { bufferView: 1, componentType: 5125 }, values: { bufferView: 2 } };

    assert.deepStrictEqual((await geo.parseGltf(sparse)).verts, [[0, 0, 0], [3, 4, 5], [0, 0, 0]]);
});

test('a glTF that needs a decoder the viewer does not have is not read, and neither is a damaged one', async () => {
    const draco = gltfWith([{ positions: [0, 0, 0, 1, 0, 0, 0, 1, 0] }], [{ mesh: 0 }]);

    draco.extensionsRequired = ['KHR_draco_mesh_compression'];
    assert.strictEqual(await geo.parseGltf(draco), null);

    const pastItsEnd = gltfWith([{ positions: [0, 0, 0, 1, 0, 0, 0, 1, 0], indices: [0, 1, 7] }], [{ mesh: 0 }]);

    assert.strictEqual(await geo.parseGltf(pastItsEnd), null);
    assert.strictEqual(await geo.parseModelData(new Uint8Array([0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0, 99, 0, 0, 0]).buffer), null);
});

//── glTF fixture helpers ──────────────────────────────────────────────────────────────────────

//A glTF of these primitives in one mesh, each a positions accessor and, when it has them, an indices one, all in one
//embedded buffer; and these nodes, every node a root unless another holds it.
function gltfWith(primitives, nodes) {
    const chunks = [];
    const bufferViews = [];
    const accessors = [];
    let length = 0;

    const add = (bytes, accessor) => {
        bufferViews.push({ buffer: 0, byteOffset: length, byteLength: bytes.length });
        accessor.bufferView = bufferViews.length - 1;
        accessors.push(accessor);
        chunks.push(bytes);
        length += bytes.length;

        const pad = (4 - length % 4) % 4;

        if (pad > 0) {
            chunks.push(new Uint8Array(pad));
            length += pad;
        }

        return accessors.length - 1;
    };

    const gltfPrimitives = primitives.map(p => {
        let positions;

        if (p.componentType === 5123)
            positions = new Uint8Array(new Uint16Array(p.positions).buffer);
        else
            positions = new Uint8Array(new Float32Array(p.positions).buffer);

        const out = {
            attributes: {
                POSITION: add(positions, { componentType: p.componentType || 5126, normalized: p.normalized || false, count: p.positions.length / 3, type: 'VEC3' })
            }
        };

        if (p.indices)
            out.indices = add(new Uint8Array(new Uint32Array(p.indices).buffer), { componentType: 5125, count: p.indices.length, type: 'SCALAR' });

        if (p.mode !== undefined)
            out.mode = p.mode;

        return out;
    });

    const bytes = new Uint8Array(length);
    let at = 0;

    for (const chunk of chunks) {
        bytes.set(chunk, at);
        at += chunk.length;
    }

    const held = new Set(nodes.flatMap(n => n.children || []));

    return {
        asset: { version: '2.0' },
        scene: 0,
        scenes: [{ nodes: nodes.map((n, i) => i).filter(i => !held.has(i)) }],
        nodes: nodes,
        meshes: [{ primitives: gltfPrimitives }],
        buffers: [{ byteLength: length, uri: 'data:application/octet-stream;base64,' + geo.bytesToBase64(bytes) }],
        bufferViews: bufferViews,
        accessors: accessors
    };
}

function dataBytes(uri) {
    const binary = atob(uri.substring(uri.indexOf(',') + 1));

    return Array.from(binary, c => c.charCodeAt(0));
}

function assertNear(actual, expected) {
    for (let i = 0; i < expected.length; i++)
        assert.ok(Math.abs(actual[i] - expected[i]) < 1e-9, 'expected ' + JSON.stringify(expected) + ', got ' + JSON.stringify(actual));
}

//A stored zip of these [name, bytes] entries.
function makeStoredZipOf(files) {
    const enc = new TextEncoder();
    const local = [];
    const central = [];

    for (const [name, content] of files) {
        const nameB = Array.from(enc.encode(name));
        const data = Array.from(content);
        const offset = local.length;

        u32(local, 0x04034b50); u16(local, 20); u16(local, 0); u16(local, 0);
        u16(local, 0); u16(local, 0);
        u32(local, 0); u32(local, data.length); u32(local, data.length);
        u16(local, nameB.length); u16(local, 0);
        local.push(...nameB, ...data);

        u32(central, 0x02014b50); u16(central, 20); u16(central, 20); u16(central, 0); u16(central, 0);
        u16(central, 0); u16(central, 0);
        u32(central, 0); u32(central, data.length); u32(central, data.length);
        u16(central, nameB.length); u16(central, 0); u16(central, 0);
        u16(central, 0); u16(central, 0); u32(central, 0);
        u32(central, offset);
        central.push(...nameB);
    }

    const eocd = [];
    u32(eocd, 0x06054b50); u16(eocd, 0); u16(eocd, 0); u16(eocd, files.length); u16(eocd, files.length);
    u32(eocd, central.length); u32(eocd, local.length); u16(eocd, 0);

    return new Uint8Array([...local, ...central, ...eocd]).buffer;
}

test('extractObjFromZip finds the obj entry among others', async () => {
    const zip = makeStoredZip('mesh.obj', 'v 5 5 5');
    const text = await geo.extractObjFromZip(zip);
    assert.ok(text.includes('v 5 5 5'));
});

//── ZIP fixture helpers (build a minimal STORED zip; CRC left 0, which the reader ignores) ──

function u16(arr, v) {
    arr.push(v & 0xff, (v >> 8) & 0xff);
}

function u32(arr, v) {
    arr.push(v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >> 24) & 0xff);
}

function makeStoredZip(name, text) {
    const enc = new TextEncoder();
    const nameB = Array.from(enc.encode(name));
    const data = Array.from(enc.encode(text));

    const local = [];
    u32(local, 0x04034b50); u16(local, 20); u16(local, 0); u16(local, 0);
    u16(local, 0); u16(local, 0);
    u32(local, 0); u32(local, data.length); u32(local, data.length);
    u16(local, nameB.length); u16(local, 0);
    local.push(...nameB, ...data);

    const central = [];
    u32(central, 0x02014b50); u16(central, 20); u16(central, 20); u16(central, 0); u16(central, 0);
    u16(central, 0); u16(central, 0);
    u32(central, 0); u32(central, data.length); u32(central, data.length);
    u16(central, nameB.length); u16(central, 0); u16(central, 0);
    u16(central, 0); u16(central, 0); u32(central, 0);
    u32(central, 0);
    central.push(...nameB);

    const eocd = [];
    u32(eocd, 0x06054b50); u16(eocd, 0); u16(eocd, 0); u16(eocd, 1); u16(eocd, 1);
    u32(eocd, central.length); u32(eocd, local.length); u16(eocd, 0);

    return new Uint8Array([...local, ...central, ...eocd]).buffer;
}

//── Geography: the Map and Globe layouts' math ─────────────────────────────────────────────────

test('geoProject maps the world onto the image plane, longitude across and latitude down', () => {
    const w = 2048, h = 1024;
    assert.deepStrictEqual(geo.geoProject(0, 0, w, h), { x: 1024, y: 512 });
    assert.deepStrictEqual(geo.geoProject(90, -180, w, h), { x: 0, y: 0 });
    assert.deepStrictEqual(geo.geoProject(-90, 180, w, h), { x: 2048, y: 1024 });
    //Vienna sits east of the meridian and north of the equator: right of center, above the middle.
    const vienna = geo.geoProject(48.2, 16.4, w, h);
    assert.ok(vienna.x > 1024 && vienna.y < 512);
});

test('geoToCartesian follows the sphere texture frame: lng 0 faces +x, lng 90 faces -z, north is +y', () => {
    const close = (a, b) => Math.abs(a - b) < 1e-9;
    const p0 = geo.geoToCartesian(0, 0, 100);
    assert.ok(close(p0.x, 100) && close(p0.y, 0) && close(p0.z, 0));
    const p90 = geo.geoToCartesian(0, 90, 100);
    assert.ok(close(p90.x, 0) && close(p90.y, 0) && close(p90.z, -100));
    const north = geo.geoToCartesian(90, 0, 100);
    assert.ok(close(north.x, 0) && close(north.y, 100) && close(north.z, 0));
    //Every point lands on the sphere.
    const v = geo.geoToCartesian(48.2, 16.4, 100);
    assert.ok(close(Math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z), 100));
});

test('geoArc bows every link outward, over the surface rather than through it', () => {
    const R = 220;
    const places = [[48.2, 16.4], [40.6, -73.8], [-33.9, 151.2], [35.7, 139.7], [0, 0], [89, 10], [-45, -170], [10, 179]];

    for (let i = 0; i < places.length; i++)
        for (let j = 0; j < places.length; j++) {
            if (i === j)
                continue;

            const a = geo.geoToCartesian(places[i][0], places[i][1], R);
            const b = geo.geoToCartesian(places[j][0], places[j][1], R);
            const arc = geo.geoArc(a, b, R);
            const cp = geo.curveControlPoint(a, b, arc.curvature, arc.curveRotation);
            const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, z: (a.z + b.z) / 2 };
            const outward = cp.x * mid.x + cp.y * mid.y + cp.z * mid.z;
            const apex = { x: (a.x + 2 * cp.x + b.x) / 4, y: (a.y + 2 * cp.y + b.y) / 4, z: (a.z + 2 * cp.z + b.z) / 4 };
            const apexRadius = Math.sqrt(apex.x * apex.x + apex.y * apex.y + apex.z * apex.z);

            assert.ok(outward > 0, `control point points outward for ${i}->${j}`);
            assert.ok(apexRadius > R * 1.001, `the arc's apex clears the surface for ${i}->${j} (${apexRadius.toFixed(1)})`);
        }
});

test('geoArc grows with distance and copes with the degenerate cases', () => {
    const R = 220;
    const near = geo.geoArc(geo.geoToCartesian(48.2, 16.4, R), geo.geoToCartesian(48.3, 16.5, R), R);
    const far = geo.geoArc(geo.geoToCartesian(48.2, 16.4, R), geo.geoToCartesian(-33.9, 151.2, R), R);
    assert.ok(far.curvature > near.curvature);

    const same = geo.geoArc(geo.geoToCartesian(1, 1, R), geo.geoToCartesian(1, 1, R), R);
    assert.deepStrictEqual(same, { curvature: 0, curveRotation: 0 });

    const antipodes = geo.geoArc(geo.geoToCartesian(0, 0, R), geo.geoToCartesian(0, 180, R), R);
    assert.ok(Number.isFinite(antipodes.curvature) && Number.isFinite(antipodes.curveRotation));
});

test('geoGraticuleStep refines as the map zooms in and never crowds the screen', () => {
    //At the fit zoom of a 2048-wide world in a 900px canvas, a degree is ~2.5px: 30° lines, 75px apart.
    assert.strictEqual(geo.geoGraticuleStep(2.5, 70), 30);
    //Ten times closer, 5° lines (2° would be 50px apart, under the floor); at 100px a degree, 1°.
    assert.strictEqual(geo.geoGraticuleStep(25, 70), 5);
    assert.strictEqual(geo.geoGraticuleStep(100, 70), 1);
    //Zoomed far out nothing fits under the minimum, and the coarsest step is the answer, not none.
    assert.strictEqual(geo.geoGraticuleStep(0.1, 70), 90);
});

test('geoDegreeLabel names the hemispheres and the special lines', () => {
    assert.strictEqual(geo.geoDegreeLabel(30, 'lon'), '30°E');
    assert.strictEqual(geo.geoDegreeLabel(-45, 'lon'), '45°W');
    assert.strictEqual(geo.geoDegreeLabel(48.5, 'lat'), '48.5°N');
    assert.strictEqual(geo.geoDegreeLabel(-33.87, 'lat'), '33.87°S');
    assert.strictEqual(geo.geoDegreeLabel(0, 'lat'), '0°');
    assert.strictEqual(geo.geoDegreeLabel(180, 'lon'), '180°');
    assert.strictEqual(geo.geoDegreeLabel(-180, 'lon'), '180°');
});

test('geoGreatCircle follows the shorter arc and bends toward the pole', () => {
    //London to New York: the arc's middle passes well north of both cities' latitudes.
    const points = geo.geoGreatCircle(51.5, -0.13, 40.7, -74.0, 10);

    assert.strictEqual(points.length, 11);
    assert.ok(Math.abs(points[0][0] - 51.5) < 1e-9 && Math.abs(points[0][1] + 0.13) < 1e-9);
    assert.ok(Math.abs(points[10][0] - 40.7) < 1e-9 && Math.abs(points[10][1] + 74.0) < 1e-9);
    assert.ok(points[5][0] > 52, 'the midpoint lies north of both ends: ' + points[5][0]);
    assert.ok(points[5][1] > -74 && points[5][1] < -0.13, 'the midpoint longitude lies between the ends');

    //Sydney to Los Angeles crosses the antimeridian; the flat map cannot follow it.
    assert.strictEqual(geo.geoCrossesAntimeridian(151.2, -118.2), true);
    assert.strictEqual(geo.geoCrossesAntimeridian(-0.13, -74.0), false);
});

test('geoEdgeBend gives a map edge Cytoscape segment controls that bow north in the northern hemisphere', () => {
    const bend = geo.geoEdgeBend(51.5, -0.13, 40.7, -74.0, 2048, 1024);

    assert.ok(bend, 'a transatlantic route bends');
    assert.strictEqual(bend.weights.length, bend.distances.length);
    assert.ok(bend.weights.length >= 5);

    //Weights run from the source to the target; every interior point sits between them.
    for (let i = 1; i < bend.weights.length; i++)
        assert.ok(bend.weights[i] > bend.weights[i - 1]);

    assert.ok(bend.weights[0] > 0 && bend.weights[bend.weights.length - 1] < 1);

    //London is east of New York, so the direction of travel is west (dx < 0) with y down; the arc
    //lies north — up the screen — which is the *right* of that direction, hence positive distances.
    //(Cytoscape: start + w·d + dist·(−dy, dx)/|d|; for d = (−1, 0) that normal is (0, −1), up.)
    const middle = bend.distances[Math.floor(bend.distances.length / 2)];

    assert.ok(middle > 20, 'the middle of the arc is clearly off the chord: ' + middle);
    assert.ok(bend.distances.every(d => d > 0));

    //Too short to bend (Amsterdam to Brussels), and across the antimeridian: no controls.
    assert.strictEqual(geo.geoEdgeBend(52.37, 4.9, 50.85, 4.35, 2048, 1024), null);
    assert.strictEqual(geo.geoEdgeBend(-33.9, 151.2, 34.1, -118.2, 2048, 1024), null);
});

test('sphereOccludes hides what is behind the globe and nothing on its near face', () => {
    const camera = { x: 0, y: 0, z: 748 };
    const drawn = 220 * 0.985;

    //A node on the near surface (facing the camera) and one on the far side, both at radius 220.
    assert.strictEqual(geo.sphereOccludes(camera, { x: 0, y: 0, z: 220 }, drawn), false);
    assert.strictEqual(geo.sphereOccludes(camera, { x: 0, y: 0, z: -220 }, drawn), true);

    //On the limb, just this side of the horizon, still visible; well around the back, hidden.
    assert.strictEqual(geo.sphereOccludes(camera, { x: 220, y: 0, z: 30 }, drawn), false);
    assert.strictEqual(geo.sphereOccludes(camera, { x: 150, y: 0, z: -160 }, drawn), true);

    //A label floating above the far side is hidden too; one above the near side is not.
    assert.strictEqual(geo.sphereOccludes(camera, { x: 0, y: 40, z: -260 }, drawn), true);
    assert.strictEqual(geo.sphereOccludes(camera, { x: 0, y: 40, z: 260 }, drawn), false);
});

//── Map tiles ─────────────────────────────────────────────────────────────────────────────────

test('geoTileUrl fills the zoom, column, row, TMS row, subdomain and key into the template', () => {
    assert.strictEqual(geo.geoTileUrl('https://t.example/{z}/{x}/{y}.png', 3, 5, 2), 'https://t.example/3/5/2.png');
    assert.strictEqual(geo.geoTileUrl('https://t.example/{z}/{x}/{-y}.png', 3, 5, 2), 'https://t.example/3/5/5.png');
    assert.strictEqual(geo.geoTileUrl('https://{s}.t.example/{z}/{x}/{y}.png', 1, 0, 0), 'https://a.t.example/1/0/0.png');
    assert.strictEqual(geo.geoTileUrl('https://{s}.t.example/{z}/{x}/{y}.png', 1, 1, 0), 'https://b.t.example/1/1/0.png');
    assert.strictEqual(geo.geoTileUrl('https://{s}.t.example/{z}/{x}/{y}.png', 2, 1, 1), 'https://c.t.example/2/1/1.png');
    assert.strictEqual(geo.geoTileUrl('https://t.example/{z}/{x}/{y}?key={key}&k2={key}', 0, 0, 0, 'a b&c'), 'https://t.example/0/0/0?key=a%20b%26c&k2=a%20b%26c');
});

//Google's copyright follows the view (2026-11-07): a view drawing Google's tiles says its zoom and bounds a moment after they
//settle, only when they changed, rounded and held inside the world; forgotten, it says the same view again for a new session.
test('geoTileViewReporter says a view once it settles, only when it changed, held inside the world', async () => {
    const said = [];
    const say = geo.geoTileViewReporter(view => said.push(view), 20);
    const settle = () => new Promise(resolve => setTimeout(resolve, 50));

    say(3, -200, 20.123, 89, -10.456);
    say(3, -200, 20.123, 89, -10.456);
    await settle();

    assert.deepStrictEqual(said, [{ zoom: 3, west: -180, east: 20.12, north: 85.05, south: -10.46 }]);

    say(3, -200, 20.123, 89, -10.456);
    await settle();

    assert.strictEqual(said.length, 1);

    //Moving on before it settled: only where it came to rest is said.
    say(4, 0, 10, 50, 40);
    say(5, 2, 8, 48, 42);
    await settle();

    assert.strictEqual(said.length, 2);
    assert.strictEqual(said[1].zoom, 5);

    say.forget();
    say(5, 2, 8, 48, 42);
    await settle();

    assert.strictEqual(said.length, 3);
});

test('geoTileLat and geoTileRow are each other\'s inverse, with the equator halfway down and the square ending at 85.05°', () => {
    assert.ok(Math.abs(geo.geoTileLat(0, 0) - geo.TILE_MAX_LAT) < 1e-9);
    assert.ok(Math.abs(geo.geoTileLat(1, 0) + geo.TILE_MAX_LAT) < 1e-9);
    assert.ok(Math.abs(geo.geoTileLat(2, 2)) < 1e-9);

    for (const lat of [-80, -45.5, -10, 0, 12.3, 46.05, 70]) {
        const row = geo.geoTileRow(lat, 7);
        assert.ok(Math.abs(geo.geoTileLat(row, 7) - lat) < 1e-9, String(lat));
    }

    //Beyond the square a latitude is held at its edge.
    assert.ok(Math.abs(geo.geoTileRow(89, 4)) < 1e-9);
    assert.ok(Math.abs(geo.geoTileRow(-89, 4) - 16) < 1e-9);
});

test('geoTileZoom picks the zoom whose tiles match the world\'s width on screen, within 0 and the server\'s most', () => {
    assert.strictEqual(geo.geoTileZoom(256, 19), 0);
    assert.strictEqual(geo.geoTileZoom(2048, 19), 3);
    assert.strictEqual(geo.geoTileZoom(2048 * 1.3, 19), 3);
    assert.strictEqual(geo.geoTileZoom(2048 * 1.5, 19), 4);
    assert.strictEqual(geo.geoTileZoom(100, 19), 0);
    assert.strictEqual(geo.geoTileZoom(256 * Math.pow(2, 25), 19), 19);
    assert.strictEqual(geo.geoTileZoom(0, 19), 0);
});

test('geoTileRange lists the tiles over a box, rows held to the square and columns wrapping over the antimeridian', () => {
    //The whole world at zoom 1 is its four tiles.
    const all = geo.geoTileRange(1, -180, 180, 90, -90).map(t => t.x + ',' + t.y).sort();
    assert.deepStrictEqual(all, ['0,0', '0,1', '1,0', '1,1']);

    //A box in the north-east quarter, ending on a tile's edge, takes only the tiles it overlaps.
    const ne = geo.geoTileRange(2, 0, 90, 70, 1).map(t => t.x + ',' + t.y).sort();
    assert.deepStrictEqual(ne, ['2,0', '2,1']);

    //Across the antimeridian: from 170° east to 170° west at zoom 2 is the last column and the first.
    const wrap = geo.geoTileRange(2, 170, -170, 10, -10).map(t => t.x + ',' + t.y).sort();
    assert.deepStrictEqual(wrap, ['0,1', '0,2', '3,1', '3,2']);

    //The first column again past the last is drawn east of the antimeridian, where the box goes on.
    const cols = geo.geoTileRange(2, 170, -170, 10, -10).filter(t => t.y === 1).map(t => [t.x, t.col]);
    assert.deepStrictEqual(cols, [[3, 3], [0, 4]]);
    assert.ok(geo.geoTileRange(2, -180, 180, 90, -90).every(t => t.col === t.x));
});

test('geoTileStrips stretches a tile\'s rows to the latitudes they cover, its columns to its longitudes', () => {
    const world = { x: 10, y: 20, w: 360, h: 180 };//a degree to the pixel
    const strips = geo.geoTileStrips(1, 1, 0, world, 4);

    assert.strictEqual(strips.length, 4);
    strips.forEach(s => {
        assert.strictEqual(s.x, 10 + 180);
        assert.strictEqual(s.w, 180);
    });

    //The tile runs from the square's top (85.05° north) to the equator, its strips end to end.
    assert.ok(Math.abs(strips[0].y - (20 + 90 - geo.TILE_MAX_LAT)) < 1e-9);
    assert.ok(Math.abs(strips[3].y + strips[3].h - (20 + 90)) < 1e-9);
    for (let i = 1; i < 4; i++)
        assert.ok(Math.abs(strips[i].y - (strips[i - 1].y + strips[i - 1].h)) < 1e-9);

    //Mercator stretches the north, so its top quarter of pixels covers far less latitude than its bottom quarter.
    assert.ok(strips[3].h > strips[0].h * 5);
    assert.deepStrictEqual(strips.map(s => [s.f0, s.f1]), [[0, 0.25], [0.25, 0.5], [0.5, 0.75], [0.75, 1]]);
});

test('geoTileStripCount cuts low-zoom tiles finely and leaves a street\'s tile whole', () => {
    //The world 1024 high: a zoom-0 tile is one band of 85° north to south, far from straight; zoom 10 is all but straight.
    assert.ok(geo.geoTileStripCount(0, 1024) >= 16);
    assert.ok(geo.geoTileStripCount(3, 1024) > geo.geoTileStripCount(6, 1024 * 8));
    assert.strictEqual(geo.geoTileStripCount(12, 1024 * 4096), 1);
    assert.ok(geo.geoTileStripCount(0, 1e9) <= 64);

    //Enough that no strip is off its true place by more than a quarter of a pixel: check by comparing a strip's linear
    //middle with the true one.
    for (const [z, h] of [[0, 1024], [2, 2048], [5, 16384]]) {
        const count = geo.geoTileStripCount(z, h);
        const world = { x: 0, y: 0, w: 2 * h, h: h };
        const n = Math.pow(2, z);
        const y = Math.floor(n / 4);
        geo.geoTileStrips(z, 0, y, world, count).forEach(s => {
            const linear = s.y + s.h / 2;
            const truth = (90 - geo.geoTileLat(y + (s.f0 + s.f1) / 2, z)) / 180 * h;
            assert.ok(Math.abs(linear - truth) <= 0.25, z + ': ' + Math.abs(linear - truth));
        });
    }
});

//A click on the Map or the Globe places a new component by the place on the earth under it (2026-11-08, the user: on the globe
//one went in with no place, drawn inside the earth): the two projections backward.
test('geoUnproject is geoProject backward, and null off the earth', () => {
    const p = geo.geoProject(48.2, -16.5, 2048, 1024);
    const back = geo.geoUnproject(p.x, p.y, 2048, 1024);

    assert.ok(Math.abs(back.lat - 48.2) < 1e-9);
    assert.ok(Math.abs(back.lng + 16.5) < 1e-9);
    assert.strictEqual(geo.geoUnproject(-1, 500, 2048, 1024), null);
    assert.strictEqual(geo.geoUnproject(2049, 500, 2048, 1024), null);
    assert.strictEqual(geo.geoUnproject(1000, -1, 2048, 1024), null);
    assert.strictEqual(geo.geoUnproject(1000, 1025, 2048, 1024), null);
});

test('cartesianToGeo is geoToCartesian backward, at any distance from the center', () => {
    for (const r of [1, 216.7, 900])
        for (const [lat, lng] of [[48.2, -16.5], [-33.87, 151.21], [0, 179], [70, -120]]) {
            const p = geo.geoToCartesian(lat, lng, r);
            const back = geo.cartesianToGeo(p.x, p.y, p.z);

            assert.ok(Math.abs(back.lat - lat) < 1e-9, `${lat} at ${r} came back as ${back.lat}`);
            assert.ok(Math.abs(back.lng - lng) < 1e-9, `${lng} at ${r} came back as ${back.lng}`);
        }
});
