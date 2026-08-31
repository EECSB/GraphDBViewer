using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class LlmConnectionStoreTests
{
    //Faithful in-memory IAppStorage (typed values JSON round-tripped, like localStorage) — this suite
    //tests the storage round-trip itself, so the shared NullStorage fake won't do.
    private sealed class InMemoryStorage : IAppStorage
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

    [Fact]
    public async Task LoadAsync_NothingStored_ReturnsEmptyDictionaryNotNull()
    {
        var store = new LlmConnectionStore(new InMemoryStorage());

        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheConnections()
    {
        var store = new LlmConnectionStore(new InMemoryStorage());
        var connections = new Dictionary<string, LlmConnection>
        {
            ["My Claude"] = new LlmConnection { Name = "My Claude", ProviderType = "Anthropic", ApiKey = "sk-test", MaxTokens = 4096 }
        };

        await store.SaveAsync(connections);
        var loaded = await store.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("Anthropic", loaded["My Claude"].ProviderType);
        Assert.Equal(4096, loaded["My Claude"].MaxTokens);
    }

    //The key and shape NlQueryModal used to own: models a user saved before the store existed. Written
    //through raw IAppStorage exactly as the modal's PersistAsync did — if this fails, existing saved
    //models vanish on upgrade, which the spec's Phase 0 explicitly forbids (no migration needed or run).
    [Fact]
    public async Task LoadAsync_SeesConnectionsSavedByTheOldModalCode()
    {
        var storage = new InMemoryStorage();
        await storage.SetAsync("graphdbviewer:llmConnections", new Dictionary<string, LlmConnection>
        {
            ["Legacy"] = new LlmConnection { Name = "Legacy", ProviderType = "Gemini", BaseUrl = null, Model = "gemini-2.5-flash" }
        });

        var loaded = await new LlmConnectionStore(storage).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("Gemini", loaded["Legacy"].ProviderType);
        Assert.Equal("gemini-2.5-flash", loaded["Legacy"].Model);
    }
}
