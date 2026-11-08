using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Storage held in memory for as long as the page is open (2026-10-21): the embedded viewer's (<see cref="EmbedSession"/>).
///A viewer embedded in another site reads with the token that site passed, so it has no account's workspace to restore
///or to save into, and no business in this browser's own storage for this host: what it keeps between one click and
///the next, it keeps here, and it is gone with the page. Values are held as their JSON, as the other stores hold
///them, so what is read back is a copy and never the object that was written.
///</summary>
public sealed class MemoryAppStorage : IAppStorage
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    //Memory has no quota to run into, so this is never raised; the interface asks for it.
    public event Action StorageQuotaExceeded
    {
        add { }
        remove { }
    }

    public Task<T> GetAsync<T>(string key)
    {
        if (key == null || !_values.TryGetValue(key, out var json))
            return Task.FromResult(default(T));

        return Task.FromResult(JsonSerializer.Deserialize<T>(json));
    }

    public Task SetAsync<T>(string key, T value)
    {
        _values[key] = JsonSerializer.Serialize(value);

        return Task.CompletedTask;
    }

    public Task<string> GetStringAsync(string key)
    {
        if (key == null || !_values.TryGetValue(key, out var value))
            return Task.FromResult<string>(null);

        return Task.FromResult(value);
    }

    public Task SetStringAsync(string key, string value)
    {
        _values[key] = value;

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        if (key != null)
            _values.Remove(key);

        return Task.CompletedTask;
    }
}
