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

    //── Open connections (the multiple-connections spec, task 3) ──────────

    //Nothing stored as open connections reads back as nothing, though a browser from before there were several keeps
    //its one list of tabs: the page brings those back from their own key as the first connection's.
    [Fact]
    public async Task LoadConnections_ReturnsNull_WhenNoneStored_EvenWithTheOldTabs()
    {
        var store = NewStore();

        await store.SaveTabsAsync(new[] { new QueryTab { Name = "Kept" } });

        Assert.Null(await store.LoadConnectionsAsync());
        Assert.Equal("Kept", Assert.Single(await store.LoadTabsAsync()).Name);
    }

    //Each connection comes back with its form, the saved connection it was picked from, whether it was drawing offline,
    //its tabs and the tab in front, disconnected, and the connection that was in front is in front again. Its key does
    //not: a form nobody saved does not keep a password in a second place, and a saved one fills it again on the way back.
    [Fact]
    public async Task SaveConnections_LoadConnections_RoundTripsEachConnectionAndItsTabs()
    {
        var store = NewStore();
        var a = new ViewerConnection
        {
            Connection = new GremlinDB.GremlinConnection("WebSocket", 8182, false, "a.example", "secret", "", ""),
            SelectedConnectionKey = "Saved A",
            Tabs = new List<QueryTab> { new QueryTab { Name = "A1" }, new QueryTab { Name = "A2" } },
            ActiveTabIndex = 1,
            IsConnected = true
        };
        var b = new ViewerConnection
        {
            Connection = new GremlinDB.GremlinConnection("HTTP", 8529, true, "b.example", "other", "", ""),
            OfflineMode = true,
            Tabs = new List<QueryTab> { new QueryTab { Name = "B1" } }
        };

        await store.SaveConnectionsAsync(new[] { a, b }, 1);

        var open = await store.LoadConnectionsAsync();

        Assert.Equal(1, open.Active);
        Assert.Equal(new[] { a.Id, b.Id }, open.Connections.Select(c => c.Id));

        var ra = open.Connections[0];
        var rb = open.Connections[1];

        Assert.Equal("a.example", ra.Connection.Hostname);
        Assert.Equal(8182, ra.Connection.Port);
        Assert.Equal("Saved A", ra.SelectedConnectionKey);
        Assert.Equal(new[] { "A1", "A2" }, ra.Tabs.Select(t => t.Name));
        Assert.Equal(1, ra.ActiveTabIndex);
        Assert.False(ra.IsConnected);
        Assert.Null(ra.Db);
        Assert.Equal("b.example", rb.Connection.Hostname);
        Assert.Null(rb.SelectedConnectionKey);
        Assert.True(rb.OfflineMode);
        Assert.Equal("B1", Assert.Single(rb.Tabs).Name);
        Assert.Empty(ra.Connection.AuthKey ?? "");
        Assert.Empty(rb.Connection.AuthKey ?? "");
        Assert.Equal("secret", a.Connection.AuthKey);
    }

    //The editor text typed since the last heavy save comes back on the tab it was typed in, whichever connection's.
    [Fact]
    public async Task LoadConnections_OverlaysTheEditorTextOnEveryConnectionsTabs()
    {
        var store = NewStore();
        var a = new ViewerConnection { Tabs = new List<QueryTab> { new QueryTab { Name = "A1", QueryText = "old a" } } };
        var b = new ViewerConnection { Tabs = new List<QueryTab> { new QueryTab { Name = "B1", QueryText = "old b" } } };

        await store.SaveConnectionsAsync(new[] { a, b }, 0);

        b.Tabs[0].QueryText = "typed in b";
        await store.SaveTabTextAsync(new[] { a, b }.SelectMany(c => c.Tabs));

        var open = await store.LoadConnectionsAsync();

        Assert.Equal("old a", open.Connections[0].Tabs[0].QueryText);
        Assert.Equal("typed in b", open.Connections[1].Tabs[0].QueryText);
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
            MapStyle = "outline",
            MapDetail = "high",
            Saved2dPositionsJson = @"[{""id"":""v1"",""x"":1,""y"":2}]",
            Saved3dPositionsJson = @"[{""id"":""v1"",""x"":3,""y"":4,""z"":5}]",
            Show3dVertexLabels = false,
            Show3dEdgeLabels = true,
            Show3dProps = false,
            G3dExportFormat = "ply",
            TableExportFormat = "xlsx",
            LabelStyles = new Dictionary<string, LabelStyle> { ["person"] = new LabelStyle { Color = "#ff0000", Size = 40, Shape = "circle", Shape3d = "cube" } },
            EdgeColorMode = 2,
            EdgeColors = new Dictionary<string, string> { ["knows"] = "#00ff00" },
            EdgeDetails = ["knows"]
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
        Assert.Equal("outline", r.MapStyle);
        Assert.Equal("high", r.MapDetail);
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
        Assert.Equal(["knows"], r.EdgeDetails);
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

    //The 3D draw-distance sliders and Node size on map are view preferences of the tab like its layout and its map
    //style, and come back as they were set, a slider's end, 0, among them (the audit of 2026-11-07: ToSnapshot never
    //wrote them, so every reload put all three back at 100).
    [Fact]
    public async Task TheDrawDistances_AndTheMapNodeSize_SurviveAReload()
    {
        var store = NewStore();
        var tab = new QueryTab { Name = "Q", Draw3dLabelDistance = 40, Draw3dNodeDistance = 0, MapNodeSize = 150 };

        await store.SaveTabsAsync(new[] { tab });
        var restored = await store.LoadTabsAsync();

        var r = Assert.Single(restored);
        Assert.Equal(40, r.Draw3dLabelDistance);
        Assert.Equal(0, r.Draw3dNodeDistance);
        Assert.Equal(150, r.MapNodeSize);
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
        Assert.NotNull(tab.EdgeDetails);
        //A tab saved before the 3D "Properties" toggle existed restores with it ON (the default).
        Assert.True(tab.Show3dProps);
        //And one saved before the earth had two sizes restores on the standard one: the detailed image is
        //3.7 MB, and only a choice made since fetches it.
        Assert.Equal("satellite", tab.MapStyle);
        Assert.Equal("standard", tab.MapDetail);
        //A tab saved before the draw-distance sliders and Node size on map were kept restores with all three at 100,
        //the sliders' "Full".
        Assert.Equal(100, tab.Draw3dLabelDistance);
        Assert.Equal(100, tab.Draw3dNodeDistance);
        Assert.Equal(100, tab.MapNodeSize);
    }

    [Fact]
    public void FromSnapshot_AlwaysResetsImageFormatToPng()
    {
        var tab = WorkspaceStore.FromSnapshot(new TabSnapshot { ImageExportFormat = "svg" });

        Assert.Equal("png", tab.ImageExportFormat);
    }

    //Each language an engine with AI tools runs is kept by GraphDbProvidersTests.EveryEngineWithAiTools_NamesTheLanguageItRuns;
    //SPARQL, which none of them runs, is kept here, and nothing, or a word no editor offers, is Gremlin.
    [Theory]
    [InlineData("sparql", "sparql")]
    [InlineData(null, "gremlin")]
    [InlineData("cobol", "gremlin")]
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
