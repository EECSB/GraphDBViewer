//Pure geometry/serialization helpers for the 3D export and model loading. No DOM or
//three.js dependencies, so they can be unit-tested under Node (see jstests/). Exposed
//as window.graphGeometry in the browser and as module.exports under Node.
(function (factory) {
    const api = factory();

    if (typeof module !== 'undefined' && module.exports)
        module.exports = api;

    if (typeof window !== 'undefined')
        window.graphGeometry = api;
})(function () {
    'use strict';

    //Rewrites common cloud "share page" links into their direct, CORS-fetchable form
    //so an anyone-with-link file can actually be downloaded by the browser.
    function normalizeModelUrl(url) {
        if (!url)
            return url;

        //OneDrive (1drv.ms / onedrive.live.com) -> anonymous shares content endpoint.
        if (url.indexOf('1drv.ms') !== -1 || url.indexOf('onedrive.live.com') !== -1) {
            let token = btoa(url);
            token = token.replace(/=+$/, '').replace(/\//g, '_').replace(/\+/g, '-');
            return 'https://api.onedrive.com/v1.0/shares/u!' + token + '/root/content';
        }

        //Dropbox share link -> direct-download host (CORS-enabled).
        if (url.indexOf('www.dropbox.com') !== -1) {
            let direct = url.replace('www.dropbox.com', 'dl.dropboxusercontent.com');
            direct = direct.replace('?dl=0', '').replace('&dl=0', '');
            return direct;
        }

        return url;
    }

    //Parses Wavefront OBJ text into { verts:[[x,y,z],...], faces:[[i,...],...] } with
    //1-based vertex indices, or null when it holds no vertices. Texture/normal refs and
    //negative (relative) indices are handled; everything else is ignored.
    function parseObj(text) {
        const verts = [];
        const faces = [];
        const rows = text.split('\n');

        rows.forEach(row => {
            const line = row.trim();
            if (line.length === 0 || line[0] === '#')
                return;

            const parts = line.split(/\s+/);
            if (parts[0] === 'v') {
                verts.push([parseFloat(parts[1]), parseFloat(parts[2]), parseFloat(parts[3])]);
            } else if (parts[0] === 'f') {
                const idx = [];
                for (let i = 1; i < parts.length; i++) {
                    const token = parts[i].split('/')[0];
                    let n = parseInt(token, 10);
                    if (n < 0)
                        n = verts.length + n + 1;

                    idx.push(n);
                }

                if (idx.length >= 3)
                    faces.push(idx);
            }
        });

        if (verts.length === 0)
            return null;

        return { verts: verts, faces: faces };
    }

    //Adds a UV sphere centered at (cx,cy,cz) to the mesh (tri indices are 0-based).
    function addSphere(mesh, cx, cy, cz, r) {
        const slices = 12;
        const stacks = 8;
        const cols = slices + 1;
        const base = mesh.verts.length;

        for (let lat = 0; lat <= stacks; lat++) {
            const theta = lat * Math.PI / stacks;
            const sinTheta = Math.sin(theta);
            const cosTheta = Math.cos(theta);

            for (let lon = 0; lon <= slices; lon++) {
                const phi = lon * 2 * Math.PI / slices;
                const x = cx + r * Math.cos(phi) * sinTheta;
                const y = cy + r * cosTheta;
                const z = cz + r * Math.sin(phi) * sinTheta;

                mesh.verts.push([x, y, z]);
            }
        }

        for (let lat = 0; lat < stacks; lat++) {
            for (let lon = 0; lon < slices; lon++) {
                const a = base + lat * cols + lon;
                const b = base + (lat + 1) * cols + lon;
                const c = base + (lat + 1) * cols + (lon + 1);
                const d = base + lat * cols + (lon + 1);

                mesh.tris.push([a, b, d]);
                mesh.tris.push([b, c, d]);
            }
        }
    }

    //Adds an open cylinder (tube) between points a and b to the mesh (tri indices are 0-based).
    function addTube(mesh, a, b, radius) {
        const radial = 8;
        const base = mesh.verts.length;

        let dx = b.x - a.x;
        let dy = b.y - a.y;
        let dz = b.z - a.z;
        const len = Math.sqrt(dx * dx + dy * dy + dz * dz);
        if (len === 0)
            return;

        dx /= len;
        dy /= len;
        dz /= len;

        //Pick an arbitrary vector not parallel to the axis, then build a perpendicular basis.
        let kx = 0, ky = 1, kz = 0;
        if (Math.abs(dy) > 0.9) {
            kx = 1;
            ky = 0;
            kz = 0;
        }

        //u = normalize(cross(d, k))
        let ux = dy * kz - dz * ky;
        let uy = dz * kx - dx * kz;
        let uz = dx * ky - dy * kx;
        const ulen = Math.sqrt(ux * ux + uy * uy + uz * uz);
        ux /= ulen;
        uy /= ulen;
        uz /= ulen;

        //v = cross(d, u)
        const vx = dy * uz - dz * uy;
        const vy = dz * ux - dx * uz;
        const vz = dx * uy - dy * ux;

        //Two rings of vertices, one at each endpoint.
        const ends = [a, b];
        ends.forEach(p => {
            for (let i = 0; i < radial; i++) {
                const ang = i * 2 * Math.PI / radial;
                const ca = Math.cos(ang);
                const sa = Math.sin(ang);

                const ox = (ux * ca + vx * sa) * radius;
                const oy = (uy * ca + vy * sa) * radius;
                const oz = (uz * ca + vz * sa) * radius;

                mesh.verts.push([p.x + ox, p.y + oy, p.z + oz]);
            }
        });

        //Side faces between the two rings.
        for (let i = 0; i < radial; i++) {
            const next = (i + 1) % radial;
            const a0 = base + i;
            const a1 = base + next;
            const b0 = base + radial + i;
            const b1 = base + radial + next;

            mesh.tris.push([a0, b0, b1]);
            mesh.tris.push([a0, b1, a1]);
        }
    }

    //Adds an external OBJ model to the mesh, centered at (cx,cy,cz) and scaled so its
    //largest dimension matches the sphere diameter (2r). Polygon faces are fan-triangulated.
    function addModel(mesh, model, cx, cy, cz, r) {
        const verts = model.verts;
        const base = mesh.verts.length;

        let minX = Infinity, minY = Infinity, minZ = Infinity;
        let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
        verts.forEach(v => {
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

        verts.forEach(v => {
            const x = (v[0] - mcx) * scale + cx;
            const y = (v[1] - mcy) * scale + cy;
            const z = (v[2] - mcz) * scale + cz;
            mesh.verts.push([x, y, z]);
        });

        //Model face indices are 1-based within the model; fan-triangulate to global 0-based.
        model.faces.forEach(face => {
            for (let i = 1; i + 1 < face.length; i++) {
                const a = base + face[0] - 1;
                const b = base + face[i] - 1;
                const c = base + face[i + 1] - 1;
                mesh.tris.push([a, b, c]);
            }
        });
    }

    //Serializes a mesh to Wavefront OBJ text (1-based indices).
    function meshToObj(mesh) {
        const lines = ['#Exported from Treeality'];

        mesh.verts.forEach(v => {
            lines.push('v ' + v[0].toFixed(4) + ' ' + v[1].toFixed(4) + ' ' + v[2].toFixed(4));
        });

        mesh.tris.forEach(t => {
            lines.push('f ' + (t[0] + 1) + ' ' + (t[1] + 1) + ' ' + (t[2] + 1));
        });

        return lines.join('\n');
    }

    //Serializes a mesh to ASCII PLY text (0-based indices).
    function meshToPly(mesh) {
        const lines = [];
        lines.push('ply');
        lines.push('format ascii 1.0');
        lines.push('comment Exported from Treeality');
        lines.push('element vertex ' + mesh.verts.length);
        lines.push('property float x');
        lines.push('property float y');
        lines.push('property float z');
        lines.push('element face ' + mesh.tris.length);
        lines.push('property list uchar int vertex_index');
        lines.push('end_header');

        mesh.verts.forEach(v => {
            lines.push(v[0].toFixed(4) + ' ' + v[1].toFixed(4) + ' ' + v[2].toFixed(4));
        });

        mesh.tris.forEach(t => {
            lines.push('3 ' + t[0] + ' ' + t[1] + ' ' + t[2]);
        });

        return lines.join('\n');
    }

    //Serializes a mesh to ASCII STL text (per-facet normals, no shared vertices).
    function meshToStl(mesh) {
        const lines = ['solid graph'];

        mesh.tris.forEach(t => {
            const a = mesh.verts[t[0]];
            const b = mesh.verts[t[1]];
            const c = mesh.verts[t[2]];

            //normal = normalize((b - a) x (c - a))
            const ux = b[0] - a[0];
            const uy = b[1] - a[1];
            const uz = b[2] - a[2];
            const vx = c[0] - a[0];
            const vy = c[1] - a[1];
            const vz = c[2] - a[2];

            let nx = uy * vz - uz * vy;
            let ny = uz * vx - ux * vz;
            let nz = ux * vy - uy * vx;
            let nlen = Math.sqrt(nx * nx + ny * ny + nz * nz);
            if (nlen === 0)
                nlen = 1;

            nx /= nlen;
            ny /= nlen;
            nz /= nlen;

            lines.push('facet normal ' + nx.toFixed(6) + ' ' + ny.toFixed(6) + ' ' + nz.toFixed(6));
            lines.push('  outer loop');
            lines.push('    vertex ' + a[0].toFixed(4) + ' ' + a[1].toFixed(4) + ' ' + a[2].toFixed(4));
            lines.push('    vertex ' + b[0].toFixed(4) + ' ' + b[1].toFixed(4) + ' ' + b[2].toFixed(4));
            lines.push('    vertex ' + c[0].toFixed(4) + ' ' + c[1].toFixed(4) + ' ' + c[2].toFixed(4));
            lines.push('  endloop');
            lines.push('endfacet');
        });

        lines.push('endsolid graph');
        return lines.join('\n');
    }

    //True when the bytes start with the ZIP local-file-header magic (PK\x03\x04).
    function isZip(bytes) {
        return bytes.length >= 4 && bytes[0] === 0x50 && bytes[1] === 0x4B && bytes[2] === 0x03 && bytes[3] === 0x04;
    }

    //Inflates a raw DEFLATE byte array via the browser/Node DecompressionStream.
    async function inflateRaw(bytes) {
        const ds = new DecompressionStream('deflate-raw');
        const stream = new Response(bytes).body.pipeThrough(ds);
        const buf = await new Response(stream).arrayBuffer();
        return new Uint8Array(buf);
    }

    //Extracts the first ".obj" entry's text from a ZIP arraybuffer (stored or deflated),
    //or null. A ".mtl" is ignored — the geometry-only pipeline doesn't apply materials.
    async function extractObjFromZip(arrayBuffer) {
        const dv = new DataView(arrayBuffer);
        const bytes = new Uint8Array(arrayBuffer);
        const len = bytes.length;

        //Find the End Of Central Directory record (0x06054b50), scanning back from the end.
        let eocd = -1;
        const minPos = Math.max(0, len - 22 - 65535);
        for (let i = len - 22; i >= minPos; i--) {
            if (dv.getUint32(i, true) === 0x06054b50) {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
            return null;

        const count = dv.getUint16(eocd + 10, true);
        let p = dv.getUint32(eocd + 16, true);
        const decoder = new TextDecoder();

        let entry = null;
        for (let i = 0; i < count; i++) {
            if (p + 46 > len || dv.getUint32(p, true) !== 0x02014b50)
                break;

            const method = dv.getUint16(p + 10, true);
            const compSize = dv.getUint32(p + 20, true);
            const nameLen = dv.getUint16(p + 28, true);
            const extraLen = dv.getUint16(p + 30, true);
            const commentLen = dv.getUint16(p + 32, true);
            const localOffset = dv.getUint32(p + 42, true);
            const name = decoder.decode(bytes.subarray(p + 46, p + 46 + nameLen));

            if (name.toLowerCase().endsWith('.obj')) {
                entry = { method: method, compSize: compSize, localOffset: localOffset };
                break;
            }

            p += 46 + nameLen + extraLen + commentLen;
        }

        if (!entry)
            return null;

        const lo = entry.localOffset;
        if (dv.getUint32(lo, true) !== 0x04034b50)
            return null;

        const lhNameLen = dv.getUint16(lo + 26, true);
        const lhExtraLen = dv.getUint16(lo + 28, true);
        const dataStart = lo + 30 + lhNameLen + lhExtraLen;
        const comp = new Uint8Array(arrayBuffer, dataStart, entry.compSize);

        let raw;
        if (entry.method === 0)
            raw = comp;
        else if (entry.method === 8)
            raw = await inflateRaw(comp);
        else
            return null;

        return decoder.decode(raw);
    }

    //Parses fetched model bytes (arraybuffer) into a model: a raw .obj, or a .zip that
    //contains an .obj (+ optional .mtl, which is not applied). Returns null on failure.
    async function parseObjData(arrayBuffer) {
        const bytes = new Uint8Array(arrayBuffer);

        let text;
        if (isZip(bytes))
            text = await extractObjFromZip(arrayBuffer);
        else
            text = new TextDecoder().decode(bytes);

        if (!text)
            return null;

        return parseObj(text);
    }

    //Base64-encodes a byte array in chunks (avoids call-stack limits on large inputs).
    function bytesToBase64(bytes) {
        let binary = '';
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        return btoa(binary);
    }

    //Serializes a mesh to a self-contained glTF 2.0 JSON string: positions (FLOAT VEC3)
    //and indices (UNSIGNED_INT) packed into one base64-embedded buffer. Geometry only.
    function meshToGltf(mesh) {
        const vCount = mesh.verts.length;
        const iCount = mesh.tris.length * 3;
        const posBytes = vCount * 3 * 4;
        const idxBytes = iCount * 4;

        const buffer = new ArrayBuffer(posBytes + idxBytes);
        const posView = new Float32Array(buffer, 0, vCount * 3);
        const idxView = new Uint32Array(buffer, posBytes, iCount);

        let minX = Infinity, minY = Infinity, minZ = Infinity;
        let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
        for (let i = 0; i < vCount; i++) {
            const v = mesh.verts[i];
            posView[i * 3] = v[0];
            posView[i * 3 + 1] = v[1];
            posView[i * 3 + 2] = v[2];

            minX = Math.min(minX, v[0]);
            minY = Math.min(minY, v[1]);
            minZ = Math.min(minZ, v[2]);
            maxX = Math.max(maxX, v[0]);
            maxY = Math.max(maxY, v[1]);
            maxZ = Math.max(maxZ, v[2]);
        }

        for (let i = 0; i < mesh.tris.length; i++) {
            const t = mesh.tris[i];
            idxView[i * 3] = t[0];
            idxView[i * 3 + 1] = t[1];
            idxView[i * 3 + 2] = t[2];
        }

        if (vCount === 0) {
            minX = 0;
            minY = 0;
            minZ = 0;
            maxX = 0;
            maxY = 0;
            maxZ = 0;
        }

        const gltf = {
            asset: { version: '2.0', generator: 'Treeality' },
            scene: 0,
            scenes: [{ nodes: [0] }],
            nodes: [{ mesh: 0 }],
            meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1, mode: 4 }] }],
            buffers: [{ byteLength: buffer.byteLength, uri: 'data:application/octet-stream;base64,' + bytesToBase64(new Uint8Array(buffer)) }],
            bufferViews: [
                { buffer: 0, byteOffset: 0, byteLength: posBytes, target: 34962 },
                { buffer: 0, byteOffset: posBytes, byteLength: idxBytes, target: 34963 }
            ],
            accessors: [
                { bufferView: 0, byteOffset: 0, componentType: 5126, count: vCount, type: 'VEC3', min: [minX, minY, minZ], max: [maxX, maxY, maxZ] },
                { bufferView: 1, byteOffset: 0, componentType: 5125, count: iCount, type: 'SCALAR' }
            ]
        };

        return JSON.stringify(gltf);
    }

    return {
        normalizeModelUrl: normalizeModelUrl,
        parseObj: parseObj,
        parseObjData: parseObjData,
        isZip: isZip,
        extractObjFromZip: extractObjFromZip,
        addSphere: addSphere,
        addTube: addTube,
        addModel: addModel,
        meshToObj: meshToObj,
        meshToPly: meshToPly,
        meshToStl: meshToStl,
        meshToGltf: meshToGltf
    };
});
