//A minimal string key-value store over IndexedDB, used by IndexedDbAppStorage as the app's persistence
//backend. IndexedDB's per-origin quota is far larger than localStorage's ~5 MB (typically a large
//fraction of free disk), so heavy workspaces (tab results, saved positions) no longer hit the wall.
//Values are opaque strings — the C# layer handles JSON serialization and compression. Writes never
//throw: set() resolves false on a full quota or when storage is unavailable, so a failed save can be
//surfaced as a friendly warning instead of crashing Blazor's render.
window.gdbvIdb = {
    _dbName: 'graphdbviewer',
    _store: 'kv',
    _dbPromise: null,

    _open: function () {
        if (this._dbPromise)
            return this._dbPromise;

        const self = this;
        this._dbPromise = new Promise(function (resolve, reject) {
            let req;

            try {
                req = indexedDB.open(self._dbName, 1);
            }
            catch (e) {
                reject(e);
                return;
            }

            req.onupgradeneeded = function () {
                req.result.createObjectStore(self._store);
            };
            req.onsuccess = function () {
                resolve(req.result);
            };
            req.onerror = function () {
                reject(req.error);
            };
        });

        return this._dbPromise;
    },

    //Reads the string stored under key, or null when absent / on any failure.
    get: async function (key) {
        try {
            const db = await this._open();
            const store = this._store;

            return await new Promise(function (resolve, reject) {
                const tx = db.transaction(store, 'readonly');
                const req = tx.objectStore(store).get(key);
                req.onsuccess = function () {
                    resolve(req.result === undefined ? null : req.result);
                };
                req.onerror = function () {
                    reject(req.error);
                };
            });
        }
        catch (e) {
            return null;
        }
    },

    //Writes value under key. Resolves true on success, false on a full quota / disabled storage — never throws.
    set: async function (key, value) {
        try {
            const db = await this._open();
            const store = this._store;

            return await new Promise(function (resolve) {
                let tx;

                try {
                    tx = db.transaction(store, 'readwrite');
                }
                catch (e) {
                    resolve(false);
                    return;
                }

                tx.objectStore(store).put(value, key);
                tx.oncomplete = function () {
                    resolve(true);
                };
                tx.onerror = function () {
                    resolve(false);
                };
                tx.onabort = function () {
                    resolve(false);
                };
            });
        }
        catch (e) {
            return false;
        }
    },

    //Removes a key. A no-op when absent or on failure.
    remove: async function (key) {
        try {
            const db = await this._open();
            const store = this._store;

            await new Promise(function (resolve) {
                const tx = db.transaction(store, 'readwrite');
                tx.objectStore(store).delete(key);
                tx.oncomplete = function () {
                    resolve();
                };
                tx.onerror = function () {
                    resolve();
                };
            });
        }
        catch (e) { }
    },

    //Whether IndexedDB is usable at all (it's disabled in some private-browsing modes).
    available: function () {
        try {
            return typeof indexedDB !== 'undefined' && indexedDB !== null;
        }
        catch (e) {
            return false;
        }
    },

    //Best-effort {usage, quota} in bytes for the origin (0/0 when the API is unavailable), for a usage meter.
    estimate: async function () {
        try {
            if (navigator.storage && navigator.storage.estimate) {
                const e = await navigator.storage.estimate();
                return { usage: e.usage || 0, quota: e.quota || 0 };
            }
        }
        catch (e) { }

        return { usage: 0, quota: 0 };
    }
};
