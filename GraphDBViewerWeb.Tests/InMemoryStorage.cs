using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///Faithful in-memory IAppStorage — typed values JSON round-tripped, exactly like localStorage — for
///component tests whose behavior depends on persistence actually persisting (e.g. the Merge/Replace
///toggle). WorkspaceStoreTests and LlmConnectionStoreTests keep their own private copies on purpose:
///those suites test storage round-trips themselves.
///</summary>
internal sealed class InMemoryStorage : IAppStorage
{
    private readonly Dictionary<string, string> _data = new();

    public event Action StorageQuotaExceeded { add { } remove { } }

    public Task<T> GetAsync<T>(string key)
    {
        if (!_data.TryGetValue(key, out var json))
            return Task.FromResult<T>(default);

        return Task.FromResult(JsonSerializer.Deserialize<T>(json));
    }

    public Task SetAsync<T>(string key, T value)
    {
        _data[key] = JsonSerializer.Serialize(value);
        return Task.CompletedTask;
    }

    public Task<string> GetStringAsync(string key)
    {
        if (!_data.TryGetValue(key, out var s))
            return Task.FromResult<string>(null);

        return Task.FromResult(s);
    }

    public Task SetStringAsync(string key, string value)
    {
        _data[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _data.Remove(key);
        return Task.CompletedTask;
    }
}
