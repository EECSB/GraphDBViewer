using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///<see cref="IAppStorage"/> backed by the browser's localStorage (via Blazored.LocalStorage).
///The store the viewer had before IndexedDB (<see cref="IndexedDbAppStorage"/>, which migrates out of it once);
///no build registers it today, and it stays should localStorage ever need swapping back in.
///</summary>
public class LocalAppStorage : IMirrorStorage
{
    private readonly ILocalStorageService _localStorage;
    private readonly IJSRuntime _js;

    public LocalAppStorage(ILocalStorageService localStorage, IJSRuntime js)
    {
        _localStorage = localStorage;
        _js = js;
    }

    public event Action StorageQuotaExceeded;

    public async Task<T> GetAsync<T>(string key)
    {
        return await _localStorage.GetItemAsync<T>(key);
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try
        {
            await _localStorage.SetItemAsync(key, value);
        }
        catch (JSException ex)
        {
            OnWriteThrew(ex);
            return;
        }

        await RaiseIfWriteFailedAsync();
    }

    public async Task<string> GetStringAsync(string key)
    {
        return await _localStorage.GetItemAsStringAsync(key);
    }

    public async Task SetStringAsync(string key, string value)
    {
        try
        {
            await _localStorage.SetItemAsStringAsync(key, value);
        }
        catch (JSException ex)
        {
            OnWriteThrew(ex);
            return;
        }

        await RaiseIfWriteFailedAsync();
    }

    public async Task RemoveAsync(string key)
    {
        await _localStorage.RemoveItemAsync(key);
    }

    ///<summary>Every key the app wrote — the <c>graphdbviewer:</c> ones. Anything else in localStorage is not its.</summary>
    public async Task ClearAsync()
    {
        var keys = new List<string>(await _localStorage.KeysAsync());

        foreach (var key in keys)
            if (key.StartsWith("graphdbviewer:", StringComparison.Ordinal))
                await _localStorage.RemoveItemAsync(key);
    }

    //The setItem shim in index.html swallows a failed localStorage write — a full per-origin quota (a few
    //MB), or storage disabled in private browsing — so it never throws into Blazor's interop layer and pops
    //the framework's fatal-error bar. It flags the failure instead; surface that here as the quota event so
    //the UI can tell the user their work is no longer being saved. Best-effort: if the shim isn't present
    //(a stale cached index.html), the write throws and OnWriteThrew is the fallback.
    private async Task RaiseIfWriteFailedAsync()
    {
        bool failed;

        try
        {
            failed = await _js.InvokeAsync<bool>("gdbvStorage.takeWriteFailed");
        }
        catch
        {
            return;
        }

        if (failed)
            StorageQuotaExceeded?.Invoke();
    }

    //Fallback for when the setItem shim isn't in place: the write threw instead of being swallowed. Keep the
    //app alive (persistence is best-effort) and raise the quota event on a quota error just like the shim path.
    private void OnWriteThrew(JSException ex)
    {
        Console.Error.WriteLine($"localStorage write failed: {ex.Message}");

        if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
            StorageQuotaExceeded?.Invoke();
    }
}
