//Availability probe for a pasted image / 3D-model URL. Returns { reachable, error } — error carries the
//exact reason (an HTTP status like "HTTP 404 Not Found", or a network / CORS message) so the UI can show
//it.
//
//WHY THIS IS JAVASCRIPT, AND NOT C#
//
//The question this answers is not "does that URL exist?" — it is "will *this app* be able to load it?".
//The only way to answer that honestly is to ask over the very same network path the thing that displays
//the file will use. There are two such paths, and they do not behave alike:
//
//  - An image is drawn by Cytoscape through 'background-image' (CytoscapeInterop.js) and by three.js as
//    a texture — the <img> path, which loads cross-origin *without* CORS. So the probe is an <img> load.
//    The price is the HTTP status: an <img> reports that it failed, never why. That is the right way
//    round — a wrong red on a working image is worse than a vague message on a broken one.
//  - A model is fetched (3dForceGraphInterop.loadObjModel), which *does* need the host to send CORS
//    headers. So the probe is a fetch, and a "not reachable" answer also means the loader would fail.
//
//The <img> half is the part that cannot move to C#. In WebAssembly a .NET HttpClient is implemented on
//top of the browser's fetch, so it inherits fetch's CORS rules exactly — there is no .NET API that
//reproduces the no-CORS <img> load. Probing an image with HttpClient would turn the box red for every
//cross-origin image that displays perfectly well.
//
//The fetch half *could* be C#, since HttpClient and fetch are the same request there. It is deliberately
//not: splitting one small job across two languages buys nothing and costs a reader the ability to see
//both answers side by side, which is the only place the difference between them is visible. Both probes
//stay here, together, for the same reason they differ at all.
window.urlCheck = {
    isReachable: function (url, kind) {
        if (!url || !url.trim())
            return Promise.resolve({ reachable: false, error: "No URL entered." });

        url = url.trim();

        if (kind === 'image') {
            return new Promise(function (resolve) {
                var done = false;
                var finish = function (result) {
                    if (!done) {
                        done = true;
                        resolve(result);
                    }
                };

                var img = new Image();
                img.onload = function () { finish({ reachable: true, error: "" }); };
                img.onerror = function () { finish({ reachable: false, error: "Image could not be loaded — the URL may be wrong, blocked, or not an image. (An <img> probe can't read the HTTP status.)" }); };
                setTimeout(function () { finish({ reachable: false, error: "Timed out loading the image (12s)." }); }, 12000);
                img.src = url;
            });
        }

        return (async function () {
            var controller = new AbortController();
            var timer = setTimeout(function () { controller.abort(); }, 12000);

            //A share link is not the file. The loader rewrites OneDrive and Dropbox URLs to their
            //direct-download endpoints before fetching, so the probe has to ask about the same address —
            //otherwise a link pasted straight out of Dropbox checks the share page, fails CORS and shows
            //red for a model that then loads perfectly well.
            var fetchUrl = graphGeometry.normalizeModelUrl(url);

            try {
                var resp = await fetch(fetchUrl, { method: 'GET', signal: controller.signal });
                clearTimeout(timer);

                //Got the headers — we don't need the body, so stop the download.
                try { if (resp.body) resp.body.cancel(); } catch (e) { }

                if (resp.ok)
                    return { reachable: true, error: "" };

                return { reachable: false, error: "HTTP " + resp.status + (resp.statusText ? " " + resp.statusText : "") };
            } catch (e) {
                clearTimeout(timer);

                var msg;
                if (e && e.name === 'AbortError')
                    msg = "Timed out (12s).";
                else if (e && e.message)
                    msg = e.message;
                else
                    msg = "Request failed.";

                return { reachable: false, error: msg + " — the host may not send CORS headers, so the app can't load it either." };
            }
        })();
    }
};
