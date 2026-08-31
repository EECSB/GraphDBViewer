using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

public class WorkspaceStoreTests
{
    //Faithful in-memory IAppStorage: typed values are JSON round-tripped, exactly like localStorage.
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

    private static WorkspaceStore NewStore()
    {
        return new WorkspaceStore(new InMemoryStorage());
    }

    //── Tabs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadTabs_ReturnsNull_WhenNothingStored()
    {
        var store = NewStore();

        Assert.Null(await store.LoadTabsAsync());
    }

    [Fact]
    public async Task SaveTabs_LoadTabs_RoundTripsTabState()
    {
        var store = NewStore();
        var resultJson = @"[{""id"":""v1"",""label"":""person""}]";

        var tab = new QueryTab
        {
            Name = "My tab",
            QueryText = "g.V()",
            EditorLanguage = "sparql",
            GeneratedQueryText = "g.addV('x')",
            QueryEditorTab = 2,
            QueryResults = "[...]",
            VisualizationMode = 3,
            SearchTerm = "alice",
            HiddenLabels = new HashSet<string> { "material" },
            LastResultData = JsonDocument.Parse(resultJson).RootElement,
            ResultsCleared = true,
            LoadDbLimit = 250,
            AutoReloadAfterCommit = true,
            ReflectDbState = false,
            Cy2dLayout = "grid",
            G3dLayout = "td",
            Saved2dPositionsJson = @"[{""id"":""v1"",""x"":1,""y"":2}]",
            Saved3dPositionsJson = @"[{""id"":""v1"",""x"":3,""y"":4,""z"":5}]",
            Show3dVertexLabels = false,
            Show3dEdgeLabels = true,
            Show3dProps = false,
            G3dExportFormat = "ply",
            TableExportFormat = "xlsx",
            LabelStyles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Color = "#ff0000", Size = 40, Shape = "circle", Shape3d = "cube" } },
            EdgeColorMode = 2,
            EdgeColors = new Dictionary<string, string> { ["knows"] = "#00ff00" }
        };

        await store.SaveTabsAsync(new[] { tab });
        var restored = await store.LoadTabsAsync();

        var r = Assert.Single(restored);
        Assert.Equal(tab.Id, r.Id);
        Assert.Equal("My tab", r.Name);
        Assert.Equal("g.V()", r.QueryText);
        Assert.Equal("sparql", r.EditorLanguage);
        Assert.Equal("g.addV('x')", r.GeneratedQueryText);
        Assert.Equal(2, r.QueryEditorTab);
        Assert.Equal("[...]", r.QueryResults);
        Assert.Equal(3, r.VisualizationMode);
        Assert.Equal("alice", r.SearchTerm);
        Assert.Contains("material", r.HiddenLabels);
        Assert.Equal(resultJson, r.LastResultData.GetRawText());
        Assert.Equal(JsonValueKind.Undefined, r.GraphResultData.ValueKind);
        Assert.True(r.ResultsCleared);
        Assert.Equal(250, r.LoadDbLimit);
        Assert.True(r.AutoReloadAfterCommit);
        Assert.False(r.ReflectDbState);
        Assert.Equal("grid", r.Cy2dLayout);
        Assert.Equal("td", r.G3dLayout);
        Assert.Equal(tab.Saved2dPositionsJson, r.Saved2dPositionsJson);
        Assert.Equal(tab.Saved3dPositionsJson, r.Saved3dPositionsJson);
        Assert.False(r.Show3dVertexLabels);
        Assert.True(r.Show3dEdgeLabels);
        Assert.False(r.Show3dProps);
        Assert.Equal("ply", r.G3dExportFormat);
        Assert.Equal("xlsx", r.TableExportFormat);
        Assert.Equal("#ff0000", r.LabelStyles["person"].Color);
        Assert.Equal(40, r.LabelStyles["person"].Size);
        Assert.Equal("circle", r.LabelStyles["person"].Shape);
        Assert.Equal("cube", r.LabelStyles["person"].Shape3d);
        Assert.Equal(2, r.EdgeColorMode);
        Assert.Equal("#00ff00", r.EdgeColors["knows"]);
    }

    [Fact]
    public async Task LoadTabs_OverlaysFresherEditorText()
    {
        var store = NewStore();
        var tab = new QueryTab { Name = "Query 1", QueryText = "old query", GeneratedQueryText = "old generated" };

        //The heavy checkpoint carries the old text; the per-keystroke overlay is fresher.
        await store.SaveTabsAsync(new[] { tab });
        tab.QueryText = "new query";
        tab.GeneratedQueryText = "new generated";
        await store.SaveTabTextAsync(new[] { tab });

        var restored = await store.LoadTabsAsync();

        var r = Assert.Single(restored);
        Assert.Equal("new query", r.QueryText);
        Assert.Equal("new generated", r.GeneratedQueryText);
    }

    [Fact]
    public void FromSnapshot_DefaultsLegacyFields()
    {
        var tab = WorkspaceStore.FromSnapshot(new TabSnapshot());

        Assert.False(string.IsNullOrEmpty(tab.Id));
        Assert.Equal("Query", tab.Name);
        Assert.Equal("gremlin", tab.EditorLanguage);
        Assert.Equal(1, tab.QueryEditorTab);
        Assert.Equal(2, tab.VisualizationMode);
        Assert.Empty(tab.HiddenLabels);
        Assert.Equal(JsonValueKind.Undefined, tab.LastResultData.ValueKind);
        Assert.Equal(100, tab.LoadDbLimit);
        Assert.True(tab.ReflectDbState);
        Assert.Equal("cose", tab.Cy2dLayout);
        Assert.Equal("force", tab.G3dLayout);
        Assert.Equal("obj", tab.G3dExportFormat);
        Assert.Equal("png", tab.ImageExportFormat);
        Assert.Equal("csv", tab.TableExportFormat);
        Assert.NotNull(tab.LabelStyles);
        //A tab saved before edge coloring existed restores in the default Auto mode with an empty color map.
        Assert.Equal(0, tab.EdgeColorMode);
        Assert.NotNull(tab.EdgeColors);
        //A tab saved before the 3D "Properties" toggle existed restores with it ON (the default).
        Assert.True(tab.Show3dProps);
    }

    [Fact]
    public void FromSnapshot_AlwaysResetsImageFormatToPng()
    {
        var tab = WorkspaceStore.FromSnapshot(new TabSnapshot { ImageExportFormat = "svg" });

        Assert.Equal("png", tab.ImageExportFormat);
    }

    [Theory]
    [InlineData("gremlin", "gremlin")]
    [InlineData("sparql", "sparql")]
    [InlineData("cypher", "cypher")]
    [InlineData("", "gremlin")]
    [InlineData(null, "gremlin")]
    public void NormalizeEditorLanguage_KeepsKnownLanguagesElseFallsBackToGremlin(string input, string expected)
    {
        Assert.Equal(expected, WorkspaceStore.NormalizeEditorLanguage(input));
    }

    //── History ───────────────────────────────────────────────────────

    [Fact]
    public void PushHistory_InsertsAtTop_AndDedupes()
    {
        var history = new List<string> { "g.V()", "g.E()" };

        Assert.True(WorkspaceStore.PushHistory(history, "  g.E()  "));

        Assert.Equal(new[] { "g.E()", "g.V()" }, history);
    }

    [Fact]
    public void PushHistory_CapsAtMaxHistory()
    {
        var history = new List<string>();

        for (int i = 0; i < WorkspaceStore.MaxHistory + 5; i++)
            WorkspaceStore.PushHistory(history, $"g.V({i})");

        Assert.Equal(WorkspaceStore.MaxHistory, history.Count);
        Assert.Equal($"g.V({WorkspaceStore.MaxHistory + 4})", history[0]);
    }

    [Fact]
    public void PushHistory_RejectsBlankQueries()
    {
        var history = new List<string> { "g.V()" };

        Assert.False(WorkspaceStore.PushHistory(history, "   "));
        Assert.False(WorkspaceStore.PushHistory(history, null));

        Assert.Equal(new[] { "g.V()" }, history);
    }

    //── Last query & saved queries ────────────────────────────────────

    [Fact]
    public async Task LastQuery_RoundTrips()
    {
        var store = NewStore();

        Assert.Null(await store.LoadLastQueryAsync());

        await store.SaveLastQueryAsync("g.V().limit(5)");

        Assert.Equal("g.V().limit(5)", await store.LoadLastQueryAsync());
    }

    [Fact]
    public async Task SavedQueries_RoundTrip()
    {
        var store = NewStore();

        await store.SaveSavedQueriesAsync(new Dictionary<string, string> { ["all"] = "g.V()" });
        var restored = await store.LoadSavedQueriesAsync();

        Assert.Equal("g.V()", restored["all"]);
    }
}
