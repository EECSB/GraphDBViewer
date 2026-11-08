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
            if (parts[0] === 'v')
                verts.push([parseFloat(parts[1]), parseFloat(parts[2]), parseFloat(parts[3])]);
            else if (parts[0] === 'f') {
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

        for (let lat = 0; lat < stacks; lat++)
            for (let lon = 0; lon < slices; lon++) {
                const a = base + lat * cols + lon;
                const b = base + (lat + 1) * cols + lon;
                const c = base + (lat + 1) * cols + (lon + 1);
                const d = base + lat * cols + (lon + 1);

                mesh.tris.push([a, b, d]);
                mesh.tris.push([b, c, d]);
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

    //The entries of a ZIP arraybuffer, read from its central directory: each one's name and where its data is, for
    //zipRead to read one. Null when the file is no ZIP this can read.
    function zipEntries(arrayBuffer) {
        const dv = new DataView(arrayBuffer);
        const bytes = new Uint8Array(arrayBuffer);
        const len = bytes.length;

        if (len < 22)
            return null;

        //Find the End Of Central Directory record (0x06054b50), scanning back from the end.
        let eocd = -1;
        const minPos = Math.max(0, len - 22 - 65535);
        for (let i = len - 22; i >= minPos; i--)
            if (dv.getUint32(i, true) === 0x06054b50) {
                eocd = i;
                break;
            }

        if (eocd < 0)
            return null;

        const count = dv.getUint16(eocd + 10, true);
        let p = dv.getUint32(eocd + 16, true);
        const decoder = new TextDecoder();
        const entries = [];

        for (let i = 0; i < count; i++) {
            if (p + 46 > len || dv.getUint32(p, true) !== 0x02014b50)
                break;

            const nameLen = dv.getUint16(p + 28, true);
            const extraLen = dv.getUint16(p + 30, true);
            const commentLen = dv.getUint16(p + 32, true);

            entries.push({
                name: decoder.decode(bytes.subarray(p + 46, p + 46 + nameLen)),
                method: dv.getUint16(p + 10, true),
                compSize: dv.getUint32(p + 20, true),
                localOffset: dv.getUint32(p + 42, true)
            });

            p += 46 + nameLen + extraLen + commentLen;
        }

        return entries;
    }

    //One ZIP entry's bytes, stored or deflated, or null.
    async function zipRead(arrayBuffer, entry) {
        const dv = new DataView(arrayBuffer);
        const lo = entry.localOffset;

        if (lo + 30 > arrayBuffer.byteLength || dv.getUint32(lo, true) !== 0x04034b50)
            return null;

        const lhNameLen = dv.getUint16(lo + 26, true);
        const lhExtraLen = dv.getUint16(lo + 28, true);
        const dataStart = lo + 30 + lhNameLen + lhExtraLen;

        if (dataStart + entry.compSize > arrayBuffer.byteLength)
            return null;

        const comp = new Uint8Array(arrayBuffer, dataStart, entry.compSize);

        if (entry.method === 0)
            return comp;
        else if (entry.method === 8)
            return inflateRaw(comp);

        return null;
    }

    //The first entry whose name ends so, or null.
    function zipNamed(entries, suffix) {
        return entries.find(e => e.name.toLowerCase().endsWith(suffix)) || null;
    }

    //Extracts the first ".obj" entry's text from a ZIP arraybuffer (stored or deflated),
    //or null. A ".mtl" is ignored — the geometry-only pipeline doesn't apply materials.
    async function extractObjFromZip(arrayBuffer) {
        const entries = zipEntries(arrayBuffer);

        if (!entries)
            return null;

        const entry = zipNamed(entries, '.obj');

        if (!entry)
            return null;

        const raw = await zipRead(arrayBuffer, entry);

        if (!raw)
            return null;

        return new TextDecoder().decode(raw);
    }

    //A model in a ZIP: its .glb, else its .gltf with the buffers the .gltf names read out of the same ZIP, else its
    //.obj. A .gltf's buffer is named relative to the .gltf itself, as a link on a web page is.
    async function parseZipModel(arrayBuffer) {
        const entries = zipEntries(arrayBuffer);

        if (!entries)
            return null;

        const glb = zipNamed(entries, '.glb');

        if (glb) {
            const bytes = await zipRead(arrayBuffer, glb);

            if (!bytes)
                return null;

            return parseModelData(bytes.slice().buffer);
        }

        const gltf = zipNamed(entries, '.gltf');

        if (gltf) {
            const bytes = await zipRead(arrayBuffer, gltf);

            if (!bytes)
                return null;

            const folder = gltf.name.substring(0, gltf.name.lastIndexOf('/') + 1);
            const beside = async uri => {
                const wanted = zipPath(folder, uri);
                const entry = entries.find(e => e.name === wanted);

                if (!entry)
                    return null;

                return zipRead(arrayBuffer, entry);
            };

            return parseGltfText(new TextDecoder().decode(bytes), beside);
        }

        const text = await extractObjFromZip(arrayBuffer);

        if (!text)
            return null;

        return parseObj(text);
    }

    //A relative URI resolved against a folder inside a ZIP: percent escapes undone, "." and ".." followed.
    function zipPath(folder, uri) {
        let decoded = uri;

        try {
            decoded = decodeURIComponent(uri);
        }
        catch (e) { }

        const parts = [];

        for (const part of (folder + decoded).split('/')) {
            if (part === '..')
                parts.pop();
            else if (part !== '.' && part !== '')
                parts.push(part);
        }

        return parts.join('/');
    }

    //── glTF ──────────────────────────────────────────────────────────────────────────────────
    //A linked model may be glTF as well as OBJ: a .glb, or a .gltf whose buffers are embedded in it, sit beside it on
    //the web, or are in the same .zip. Read for its geometry alone, as an OBJ is: every triangle of the scene the file
    //opens on, placed by its nodes' transforms. Materials, textures, skins and animation are not read, and a file that
    //needs Draco or meshopt to be decoded is not read at all.

    const GLB_MAGIC = 0x46546C67;
    const GLB_JSON = 0x4E4F534A;
    const GLB_BIN = 0x004E4942;

    const GLTF_TRIANGLES = 4;
    const GLTF_TRIANGLE_STRIP = 5;
    const GLTF_TRIANGLE_FAN = 6;

    //Extensions a file may require that change nothing for geometry read through its accessors' types.
    const GLTF_UNDERSTOOD = ['KHR_mesh_quantization', 'KHR_materials_unlit', 'KHR_texture_transform'];

    const GLTF_WIDTHS = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT2: 4, MAT3: 9, MAT4: 16 };

    //Per component type: its size, how to read one, and what a normalized one is divided by.
    const GLTF_COMPONENTS = {
        5120: { size: 1, max: 127, read: (view, at) => view.getInt8(at) },
        5121: { size: 1, max: 255, read: (view, at) => view.getUint8(at) },
        5122: { size: 2, max: 32767, read: (view, at) => view.getInt16(at, true) },
        5123: { size: 2, max: 65535, read: (view, at) => view.getUint16(at, true) },
        5125: { size: 4, max: 4294967295, read: (view, at) => view.getUint32(at, true) },
        5126: { size: 4, max: 1, read: (view, at) => view.getFloat32(at, true) }
    };

    //True when the bytes are a GLB, glTF's binary container.
    function isGlb(bytes) {
        return bytes.length >= 12 && new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(0, true) === GLB_MAGIC;
    }

    //The GLB container's JSON and its BIN chunk, or null when it is damaged or not glTF 2.0.
    function readGlb(bytes) {
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);

        if (view.byteLength < 20 || view.getUint32(0, true) !== GLB_MAGIC || view.getUint32(4, true) !== 2)
            return null;

        let gltf = null;
        let bin = null;
        let at = 12;

        while (at + 8 <= view.byteLength) {
            const length = view.getUint32(at, true);
            const type = view.getUint32(at + 4, true);
            const start = bytes.byteOffset + at + 8;

            if (at + 8 + length > view.byteLength)
                return null;

            if (type === GLB_JSON) {
                try {
                    gltf = JSON.parse(new TextDecoder().decode(new Uint8Array(bytes.buffer, start, length)));
                }
                catch (e) {
                    return null;
                }
            }
            else if (type === GLB_BIN)
                bin = new Uint8Array(bytes.buffer, start, length);

            at += 8 + length;
        }

        if (!gltf)
            return null;

        return { gltf: gltf, bin: bin };
    }

    //A .gltf's text as a model, its buffers read by beside(uri) when they are not embedded.
    async function parseGltfText(text, beside) {
        let gltf;

        try {
            gltf = JSON.parse(text);
        }
        catch (e) {
            return null;
        }

        return parseGltf(gltf, null, beside);
    }

    //True when the text is a .gltf: a JSON object with glTF's required "asset".
    function looksLikeGltf(text) {
        const start = text.search(/\S/);

        return start >= 0 && text[start] === '{' && text.indexOf('"asset"') >= 0;
    }

    //A data: URI's bytes, base64 or percent-encoded.
    function dataUriBytes(uri) {
        const comma = uri.indexOf(',');

        if (comma < 0)
            return null;

        const head = uri.substring(0, comma);
        const body = uri.substring(comma + 1);

        if (head.endsWith(';base64')) {
            const binary = atob(body);
            const out = new Uint8Array(binary.length);

            for (let i = 0; i < binary.length; i++)
                out[i] = binary.charCodeAt(i);

            return out;
        }

        return new TextEncoder().encode(decodeURIComponent(body));
    }

    //Each buffer's bytes: the GLB's own chunk, an embedded data: URI, or a file beside the model, which beside(uri)
    //reads. Null when one cannot be had.
    async function gltfBuffers(gltf, bin, beside) {
        const buffers = [];

        for (const buffer of gltf.buffers || []) {
            let bytes = null;

            if (buffer.uri === undefined)
                bytes = bin;
            else if (buffer.uri.startsWith('data:'))
                bytes = dataUriBytes(buffer.uri);
            else if (beside)
                bytes = await beside(buffer.uri);

            if (!bytes || bytes.byteLength < (buffer.byteLength || 0))
                return null;

            buffers.push(bytes);
        }

        return buffers;
    }

    //An accessor's values as one flat array, normalized integers scaled to 0..1 or -1..1 and a sparse accessor's
    //substitutions applied. Null when its data runs past its buffer.
    function readGltfAccessor(gltf, buffers, index) {
        const accessor = (gltf.accessors || [])[index];

        if (!accessor)
            return null;

        const width = GLTF_WIDTHS[accessor.type];
        const component = GLTF_COMPONENTS[accessor.componentType];

        if (!width || !component)
            return null;

        const out = new Float64Array(accessor.count * width);
        const scale = accessor.normalized && component.max !== 1;

        const read = (viewIndex, offset, count, comp, w, stride, into) => {
            const view = (gltf.bufferViews || [])[viewIndex];

            if (!view || !buffers[view.buffer || 0])
                return false;

            const bytes = buffers[view.buffer || 0];
            const step = stride || view.byteStride || w * comp.size;
            const start = (view.byteOffset || 0) + offset;
            const end = start + (count - 1) * step + w * comp.size;

            if (count > 0 && (end > (view.byteOffset || 0) + view.byteLength || end > bytes.byteLength))
                return false;

            const data = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);

            for (let i = 0; i < count; i++)
                for (let k = 0; k < w; k++)
                    into(i, k, comp.read(data, start + i * step + k * comp.size));

            return true;
        };

        const put = (i, k, value) => {
            let v = value;

            if (scale)
                v = Math.max(value / component.max, -1);

            out[i * width + k] = v;
        };

        //An accessor with no data of its own is all zeros, and only its sparse substitutions say otherwise.
        if (accessor.bufferView !== undefined && !read(accessor.bufferView, accessor.byteOffset || 0, accessor.count, component, width, 0, put))
            return null;

        if (accessor.sparse) {
            const sparse = accessor.sparse;
            const indexComponent = GLTF_COMPONENTS[sparse.indices.componentType];
            const at = new Array(sparse.count);

            if (!indexComponent || !read(sparse.indices.bufferView, sparse.indices.byteOffset || 0, sparse.count, indexComponent, 1, indexComponent.size, (i, k, value) => { at[i] = value; }))
                return null;

            if (!read(sparse.values.bufferView, sparse.values.byteOffset || 0, sparse.count, component, width, width * component.size, (i, k, value) => put(at[i], k, value)))
                return null;
        }

        return out;
    }

    //A primitive's triangles as a flat list of indices, whatever way the file strings them together, or null.
    function gltfTriangles(gltf, buffers, primitive, vertexCount) {
        let indices;

        if (primitive.indices === undefined) {
            indices = new Array(vertexCount);

            for (let i = 0; i < vertexCount; i++)
                indices[i] = i;
        }
        else
            indices = readGltfAccessor(gltf, buffers, primitive.indices);

        if (!indices)
            return null;

        for (let i = 0; i < indices.length; i++)
            if (indices[i] >= vertexCount)
                return null;

        let mode = GLTF_TRIANGLES;

        if (primitive.mode !== undefined)
            mode = primitive.mode;

        if (mode === GLTF_TRIANGLES)
            return Array.from(indices).slice(0, indices.length - indices.length % 3);

        const out = [];

        //A strip's every other triangle turns the other way round, which keeps them all facing one side.
        for (let i = 2; i < indices.length; i++) {
            if (mode === GLTF_TRIANGLE_FAN)
                out.push(indices[0], indices[i - 1], indices[i]);
            else if (i % 2 === 0)
                out.push(indices[i - 2], indices[i - 1], indices[i]);
            else
                out.push(indices[i - 1], indices[i - 2], indices[i]);
        }

        return out;
    }

    //A node's own transform as a column-major 4x4: its matrix, or its translation, rotation and scale.
    function gltfLocalMatrix(node) {
        if (node.matrix)
            return node.matrix.slice();

        const t = node.translation || [0, 0, 0];
        const q = node.rotation || [0, 0, 0, 1];
        const s = node.scale || [1, 1, 1];
        const x = q[0];
        const y = q[1];
        const z = q[2];
        const w = q[3];

        return [
            (1 - 2 * (y * y + z * z)) * s[0], 2 * (x * y + z * w) * s[0], 2 * (x * z - y * w) * s[0], 0,
            2 * (x * y - z * w) * s[1], (1 - 2 * (x * x + z * z)) * s[1], 2 * (y * z + x * w) * s[1], 0,
            2 * (x * z + y * w) * s[2], 2 * (y * z - x * w) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
            t[0], t[1], t[2], 1
        ];
    }

    function gltfMultiply(a, b) {
        const out = new Array(16);

        for (let column = 0; column < 4; column++)
            for (let row = 0; row < 4; row++) {
                let sum = 0;

                for (let k = 0; k < 4; k++)
                    sum += a[k * 4 + row] * b[column * 4 + k];

                out[column * 4 + row] = sum;
            }

        return out;
    }

    //The nodes a scene starts from: the scene the file opens on, else its first, else every node no other node holds.
    function gltfRoots(gltf) {
        const scenes = gltf.scenes || [];
        let scene = scenes[0];

        if (gltf.scene !== undefined && scenes[gltf.scene])
            scene = scenes[gltf.scene];

        if (scene)
            return scene.nodes || [];

        const held = new Set();

        for (const node of gltf.nodes || [])
            for (const child of node.children || [])
                held.add(child);

        const roots = [];

        for (let i = 0; i < (gltf.nodes || []).length; i++)
            if (!held.has(i))
                roots.push(i);

        return roots;
    }

    //A glTF's scene as a model, { verts:[[x,y,z]...], faces:[[i,j,k]...] } with 1-based indices as an OBJ's, or null
    //when it draws no triangle or cannot be read. Its lines and points are not faces, and are left out.
    async function parseGltf(gltf, bin, beside) {
        if (!gltf || !gltf.asset || String(gltf.asset.version || '').split('.')[0] !== '2')
            return null;

        const required = (gltf.extensionsRequired || []).filter(name => GLTF_UNDERSTOOD.indexOf(name) < 0);

        if (required.length > 0) {
            console.warn('A glTF model needs ' + required.join(', ') + ', which the viewer does not read.');

            return null;
        }

        const buffers = await gltfBuffers(gltf, bin, beside);

        if (!buffers)
            return null;

        const verts = [];
        const faces = [];
        const nodes = gltf.nodes || [];
        const meshes = gltf.meshes || [];
        const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

        const place = (mesh, m) => {
            for (const primitive of mesh.primitives || []) {
                let mode = GLTF_TRIANGLES;

                if (primitive.mode !== undefined)
                    mode = primitive.mode;

                if (mode !== GLTF_TRIANGLES && mode !== GLTF_TRIANGLE_STRIP && mode !== GLTF_TRIANGLE_FAN)
                    continue;

                const position = (primitive.attributes || {}).POSITION;

                if (position === undefined)
                    continue;

                const points = readGltfAccessor(gltf, buffers, position);

                if (!points)
                    continue;

                const count = points.length / 3;
                const triangles = gltfTriangles(gltf, buffers, primitive, count);

                if (!triangles)
                    continue;

                const base = verts.length;

                for (let v = 0; v < count; v++) {
                    const x = points[v * 3];
                    const y = points[v * 3 + 1];
                    const z = points[v * 3 + 2];

                    verts.push([
                        m[0] * x + m[4] * y + m[8] * z + m[12],
                        m[1] * x + m[5] * y + m[9] * z + m[13],
                        m[2] * x + m[6] * y + m[10] * z + m[14]
                    ]);
                }

                for (let i = 0; i + 2 < triangles.length; i += 3)
                    faces.push([base + triangles[i] + 1, base + triangles[i + 1] + 1, base + triangles[i + 2] + 1]);
            }
        };

        //A node holding itself, which a damaged file can, is walked once.
        const walk = (index, parent, path) => {
            const node = nodes[index];

            if (!node || path.has(index))
                return;

            const m = gltfMultiply(parent, gltfLocalMatrix(node));

            if (node.mesh !== undefined && meshes[node.mesh])
                place(meshes[node.mesh], m);

            path.add(index);

            for (const child of node.children || [])
                walk(child, m, path);

            path.delete(index);
        };

        for (const root of gltfRoots(gltf))
            walk(root, identity, new Set());

        if (verts.length === 0 || faces.length === 0)
            return null;

        return { verts: verts, faces: faces };
    }

    //Fetched model bytes (an arraybuffer) as a model: a GLB, a .gltf, an OBJ, or a .zip holding one of them.
    //beside(uri) reads a .gltf's buffer that lives in a file beside it, as Uint8Array bytes or null; without it, only
    //an embedded buffer is read. Returns null on failure.
    async function parseModelData(arrayBuffer, beside) {
        const bytes = new Uint8Array(arrayBuffer);

        if (isGlb(bytes)) {
            const glb = readGlb(bytes);

            if (!glb)
                return null;

            return parseGltf(glb.gltf, glb.bin, beside);
        }
        else if (isZip(bytes))
            return parseZipModel(arrayBuffer);

        const text = new TextDecoder().decode(bytes);

        if (!text)
            return null;

        if (looksLikeGltf(text))
            return parseGltfText(text, beside);

        return parseObj(text);
    }

    //Base64-encodes a byte array in chunks (avoids call-stack limits on large inputs).
    function bytesToBase64(bytes) {
        let binary = '';
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk)
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));

        return btoa(binary);
    }

    //A mesh as glTF 2.0: its JSON, with no buffer location yet, and the one buffer it describes, positions (FLOAT VEC3)
    //then indices (UNSIGNED_INT). Geometry only.
    function meshGltfParts(mesh) {
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
            buffers: [{ byteLength: buffer.byteLength }],
            bufferViews: [
                { buffer: 0, byteOffset: 0, byteLength: posBytes, target: 34962 },
                { buffer: 0, byteOffset: posBytes, byteLength: idxBytes, target: 34963 }
            ],
            accessors: [
                { bufferView: 0, byteOffset: 0, componentType: 5126, count: vCount, type: 'VEC3', min: [minX, minY, minZ], max: [maxX, maxY, maxZ] },
                { bufferView: 1, byteOffset: 0, componentType: 5125, count: iCount, type: 'SCALAR' }
            ]
        };

        return { gltf: gltf, bin: new Uint8Array(buffer) };
    }

    //Serializes a mesh to a self-contained glTF 2.0 JSON string, its buffer embedded as base64.
    function meshToGltf(mesh) {
        const parts = meshGltfParts(mesh);

        parts.gltf.buffers[0].uri = 'data:application/octet-stream;base64,' + bytesToBase64(parts.bin);

        return JSON.stringify(parts.gltf);
    }

    //Serializes a mesh to a GLB, glTF's binary container: a 12-byte header, the JSON chunk padded with spaces and the
    //BIN chunk padded with zeros, each to four bytes, as the format asks. A quarter smaller than the .gltf, whose
    //buffer is base64.
    function meshToGlb(mesh) {
        const parts = meshGltfParts(mesh);
        const json = new TextEncoder().encode(JSON.stringify(parts.gltf));
        const jsonLength = Math.ceil(json.length / 4) * 4;
        const binLength = Math.ceil(parts.bin.length / 4) * 4;
        const total = 12 + 8 + jsonLength + 8 + binLength;
        const out = new Uint8Array(total);
        const view = new DataView(out.buffer);

        view.setUint32(0, GLB_MAGIC, true);
        view.setUint32(4, 2, true);
        view.setUint32(8, total, true);

        view.setUint32(12, jsonLength, true);
        view.setUint32(16, GLB_JSON, true);
        out.set(json, 20);
        out.fill(0x20, 20 + json.length, 20 + jsonLength);

        const binAt = 20 + jsonLength;

        view.setUint32(binAt, binLength, true);
        view.setUint32(binAt + 4, GLB_BIN, true);
        out.set(parts.bin, binAt + 8);

        return out;
    }

    //── Geography ─────────────────────────────────────────────────────────────────────────────
    //The Map and Globe layouts place a vertex by its latitude and longitude (GeoCoordinates.cs says
    //where those are read from). Pure, so the projection and the arc math are pinned under Node.

    //Equirectangular projection onto a width×height plane, which is how the bundled world image is
    //drawn: longitude runs left to right across the width, latitude top to bottom down the height.
    function geoProject(lat, lng, width, height) {
        return { x: (lng + 180) / 360 * width, y: (90 - lat) / 180 * height };
    }

    //A point on a sphere of radius r, in the frame an unrotated three.js SphereGeometry drapes an
    //equirectangular texture in: longitude 0 faces +x, longitude 90 east faces -z, the north pole is
    //+y. Derived from the geometry's own UV mapping, so a node lands on its country.
    function geoToCartesian(lat, lng, r) {
        const latR = lat * Math.PI / 180;
        const lngR = lng * Math.PI / 180;

        return {
            x: r * Math.cos(latR) * Math.cos(lngR),
            y: r * Math.sin(latR),
            z: -r * Math.cos(latR) * Math.sin(lngR)
        };
    }

    //Where on the earth a point of geoProject's plane is: { lat, lng }, or null off the earth. A click on the Map places a
    //new vertex by it.
    function geoUnproject(x, y, width, height) {
        const lng = x / width * 360 - 180;
        const lat = 90 - y / height * 180;

        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return null;

        return { lat: lat, lng: lng };
    }

    //Where on the earth a point of geoToCartesian's frame is, whatever its distance from the center: { lat, lng }. A
    //click on the Globe places a new vertex by it.
    function cartesianToGeo(x, y, z) {
        const r = Math.hypot(x, y, z);

        return {
            lat: Math.asin(y / r) * 180 / Math.PI,
            lng: Math.atan2(-z, x) * 180 / Math.PI
        };
    }

    //── Map tiles ─────────────────────────────────────────────────────────────────────────────
    //A tile server set in Settings (MapTilesPreference.cs) serves the earth as Web Mercator tiles: at zoom z the world is
    //2^z × 2^z tiles of 256 pixels, x from the antimeridian eastward, y from the north. The Map and the Globe are
    //equirectangular, so a tile is drawn as horizontal strips, each stretched to the latitudes its rows cover; longitude
    //is linear in both projections, so only the height of each strip changes. Mercator stops at about 85.05° north and
    //south, and the caps beyond stay the views' own background.
    const TILE_MAX_LAT = 85.0511287798066;

    //A tile's URL from the template: {z}, {x}, {y}, {-y} (the row counted from the south, as TMS servers number them),
    //{s} (a, b or c, spread across tiles so a browser's connections go to several hosts) and {key}.
    function geoTileUrl(template, z, x, y, key) {
        const n = Math.pow(2, z);
        const subdomains = 'abc';

        return template
            .replace(/\{z\}/g, String(z))
            .replace(/\{x\}/g, String(x))
            .replace(/\{-y\}/g, String(n - 1 - y))
            .replace(/\{y\}/g, String(y))
            .replace(/\{s\}/g, subdomains[(x + y) % subdomains.length])
            .replace(/\{key\}/g, encodeURIComponent(key || ''));
    }

    //Google's copyright follows what is on screen (GoogleMapTiles.cs, 2026-11-07), so a view drawing its tiles says what it
    //shows: the zoom and the bounds, `delay` ms after they settle, and only when they differ from the last said. Bounds are
    //rounded to a hundredth of a degree, latitudes held inside the Mercator square and longitudes inside the world. forget()
    //lets the same view be said again, for a new session.
    function geoTileViewReporter(send, delay) {
        let said = '';
        let timer = null;
        const round = v => Math.round(v * 100) / 100;
        const inside = (v, most) => Math.max(-most, Math.min(most, v));

        const say = function (z, west, east, north, south) {
            const view = {
                zoom: z,
                west: round(inside(west, 180)),
                east: round(inside(east, 180)),
                north: round(inside(north, TILE_MAX_LAT)),
                south: round(inside(south, TILE_MAX_LAT))
            };
            const key = [view.zoom, view.west, view.east, view.north, view.south].join(',');

            if (key === said)
                return;

            said = key;
            clearTimeout(timer);
            timer = setTimeout(() => send(view), delay);
        };

        say.forget = function () {
            said = '';
            clearTimeout(timer);
        };

        return say;
    }

    //The latitude of a fractional tile row at zoom z: row 0 is the top of the Mercator square, row 2^z its bottom.
    function geoTileLat(row, z) {
        const n = Math.PI * (1 - 2 * row / Math.pow(2, z));

        return Math.atan(Math.sinh(n)) * 180 / Math.PI;
    }

    //The fractional tile row a latitude falls in at zoom z, held inside the Mercator square.
    function geoTileRow(lat, z) {
        const clamped = Math.max(-TILE_MAX_LAT, Math.min(TILE_MAX_LAT, lat));
        const r = clamped * Math.PI / 180;

        return (1 - Math.log(Math.tan(r) + 1 / Math.cos(r)) / Math.PI) / 2 * Math.pow(2, z);
    }

    //The tile zoom whose pixels best match a world drawn `worldPixels` wide on screen (device pixels), between 0 and
    //maxZoom: the zoom at which 2^z tiles of 256 cover that width, rounded, so tiles are never shown at under 0.7 or over
    //1.4 of their own size.
    function geoTileZoom(worldPixels, maxZoom) {
        if (!(worldPixels > 0))
            return 0;

        const z = Math.round(Math.log2(worldPixels / 256));

        return Math.max(0, Math.min(maxZoom, z));
    }

    //The tiles at zoom z covering a box of longitudes and latitudes (west may exceed east: the box then crosses the
    //antimeridian, and its columns wrap: x is the tile's column, col the same counted on past the last one, which is
    //where it is drawn east of the antimeridian). Rows are held to the Mercator square, columns to the world.
    function geoTileRange(z, west, east, north, south) {
        const n = Math.pow(2, z);
        const col = lon => (lon + 180) / 360 * n;
        const clamp = v => Math.max(0, Math.min(n - 1, v));
        const top = clamp(Math.floor(geoTileRow(north, z)));
        const bottom = clamp(Math.ceil(geoTileRow(south, z)) - 1);
        const first = clamp(Math.floor(col(west)));
        const tiles = [];
        let last = clamp(Math.ceil(col(east)) - 1);

        if (east < west)
            last += n;

        for (let y = top; y <= bottom; y++)
            for (let c = first; c <= last; c++)
                tiles.push({ z: z, x: c % n, y: y, col: c });

        return tiles;
    }

    //Where tile (z, x, y) lands on an equirectangular world drawn in the rectangle `world` ({x, y, w, h}, the whole globe
    //from 180° west and 90° north): `strips` horizontal bands, each with the fraction of the tile's height it takes its
    //pixels from (f0 to f1) and the rectangle it is drawn into.
    function geoTileStrips(z, x, y, world, strips) {
        const n = Math.pow(2, z);
        const left = world.x + x / n * world.w;
        const right = world.x + (x + 1) / n * world.w;
        const toY = lat => world.y + (90 - lat) / 180 * world.h;
        const out = [];

        for (let i = 0; i < strips; i++) {
            const f0 = i / strips;
            const f1 = (i + 1) / strips;
            const top = toY(geoTileLat(y + f0, z));
            const bottom = toY(geoTileLat(y + f1, z));

            out.push({ f0: f0, f1: f1, x: left, y: top, w: right - left, h: bottom - top });
        }

        return out;
    }

    //How many strips a tile at zoom z needs on a world `worldHeight` pixels high so no row is drawn more than a quarter of
    //a pixel from its true latitude. Inside one strip the latitude is taken as linear in the row; the error of that is at
    //most (strip's Mercator span)² / 16 radians, so the count grows with the span and the scale, from one at street zooms
    //to dozens for the few tiles of the whole world.
    function geoTileStripCount(z, worldHeight) {
        const span = 2 * Math.PI / Math.pow(2, z);
        const pixelsPerRadian = worldHeight / Math.PI;
        const strips = Math.ceil(Math.sqrt(span * span / 16 * pixelsPerRadian / 0.25));

        return Math.max(1, Math.min(64, strips));
    }

    function vSub(a, b) {
        return { x: a.x - b.x, y: a.y - b.y, z: a.z - b.z };
    }

    function vAdd(a, b) {
        return { x: a.x + b.x, y: a.y + b.y, z: a.z + b.z };
    }

    function vScale(a, s) {
        return { x: a.x * s, y: a.y * s, z: a.z * s };
    }

    function vDot(a, b) {
        return a.x * b.x + a.y * b.y + a.z * b.z;
    }

    function vCross(a, b) {
        return { x: a.y * b.z - a.z * b.y, y: a.z * b.x - a.x * b.z, z: a.x * b.y - a.y * b.x };
    }

    function vLength(a) {
        return Math.sqrt(vDot(a, a));
    }

    //The bow of a link between two points on a sphere of radius r, in the terms 3d-force-graph draws
    //a curved link with: a curvature (the control point's distance as a fraction of the chord) and a
    //rotation of that control point around the chord. Chosen so the arc bows outward from the sphere's
    //center — over the surface rather than through it — with an altitude that grows with the distance,
    //so neighbors stay low and antipodes arc high.
    //
    //The rotation is solved against the library's own construction: the control point starts as
    //(chord · curvature) × axis, where the axis is +z unless the chord runs purely along z, and is then
    //turned about the chord by the rotation. This finds the turn that points it outward at the midpoint.
    function geoArc(start, end, r) {
        const chord = vSub(end, start);
        const length = vLength(chord);

        if (length === 0)
            return { curvature: 0, curveRotation: 0 };

        //Central angle between the two points, from the chord of a sphere of radius r.
        const theta = 2 * Math.asin(Math.min(1, length / (2 * r)));
        const half = theta / 2;

        let axis;

        if (chord.x !== 0 || chord.y !== 0)
            axis = { x: 0, y: 0, z: 1 };
        else
            axis = { x: 0, y: 1, z: 0 };

        //Apex altitude above the surface, as a fraction of r: a quarter of the radius at the antipode.
        //A quadratic Bezier's apex sits halfway from the chord's midpoint to the control point, so the
        //control point has to reach twice that height: |mid| + offset / 2 = r · (1 + altitude). The
        //offset the library builds is |chord × axis| · c — shorter than the chord by the sine of the
        //angle between them — so c is solved against that, not against the chord.
        const altitude = 0.25 * theta / Math.PI;
        const reach = vLength(vCross(chord, axis));
        let curvature = 2 * r * (1 + altitude - Math.cos(half)) / Math.max(1e-9, reach);

        //A chord nearly parallel to the axis has almost no reach; rather than a wild curvature, the
        //arc is allowed to dip for that rare pair.
        curvature = Math.min(2.5, Math.max(0.05, curvature));

        const mid = vScale(vAdd(start, end), 0.5);

        //Antipodes: the midpoint is the center, and every direction is equally outward.
        if (vLength(mid) < 1e-9)
            return { curvature: curvature, curveRotation: 0 };

        const base = vCross(vScale(chord, curvature), axis);
        const unitChord = vScale(chord, 1 / length);

        //The signed angle from where the library starts the control point to the outward direction,
        //measured about the chord — both are perpendicular to it, mid because |start| = |end|.
        const curveRotation = Math.atan2(vDot(vCross(base, mid), unitChord), vDot(base, mid));

        return { curvature: curvature, curveRotation: curveRotation };
    }

    //The graticule's spacing in degrees: the coarsest step from a sensible ladder whose lines still sit
    //at least minSpacingPx apart on screen, so zooming in refines the grid rather than crowding it.
    function geoGraticuleStep(pixelsPerDegree, minSpacingPx) {
        const ladder = [90, 45, 30, 15, 10, 5, 2, 1, 0.5, 0.25, 0.1, 0.05];
        let chosen = ladder[0];

        for (let i = 0; i < ladder.length; i++)
            if (ladder[i] * pixelsPerDegree >= minSpacingPx)
                chosen = ladder[i];
            else
                break;

        return chosen;
    }

    //"30°E", "48.5°S", "0°" — how a graticule line is labeled. Trailing zeros go; the equator and the
    //prime meridian are just "0°"; the antimeridian is "180°" rather than east or west.
    function geoDegreeLabel(degrees, axis) {
        const magnitude = Math.abs(degrees);
        let text = String(Math.round(magnitude * 100) / 100);

        if (magnitude === 0)
            return '0°';

        if (axis === 'lon' && magnitude === 180)
            return '180°';

        let hemisphere;

        if (axis === 'lat' && degrees > 0)
            hemisphere = 'N';
        else if (axis === 'lat')
            hemisphere = 'S';
        else if (degrees > 0)
            hemisphere = 'E';
        else
            hemisphere = 'W';

        return text + '°' + hemisphere;
    }

    //Points along the shorter great-circle arc from one place to another — n segments, n + 1 points of
    //[lat, lng] — by spherical interpolation of the two unit vectors in geoToCartesian's frame.
    function geoGreatCircle(lat1, lng1, lat2, lng2, n) {
        const a = geoToCartesian(lat1, lng1, 1);
        const b = geoToCartesian(lat2, lng2, 1);
        const dot = Math.max(-1, Math.min(1, a.x * b.x + a.y * b.y + a.z * b.z));
        const omega = Math.acos(dot);

        if (omega < 1e-9 || n < 1)
            return [[lat1, lng1], [lat2, lng2]];

        const points = [];

        for (let i = 0; i <= n; i++) {
            const f = i / n;
            const s1 = Math.sin((1 - f) * omega) / Math.sin(omega);
            const s2 = Math.sin(f * omega) / Math.sin(omega);
            const x = s1 * a.x + s2 * b.x;
            const y = s1 * a.y + s2 * b.y;
            const z = s1 * a.z + s2 * b.z;
            const lat = Math.asin(Math.max(-1, Math.min(1, y))) * 180 / Math.PI;
            const lng = Math.atan2(-z, x) * 180 / Math.PI;

            points.push([lat, lng]);
        }

        return points;
    }

    //Whether the shorter arc between two longitudes crosses the antimeridian — where a flat map, which
    //ends there, cannot follow it.
    function geoCrossesAntimeridian(lng1, lng2) {
        return Math.abs(lng1 - lng2) > 180;
    }

    //How a 2D map edge bends to follow the great circle: Cytoscape's `segments` curve, whose points are
    //given relative to the straight line from source to target — a weight along it (0 at the source,
    //1 at the target) and a signed distance across it. Cytoscape places a point at
    //start + weight·(end − start) + distance·(−dy, dx)/|d|, so with y down a positive distance is on the
    //right of the direction of travel. Null when the route is too short to bend visibly, or when its
    //arc crosses the antimeridian and the straight line is the honest fallback on a map that ends there.
    function geoEdgeBend(lat1, lng1, lat2, lng2, w, h) {
        if (geoCrossesAntimeridian(lng1, lng2))
            return null;

        const a = geoToCartesian(lat1, lng1, 1);
        const b = geoToCartesian(lat2, lng2, 1);
        const dot = Math.max(-1, Math.min(1, a.x * b.x + a.y * b.y + a.z * b.z));
        const omegaDeg = Math.acos(dot) * 180 / Math.PI;

        if (omegaDeg < 3)
            return null;

        const n = Math.max(2, Math.min(24, Math.round(omegaDeg / 5)));
        const points = geoGreatCircle(lat1, lng1, lat2, lng2, n);
        const s = geoProject(lat1, lng1, w, h);
        const t = geoProject(lat2, lng2, w, h);
        const dx = t.x - s.x;
        const dy = t.y - s.y;
        const len2 = dx * dx + dy * dy;
        const len = Math.sqrt(len2);

        if (len < 1)
            return null;

        const weights = [];
        const distances = [];

        for (let i = 1; i < points.length - 1; i++) {
            const p = geoProject(points[i][0], points[i][1], w, h);
            const px = p.x - s.x;
            const py = p.y - s.y;

            weights.push(Math.round((px * dx + py * dy) / len2 * 1000) / 1000);
            distances.push(Math.round((py * dx - px * dy) / len * 10) / 10);
        }

        return { weights: weights, distances: distances };
    }

    //Whether a sphere of the given radius at the origin stands between a camera and a point: the
    //segment from the camera to the point enters the sphere before it gets there. A globe label on
    //the far side is hidden by this; a node on the near surface, just outside the drawn sphere, is not.
    function sphereOccludes(camera, point, radius) {
        const dx = point.x - camera.x;
        const dy = point.y - camera.y;
        const dz = point.z - camera.z;
        const a = dx * dx + dy * dy + dz * dz;

        if (a === 0)
            return false;

        const b = 2 * (camera.x * dx + camera.y * dy + camera.z * dz);
        const c = camera.x * camera.x + camera.y * camera.y + camera.z * camera.z - radius * radius;
        const discriminant = b * b - 4 * a * c;

        if (discriminant < 0)
            return false;

        const t = (-b - Math.sqrt(discriminant)) / (2 * a);

        return t > 0 && t < 1;
    }

    //Where 3d-force-graph puts the control point for a given curvature and rotation — the library's
    //construction, restated, so a test can check that geoArc's answer really lands outward.
    function curveControlPoint(start, end, curvature, curveRotation) {
        const chord = vSub(end, start);
        const length = vLength(chord);
        let axis;

        if (chord.x !== 0 || chord.y !== 0)
            axis = { x: 0, y: 0, z: 1 };
        else
            axis = { x: 0, y: 1, z: 0 };

        const base = vCross(vScale(chord, curvature), axis);
        const unit = vScale(chord, 1 / length);

        //Rodrigues' rotation of base about the unit chord by curveRotation.
        const cos = Math.cos(curveRotation);
        const sin = Math.sin(curveRotation);
        const rotated = vAdd(vAdd(vScale(base, cos), vScale(vCross(unit, base), sin)), vScale(unit, vDot(unit, base) * (1 - cos)));

        return vAdd(rotated, vScale(vAdd(start, end), 0.5));
    }

    //── Detailed view's card ────────────────────────────────────────────────────────────────────────────────────────────
    //Show details on canvas (2026-11-06, the user) draws a vertex as a small card: a head in its color with its label and
    //id over its name, and a row for each property under it. On the live 2D canvas the card is HTML (CytoscapeInterop.js's
    //makeDetailsCard); what has no HTML to show it lays the card out here and paints it: an image export of the 2D canvas,
    //and the 3D view, where the card is a picture facing the camera (2026-11-07, the user's "do the loose ends"). The
    //layout follows the HTML card's stylesheet (app.css, .gdbv-canvas-details) number for number, so the two agree.
    const DETAILS_ROWS_MOST = 10;
    const DETAILS_VALUE_MOST = 200;//characters put in a value at all; the card's width cuts it shorter
    const DETAILS_WIDTH_LEAST = 72;
    const DETAILS_WIDTH_MOST = 200;
    const DETAILS_PAD = 8;
    const DETAILS_META_LINE = 8 * 1.3;
    const DETAILS_TITLE_LINE = 12 * 1.35;
    const DETAILS_HEAD = 3 + DETAILS_META_LINE + DETAILS_TITLE_LINE + 4;
    const DETAILS_ROW = 10 * 1.35 + 4;
    const DETAILS_MORE_LINE = 9 * 1.35;
    const DETAILS_SELECTED_HEAD = '#fd7e14';
    const DETAILS_SELECTED_RING = '#dc3545';

    //What a card says of an element: its kind, its id (null when the title already is it, so it is said once), its title,
    //and a row for each property but the viewer's own and but the one the title shows, values cut short, the rows past
    //DETAILS_ROWS_MOST counted as more.
    function detailsCardContent(element) {
        const id = String(element.id);
        const kind = String(element.kind || 'vertex');
        let title = String(element.label || '');
        let shownId = id;

        //A vertex with no display property is drawn as "label (id)": its title is then its id, said once.
        if (!title || title === kind + ' (' + id + ')') {
            title = id;
            shownId = null;
        }

        const props = element.properties || {};
        const rows = [];

        Object.keys(props).forEach(function (key) {
            if (key.indexOf('gdbv') === 0)
                return;

            let text = props[key];

            if (text !== null && typeof text === 'object')
                text = JSON.stringify(text);

            text = String(text);

            if (text === title)
                return;

            if (text.length > DETAILS_VALUE_MOST)
                text = text.slice(0, DETAILS_VALUE_MOST - 1) + '…';

            rows.push([key, text]);
        });

        return { kind: kind, id: shownId, title: title, rows: rows.slice(0, DETAILS_ROWS_MOST), more: Math.max(0, rows.length - DETAILS_ROWS_MOST) };
    }

    //Text cut at room pixels, an ellipsis ending what was cut; a hundredth of a pixel over is the sums rounding, not too long.
    function fitDetailsText(text, font, room, measure) {
        if (measure(text, font) <= room + 0.01)
            return text;

        let cut = text.length;

        while (cut > 0 && measure(text.slice(0, cut) + '…', font) > room + 0.01)
            cut--;

        return text.slice(0, cut) + '…';
    }

    //A card laid out as shapes in its own pixels: { width, height, shapes }, each shape a rect, a line or a text, in the
    //order they are painted. look is { color, selected, bg, fg, muted, line, family }; measure(text, font) is a text's
    //width; width, when given, is the card's (the live card's, so an export cuts its text where the screen does), else it
    //is as wide as what it says, between DETAILS_WIDTH_LEAST and DETAILS_WIDTH_MOST.
    function detailsCardLayout(content, look, measure, width) {
        const family = look.family || 'sans-serif';
        const fonts = {
            meta: '8px ' + family,
            title: '600 12px ' + family,
            key: '9px ' + family,
            value: '10px ' + family,
            more: '9px ' + family
        };
        let idText = null;

        if (content.id !== null)
            idText = '#' + content.id;

        let metaWidth = measure(content.kind, fonts.meta);
        let idWidth = 0;

        if (idText !== null) {
            idWidth = measure(idText, fonts.meta);
            metaWidth += DETAILS_PAD + idWidth;
        }

        let keyWidth = 0;
        let valueWidth = 0;

        content.rows.forEach(function (row) {
            keyWidth = Math.max(keyWidth, measure(row[0], fonts.key));
            valueWidth = Math.max(valueWidth, measure(row[1], fonts.value));
        });

        let inner = Math.max(metaWidth, measure(content.title, fonts.title)) + 2 * DETAILS_PAD;

        if (content.rows.length > 0)
            inner = Math.max(inner, keyWidth + valueWidth + 3 * DETAILS_PAD);

        let moreText = null;

        if (content.more > 0) {
            moreText = '+' + content.more + ' more';
            inner = Math.max(inner, measure(moreText, fonts.more) + 2 * DETAILS_PAD);
        }

        let w = width;

        if (!(w > 0))
            w = Math.min(DETAILS_WIDTH_MOST, Math.max(DETAILS_WIDTH_LEAST, Math.ceil(inner + 2)));

        let headColor = look.color || '#0d6efd';
        let ringColor = headColor;

        if (look.selected) {
            headColor = DETAILS_SELECTED_HEAD;
            ringColor = DETAILS_SELECTED_RING;
        }

        const left = 1 + DETAILS_PAD;
        const room = w - 2 - 2 * DETAILS_PAD;
        const texts = [];
        const lines = [];
        let y = 1 + 3 + DETAILS_META_LINE / 2;

        texts.push({ type: 'text', x: left, y: y, text: fitDetailsText(content.kind, fonts.meta, Math.max(0, room - idWidth - DETAILS_PAD), measure), font: fonts.meta, size: 8, weight: 400, fill: '#ffffff', alpha: 0.85, align: 'left' });

        if (idText !== null)
            texts.push({ type: 'text', x: w - 1 - DETAILS_PAD, y: y, text: idText, font: fonts.meta, size: 8, weight: 400, fill: '#ffffff', alpha: 0.85, align: 'right' });

        y = 1 + 3 + DETAILS_META_LINE + DETAILS_TITLE_LINE / 2;
        texts.push({ type: 'text', x: left, y: y, text: fitDetailsText(content.title, fonts.title, room, measure), font: fonts.title, size: 12, weight: 600, fill: '#ffffff', alpha: 1, align: 'left' });

        //The key's column is as wide as its widest key, but never past half the card.
        const keyColumn = Math.min(keyWidth + DETAILS_PAD, (w - 2) / 2);
        const valueLeft = 1 + keyColumn + DETAILS_PAD;
        const valueRoom = Math.max(0, w - 1 - DETAILS_PAD - valueLeft);

        y = 1 + DETAILS_HEAD;

        content.rows.forEach(function (row, i) {
            if (i > 0) {
                lines.push({ type: 'line', x1: 1, y1: y + 0.5, x2: w - 1, y2: y + 0.5, stroke: look.line, lineWidth: 1 });
                y += 1;
            }

            texts.push({ type: 'text', x: left, y: y + DETAILS_ROW / 2, text: fitDetailsText(row[0], fonts.key, keyColumn - DETAILS_PAD, measure), font: fonts.key, size: 9, weight: 400, fill: look.muted, alpha: 1, align: 'left' });
            texts.push({ type: 'text', x: valueLeft, y: y + DETAILS_ROW / 2, text: fitDetailsText(row[1], fonts.value, valueRoom, measure), font: fonts.value, size: 10, weight: 400, fill: look.fg, alpha: 1, align: 'left' });
            y += DETAILS_ROW;
        });

        if (moreText !== null) {
            lines.push({ type: 'line', x1: 1, y1: y + 0.5, x2: w - 1, y2: y + 0.5, stroke: look.line, lineWidth: 1 });
            y += 1;
            texts.push({ type: 'text', x: left, y: y + 1 + DETAILS_MORE_LINE / 2, text: fitDetailsText(moreText, fonts.more, room, measure), font: fonts.more, size: 9, weight: 400, fill: look.muted, alpha: 1, align: 'left' });
            y += 1 + DETAILS_MORE_LINE + 3;
        }

        const h = y + 1;
        const shapes = [
            { type: 'rect', role: 'card', x: 0, y: 0, w: w, h: h, radius: [6, 6, 6, 6], fill: look.bg, stroke: null, lineWidth: 0 },
            { type: 'rect', role: 'head', x: 0, y: 0, w: w, h: 1 + DETAILS_HEAD, radius: [6, 6, 0, 0], fill: headColor, stroke: null, lineWidth: 0 }
        ];

        let ringWidth = 1;

        if (look.selected)
            ringWidth = 2;

        return {
            width: w,
            height: h,
            shapes: shapes.concat(lines, texts, [{ type: 'rect', role: 'frame', x: 0.5, y: 0.5, w: w - 1, h: h - 1, radius: [5.5, 5.5, 5.5, 5.5], fill: null, stroke: ringColor, lineWidth: ringWidth }])
        };
    }

    //A rectangle's outline with each corner rounded by its own radius, top left first and on clockwise, as a path's
    //points: the canvas and SVG painters both walk it.
    function roundedRectPoints(s) {
        const r = s.radius;

        return [
            ['M', s.x + r[0], s.y],
            ['L', s.x + s.w - r[1], s.y],
            ['Q', s.x + s.w, s.y, s.x + s.w, s.y + r[1]],
            ['L', s.x + s.w, s.y + s.h - r[2]],
            ['Q', s.x + s.w, s.y + s.h, s.x + s.w - r[2], s.y + s.h],
            ['L', s.x + r[3], s.y + s.h],
            ['Q', s.x, s.y + s.h, s.x, s.y + s.h - r[3]],
            ['L', s.x, s.y + r[0]],
            ['Q', s.x, s.y, s.x + r[0], s.y]
        ];
    }

    //Paints a laid-out card on a canvas, centered on (x, y), scale canvas pixels to its pixel.
    function paintDetailsCard(ctx, layout, x, y, scale) {
        ctx.save();
        ctx.translate(x - layout.width * scale / 2, y - layout.height * scale / 2);
        ctx.scale(scale, scale);

        layout.shapes.forEach(function (s) {
            if (s.type === 'rect') {
                ctx.beginPath();

                roundedRectPoints(s).forEach(function (p) {
                    if (p[0] === 'M')
                        ctx.moveTo(p[1], p[2]);
                    else if (p[0] === 'L')
                        ctx.lineTo(p[1], p[2]);
                    else
                        ctx.quadraticCurveTo(p[1], p[2], p[3], p[4]);
                });

                ctx.closePath();

                if (s.fill) {
                    ctx.fillStyle = s.fill;
                    ctx.fill();
                }

                if (s.stroke) {
                    ctx.strokeStyle = s.stroke;
                    ctx.lineWidth = s.lineWidth;
                    ctx.stroke();
                }
            }
            else if (s.type === 'line') {
                ctx.beginPath();
                ctx.moveTo(s.x1, s.y1);
                ctx.lineTo(s.x2, s.y2);
                ctx.strokeStyle = s.stroke;
                ctx.lineWidth = s.lineWidth;
                ctx.stroke();
            }
            else {
                ctx.globalAlpha = s.alpha;
                ctx.font = s.font;
                ctx.fillStyle = s.fill;
                ctx.textAlign = s.align;
                ctx.textBaseline = 'middle';
                ctx.fillText(s.text, s.x, s.y);
                ctx.globalAlpha = 1;
            }
        });

        ctx.restore();
    }

    function escapeSvgText(text) {
        return String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    //A laid-out card as an SVG group, centered on (x, y), scale units to its pixel: for the 2D canvas's SVG export.
    function detailsCardSvg(layout, x, y, scale, family) {
        const parts = ['<g transform="translate(' + (x - layout.width * scale / 2) + ' ' + (y - layout.height * scale / 2) + ') scale(' + scale + ')">'];
        const fontFamily = escapeSvgText(family || 'sans-serif');

        layout.shapes.forEach(function (s) {
            if (s.type === 'rect') {
                const d = roundedRectPoints(s).map(p => p.join(' ')).join(' ') + ' Z';
                let fill = 'none';
                let stroke = '';

                if (s.fill)
                    fill = escapeSvgText(s.fill);

                if (s.stroke)
                    stroke = ' stroke="' + escapeSvgText(s.stroke) + '" stroke-width="' + s.lineWidth + '"';

                parts.push('<path d="' + d + '" fill="' + fill + '"' + stroke + '/>');
            }
            else if (s.type === 'line')
                parts.push('<line x1="' + s.x1 + '" y1="' + s.y1 + '" x2="' + s.x2 + '" y2="' + s.y2 + '" stroke="' + escapeSvgText(s.stroke) + '" stroke-width="' + s.lineWidth + '"/>');
            else {
                let anchor = 'start';

                if (s.align === 'right')
                    anchor = 'end';

                parts.push('<text x="' + s.x + '" y="' + s.y + '" font-family="' + fontFamily + '" font-size="' + s.size + '" font-weight="' + s.weight + '" fill="' + escapeSvgText(s.fill) + '" fill-opacity="' + s.alpha + '" text-anchor="' + anchor + '" dominant-baseline="central">' + escapeSvgText(s.text) + '</text>');
            }
        });

        parts.push('</g>');

        return parts.join('');
    }

    return {
        normalizeModelUrl: normalizeModelUrl,
        geoProject: geoProject,
        geoToCartesian: geoToCartesian,
        geoUnproject: geoUnproject,
        cartesianToGeo: cartesianToGeo,
        geoTileUrl: geoTileUrl,
        geoTileViewReporter: geoTileViewReporter,
        geoTileLat: geoTileLat,
        geoTileRow: geoTileRow,
        geoTileZoom: geoTileZoom,
        geoTileRange: geoTileRange,
        geoTileStrips: geoTileStrips,
        geoTileStripCount: geoTileStripCount,
        TILE_MAX_LAT: TILE_MAX_LAT,
        geoArc: geoArc,
        geoGraticuleStep: geoGraticuleStep,
        geoDegreeLabel: geoDegreeLabel,
        geoGreatCircle: geoGreatCircle,
        geoCrossesAntimeridian: geoCrossesAntimeridian,
        geoEdgeBend: geoEdgeBend,
        sphereOccludes: sphereOccludes,
        curveControlPoint: curveControlPoint,
        parseObj: parseObj,
        parseModelData: parseModelData,
        parseGltf: parseGltf,
        isZip: isZip,
        isGlb: isGlb,
        extractObjFromZip: extractObjFromZip,
        addSphere: addSphere,
        addTube: addTube,
        addModel: addModel,
        meshToObj: meshToObj,
        meshToPly: meshToPly,
        meshToStl: meshToStl,
        meshToGltf: meshToGltf,
        meshToGlb: meshToGlb,
        bytesToBase64: bytesToBase64,
        DETAILS_ROWS_MOST: DETAILS_ROWS_MOST,
        detailsCardContent: detailsCardContent,
        detailsCardLayout: detailsCardLayout,
        paintDetailsCard: paintDetailsCard,
        detailsCardSvg: detailsCardSvg
    };
});
