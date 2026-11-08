using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;

namespace GraphDBViewerWeb.Tests;

///<summary>
///What the published viewer, v1.2.0 (`f7c7504`, 2026-09-13), left in a browser is read by this build. Each value is the
///JSON v1.2.0 wrote under its key, by hand from its types (`System.Text.Json`'s defaults, as both builds use), read back
///through the reader this build has for it. A value that no longer fits its type is not an error to the app: the storage
///hands back nothing and the person's data is quietly gone, so this is where it would show.
///
///What `e2e/released-viewer-storage.spec.js` adds: the same values in IndexedDB as v1.2.0 encoded them, raw and compressed,
///read by the app in a browser and seen on screen. TODO.md, *Before the next viewer release*, has the list of keys.
///</summary>
public class ReleasedViewerStorageTests
{
    private static async Task<InMemoryStorage> StorageWith(params (string Key, string Json)[] values)
    {
        var storage = new InMemoryStorage();

        foreach (var (key, json) in values)
            await storage.SetStringAsync(key, json);

        return storage;
    }

    [Fact]
    public async Task ItsQueryTabs_ComeBack_WithTheTextTypedSince_AsTheFirstConnectionsTabs()
    {
        var storage = await StorageWith(
            ("graphdbviewer:tabs", """
                [{"Id":"t1","Name":"People","QueryText":"g.V().hasLabel('person')","EditorLanguage":"gremlin",
                  "GeneratedQueryText":"","QueryEditorTab":0,"QueryResults":"","VisualizationMode":5,"SearchTerm":"ali",
                  "HiddenLabels":["software"],"LastResultJson":"[]","GraphResultJson":null,"ResultsCleared":false,
                  "LoadDbLimit":250,"AutoReloadAfterCommit":true,"ReflectDbState":false,"Cy2dLayout":"concentric",
                  "G3dLayout":"radialout","MapStyle":"outline","MapDetail":"high","Saved2dPositionsJson":"{\"1\":{\"x\":4,\"y\":5}}",
                  "Saved3dPositionsJson":null,"Show3dVertexLabels":true,"Show3dEdgeLabels":false,"Show3dProps":null,
                  "G3dExportFormat":"glb","ImageExportFormat":"svg","TableExportFormat":"xlsx",
                  "LabelStyles":{"person":{"Color":"#ff0000","Size":40,"Shape":"diamond","Shape3d":"cube",
                    "DisplayProperty":"name","Icon":null,"Model":null}},
                  "EdgeColorMode":1,"EdgeColors":{"knows":"#00ff00"}},
                 {"Id":"t2","Name":"Cypher","QueryText":"MATCH (n) RETURN n","EditorLanguage":"cypher","VisualizationMode":4}]
                """),
            ("graphdbviewer:tabText", """[{"Id":"t1","Query":"g.V().hasLabel('person').limit(5)","Generated":"g.addV('x')"}]"""));
        var workspace = new WorkspaceStore(storage);

        //No key of the several connections yet: the page takes the old list of tabs as its first connection's.
        Assert.Null(await workspace.LoadConnectionsAsync());

        var tabs = await workspace.LoadTabsAsync();

        Assert.Equal(["People", "Cypher"], tabs.Select(t => t.Name));

        var people = tabs[0];

        Assert.Equal("g.V().hasLabel('person').limit(5)", people.QueryText);
        Assert.Equal("g.addV('x')", people.GeneratedQueryText);
        Assert.Equal(5, people.VisualizationMode);
        Assert.Equal("ali", people.SearchTerm);
        Assert.Contains("software", people.HiddenLabels);
        Assert.Equal(250, people.LoadDbLimit);
        Assert.True(people.AutoReloadAfterCommit);
        Assert.False(people.ReflectDbState);
        Assert.Equal("concentric", people.Cy2dLayout);
        Assert.Equal("radialout", people.G3dLayout);
        Assert.Equal("outline", people.MapStyle);
        Assert.Equal("high", people.MapDetail);
        Assert.Equal("""{"1":{"x":4,"y":5}}""", people.Saved2dPositionsJson);
        Assert.Equal("glb", people.G3dExportFormat);
        Assert.Equal("#ff0000", people.LabelStyles["person"].Color);
        Assert.Equal(40, people.LabelStyles["person"].Size);
        Assert.Equal("name", people.LabelStyles["person"].DisplayProperty);
        Assert.Equal(1, people.EdgeColorMode);
        Assert.Equal("#00ff00", people.EdgeColors["knows"]);
        Assert.Equal("cypher", tabs[1].EditorLanguage);
        Assert.Equal("MATCH (n) RETURN n", tabs[1].QueryText);
    }

    [Fact]
    public async Task ItsSavedQueries_History_AndLastQuery_ComeBack()
    {
        var storage = await StorageWith(
            ("graphdbviewer:savedQueries", """{"Everyone":"g.V()","Friends":"g.V().out('knows')"}"""),
            ("graphdbviewer:queryHistory", """["g.V().limit(7)","g.E()"]"""),
            ("graphdbviewer:lastQuery", "g.V().count()"));
        var workspace = new WorkspaceStore(storage);

        Assert.Equal("g.V().out('knows')", (await workspace.LoadSavedQueriesAsync())["Friends"]);
        Assert.Equal(["g.V().limit(7)", "g.E()"], await workspace.LoadHistoryAsync());
        Assert.Equal("g.V().count()", await workspace.LoadLastQueryAsync());
    }

    [Fact]
    public async Task ItsSavedConnections_ComeBack_EveryField()
    {
        var storage = await StorageWith(("graphdbviewer:connections", """
            {"Local Neo4j":{"Transport":"bolt","Hostname":"localhost","AuthKey":"secret","Port":7687,"UseSSL":false,
              "Database":"neo4j","Collection":null,"DatabaseType":"Neo4j","Endpoint":null,"Username":"neo4j","ViaServer":false},
             "Cosmos":{"Transport":"WebSocket","Hostname":"acct.gremlin.cosmos.azure.com","AuthKey":"k","Port":443,"UseSSL":true,
              "Database":"db","Collection":"graph","DatabaseType":"CosmosDb","Endpoint":null,"Username":null,"ViaServer":true}}
            """));

        //The page's own read (Home.Connections.LoadConnectionsAsync).
        var saved = await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>("graphdbviewer:connections");

        var neo4j = saved["Local Neo4j"];

        Assert.Equal("bolt", neo4j.Transport);
        Assert.Equal("localhost", neo4j.Hostname);
        Assert.Equal("secret", neo4j.AuthKey);
        Assert.Equal(7687, neo4j.Port);
        Assert.Equal("neo4j", neo4j.Database);
        Assert.Equal("Neo4j", neo4j.DatabaseType);
        Assert.Equal("neo4j", neo4j.Username);
        Assert.False(neo4j.ViaServer);

        var cosmos = saved["Cosmos"];

        Assert.True(cosmos.UseSSL);
        Assert.Equal("graph", cosmos.Collection);
        Assert.Equal("CosmosDb", cosmos.DatabaseType);
        Assert.True(cosmos.ViaServer);
    }

    [Fact]
    public async Task ItsStylesheets_ComeBack_WithNoCoordinateMapping()
    {
        var storage = await StorageWith(("graphdbviewer:stylesheets", """
            {"Org chart":{"person":{"Color":"#123456","Size":30,"Shape":"round-rectangle","Shape3d":"sphere",
              "DisplayProperty":"title","Icon":"bi-person","Model":"https://example.test/p.glb"}}}
            """));

        //The page's own read (Home.Styling).
        var sheets = await storage.GetAsync<Dictionary<string, Dictionary<string, LabelStyle>>>("graphdbviewer:stylesheets");
        var person = sheets["Org chart"]["person"];

        Assert.Equal("#123456", person.Color);
        Assert.Equal(30, person.Size);
        Assert.Equal("round-rectangle", person.Shape);
        Assert.Equal("sphere", person.Shape3d);
        Assert.Equal("title", person.DisplayProperty);
        Assert.Equal("bi-person", person.Icon);
        Assert.Equal("https://example.test/p.glb", person.Model);

        //Added since: a label from v1.2.0 maps no coordinates and is read by the conventional names.
        Assert.Null(person.LatitudeProperty);
        Assert.Null(person.LongitudeProperty);
        Assert.Null(person.PointProperty);
    }

    [Fact]
    public async Task ItsAiModels_ComeBack_ButNotTheirEffort_WhichIsChosenPerRequestNow()
    {
        var storage = await StorageWith(("graphdbviewer:llmConnections", """
            {"Claude":{"Name":"Claude","ProviderType":"Anthropic","BaseUrl":null,"ApiKey":"sk-test","Model":"claude-opus-4-8",
              "MaxTokens":4096,"Temperature":0.2,"Effort":"high"},
             "Local":{"Name":"Local","ProviderType":"Custom","BaseUrl":"http://localhost:11434/v1","ApiKey":null,"Model":"llama3",
              "MaxTokens":8192,"Temperature":null,"Effort":null}}
            """));

        var models = await new LlmConnectionStore(storage).LoadAsync();
        var claude = models["Claude"];

        Assert.Equal("Anthropic", claude.ProviderType);
        Assert.Equal("sk-test", claude.ApiKey);
        Assert.Equal("claude-opus-4-8", claude.Model);
        Assert.Equal(4096, claude.MaxTokens);
        Assert.Equal(0.2, claude.Temperature);

        //Never kept since 2026-11-01: the window's model button sets it for each request, starting at the model's default.
        Assert.Equal("", claude.Effort);

        //Kept since 2026-11-02, and absent from what v1.2.0 wrote: the published rates, and a PDF sent as the file.
        Assert.Null(claude.InputPrice);
        Assert.False(claude.PdfAsText);

        Assert.Equal("Custom", models["Local"].ProviderType);
        Assert.Equal("http://localhost:11434/v1", models["Local"].BaseUrl);
    }

    [Fact]
    public async Task ItsSmallPreferences_ComeBack()
    {
        var storage = await StorageWith(
            ("graphdbviewer:expansionLimit", "75"),
            ("graphdbviewer:kgMergeMode", "true"),
            ("graphdbviewer:fullScreen", "true"));

        //Read as Home, GraphProposalCard (the Merge / Replace choice, KgGenerateModal's in v1.2.0) and ShellState read them.
        Assert.Equal(75, await storage.GetAsync<int?>("graphdbviewer:expansionLimit"));
        Assert.True(await storage.GetAsync<bool>(GraphProposalCard.MergeModeKey));
        Assert.True(await storage.GetAsync<bool>("graphdbviewer:fullScreen"));
    }

    //An address v1.2.0 read, with every name it took, in its main form and in its other one: the same settings come out.
    [Theory]
    [InlineData("?dbtype=neo4j&host=db.e2e.test&port=7687&ssl=true&database=graph&collection=c&key=k&user=u&viaserver=false&q=MATCH+(n)+RETURN+n&lang=cypher&view=3d&connect=true&run=true")]
    [InlineData("?databasetype=neo4j&hostname=db.e2e.test&port=7687&usessl=true&database=graph&collection=c&authkey=k&username=u&proxy=false&query=MATCH+(n)+RETURN+n&language=cypher&mode=3d&autoconnect=true&autorun=true")]
    public void AnAddressItRead_IsReadTheSame(string address)
    {
        var s = EmbedSettings.Parse(address);

        Assert.Equal("Neo4j", s.DatabaseType);
        Assert.Equal("db.e2e.test", s.Hostname);
        Assert.Equal(7687, s.Port);
        Assert.True(s.UseSsl);
        Assert.Equal("graph", s.Database);
        Assert.Equal("c", s.Collection);
        Assert.Equal("k", s.AuthKey);
        Assert.Equal("u", s.Username);
        Assert.False(s.ViaServer);
        Assert.Equal("MATCH (n) RETURN n", s.Query);
        Assert.Equal("cypher", s.Language);
        Assert.Equal(3, s.View);
        Assert.True(s.Connect);
        Assert.True(s.AutoRun);
    }

    //Its other database names and a SPARQL endpoint, as its NormalizeDbType and BuildConnection read them.
    [Theory]
    [InlineData("?dbtype=cosmos&host=a.gremlin.cosmos.azure.com", "CosmosDb")]
    [InlineData("?dbtype=gremlin&host=g.e2e.test", "ApacheTinkerPop")]
    [InlineData("?dbtype=rdf&url=https://sparql.e2e.test/query", "Sparql")]
    [InlineData("?url=https://sparql.e2e.test/query", "Sparql")]
    [InlineData("?host=g.e2e.test", "ApacheTinkerPop")]
    public void ItsDatabaseNames_NameTheSameDatabase(string address, string type)
    {
        Assert.Equal(type, EmbedSettings.Parse(address).BuildConnection().DatabaseType);
    }
}
