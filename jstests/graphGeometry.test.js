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

test('parseObjData reads a plain (non-zip) OBJ arraybuffer', async () => {
    const buf = new TextEncoder().encode('v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3').buffer;
    const m = await geo.parseObjData(buf);
    assert.strictEqual(m.verts.length, 3);
    assert.strictEqual(m.faces.length, 1);
});

test('parseObjData reads the .obj from a stored ZIP', async () => {
    const zip = makeStoredZip('model.obj', 'v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3');
    const m = await geo.parseObjData(zip);
    assert.strictEqual(m.verts.length, 3);
    assert.strictEqual(m.faces.length, 1);
});

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
