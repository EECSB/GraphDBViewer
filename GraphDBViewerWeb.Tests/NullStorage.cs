using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

///<summary>
///IAppStorage that stores nothing, for component tests whose behavior doesn't depend on persistence.
///(WorkspaceStoreTests keeps its own faithful in-memory fake — that one tests the storage round-trip itself.)
///</summary>
internal sealed class NullStorage : IAppStorage
{
    public event Action StorageQuotaExceeded { add { } remove { } }

    public Task<T> GetAsync<T>(string key)
    {
        return Task.FromResult<T>(default);
    }

    public Task SetAsync<T>(string key, T value)
    {
        return Task.CompletedTask;
    }

    public Task<string> GetStringAsync(string key)
    {
        return Task.FromResult<string>(null);
    }

    public Task SetStringAsync(string key, string value)
    {
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        return Task.CompletedTask;
    }
}
