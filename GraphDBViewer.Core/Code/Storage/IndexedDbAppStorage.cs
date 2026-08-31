using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///<see cref="IAppStorage"/> backed by the browser's IndexedDB (via the <c>gdbvIdb</c> JS interop) instead
///of localStorage. IndexedDB's per-origin quota is far larger than localStorage's ~5 MB, so heavy
///workspaces (tab results, saved positions) no longer hit the wall. Two extra behaviors on top of a plain
///key-value store:
///<list type="bullet">
///<item>Large values are transparently <b>compressed</b> (deflate + base64) so storage goes even further —
///a JSON graph packs to a fraction of its size, with zero data loss.</item>
///<item>On first use it <b>migrates</b> any existing <c>graphdbviewer:*</c> data out of localStorage, so
///upgrading users keep their saved connections, tabs and queries.</item>
///</list>
///Writes are best-effort: a failed write (a full quota, or IndexedDB disabled in private browsing) raises
///<see cref="StorageQuotaExceeded"/> rather than throwing, so the UI can warn instead of crashing.
///</summary>
public class IndexedDbAppStorage : IAppStorage
{
    private readonly IJSRuntime _js;
    private readonly ILocalStorageService _localStorage;

    //Keys that live in IndexedDB but aren't app data (skipped by any prefix logic).
    private const string MigratedFlagKey = "__gdbv_migrated";
    private const string LocalStoragePrefix = "graphdbviewer:";

    private Task _ready;

    public IndexedDbAppStorage(IJSRuntime js, ILocalStorageService localStorage)
    {
        _js = js;
        _localStorage = localStorage;
    }

    public event Action StorageQuotaExceeded;

    public async Task<T> GetAsync<T>(string key)
    {
        var json = await GetStringAsync(key);

        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            //A value written by an older/other serializer that no longer deserializes — treat as absent
            //rather than crash. The app defaults anything missing.
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        await SetStringAsync(key, JsonSerializer.Serialize(value));
    }

    public async Task<string> GetStringAsync(string key)
    {
        await EnsureReadyAsync();

        return await GetStringCoreAsync(key);
    }

    public async Task SetStringAsync(string key, string value)
    {
        await EnsureReadyAsync();

        await SetStringCoreAsync(key, value);
    }

    public async Task RemoveAsync(string key)
    {
        await EnsureReadyAsync();

        try
        {
            await _js.InvokeVoidAsync("gdbvIdb.remove", key);
        }
        catch { }
    }

    //Reads + decodes a value straight from IndexedDB (no migration gate — used both by the public API and,
    //during migration, to avoid re-entering EnsureReadyAsync).
    private async Task<string> GetStringCoreAsync(string key)
    {
        string stored;

        try
        {
            stored = await _js.InvokeAsync<string>("gdbvIdb.get", key);
        }
        catch
        {
            return null;
        }

        return StorageCompression.Decode(stored);
    }

    //Encodes + writes a value straight to IndexedDB. A failed write raises the quota event instead of throwing.
    private async Task SetStringCoreAsync(string key, string value)
    {
        bool ok;

        try
        {
            ok = await _js.InvokeAsync<bool>("gdbvIdb.set", key, StorageCompression.Encode(value));
        }
        catch
        {
            //The interop itself failed (IndexedDB unavailable) — treat as a failed, non-fatal write.
            StorageQuotaExceeded?.Invoke();
            return;
        }

        if (!ok)
            StorageQuotaExceeded?.Invoke();
    }

    #region Migration

    private Task EnsureReadyAsync()
    {
        _ready ??= MigrateAsync();

        return _ready;
    }

    //Copies any existing graphdbviewer:* keys out of localStorage into IndexedDB, once. Guarded by a marker
    //in IndexedDB so it runs at most once per browser. Best-effort throughout: if IndexedDB is unavailable or
    //a step fails, the app simply starts empty rather than crashing.
    private async Task MigrateAsync()
    {
        try
        {
            if (!await _js.InvokeAsync<bool>("gdbvIdb.available"))
                return;

            var alreadyMigrated = await _js.InvokeAsync<string>("gdbvIdb.get", MigratedFlagKey);
            if (alreadyMigrated == "1")
                return;

            IEnumerable<string> keys;

            try
            {
                keys = await _localStorage.KeysAsync();
            }
            catch
            {
                keys = Array.Empty<string>();
            }

            foreach (var key in keys)
            {
                if (!key.StartsWith(LocalStoragePrefix, StringComparison.Ordinal))
                    continue;

                string value;

                try
                {
                    value = await _localStorage.GetItemAsStringAsync(key);
                }
                catch
                {
                    continue;
                }

                if (value != null)
                    await SetStringCoreAsync(key, value);
            }

            await _js.InvokeAsync<bool>("gdbvIdb.set", MigratedFlagKey, "1");
        }
        catch
        {
            //Never let migration break startup — a failure just means nothing was carried over.
        }
    }

    #endregion
}

///<summary>Bytes used / available for the origin, from <c>navigator.storage.estimate()</c> (0 when unknown).</summary>
public record StorageEstimate(long Usage, long Quota);
