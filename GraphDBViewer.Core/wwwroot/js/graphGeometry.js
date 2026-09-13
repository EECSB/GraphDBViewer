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

        for (let i = 0; i < ladder.length; i++) {
            if (ladder[i] * pixelsPerDegree >= minSpacingPx)
                chosen = ladder[i];
            else
                break;
        }

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

        if (axis === 'lat')
            hemisphere = degrees > 0 ? 'N' : 'S';
        else
            hemisphere = degrees > 0 ? 'E' : 'W';

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

    return {
        normalizeModelUrl: normalizeModelUrl,
        geoProject: geoProject,
        geoToCartesian: geoToCartesian,
        geoArc: geoArc,
        geoGraticuleStep: geoGraticuleStep,
        geoDegreeLabel: geoDegreeLabel,
        geoGreatCircle: geoGreatCircle,
        geoCrossesAntimeridian: geoCrossesAntimeridian,
        geoEdgeBend: geoEdgeBend,
        sphereOccludes: sphereOccludes,
        curveControlPoint: curveControlPoint,
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
