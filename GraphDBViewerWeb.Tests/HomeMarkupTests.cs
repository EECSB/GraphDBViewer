using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bunit;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;
using GraphDBViewerWeb.Layout;
using GraphDBViewerWeb.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GraphDBViewerWeb.Tests;

//Markup and interaction cover for Home. Every other test here is pure xUnit over C#, which cannot see a
//.razor file at all — so 9972176 renamed the gremlin/sparql fields to db, the replace leaked into six
//string literals in the markup, and a 352-green suite said nothing while the Gremlin export silently
//wrote JSON and the editor's language picker offered the same value twice. These pin the literals that
//C# reads back, and the interactions (like the import confirm) that only exist at the component layer.
public class HomeMarkupTests : BunitContext
{
    private const string DotA = "digraph { Alice -> Bob [label=knows] }";
    private const string DotB = "digraph { Zeta -> Yara [label=knows] }";

    //A storage that raises StorageQuotaExceeded on demand — mimicking LocalAppStorage after it catches a
    //QuotaExceededError: the write is swallowed (no throw) and the event fires so the UI can warn.
    private sealed class QuotaStorage : IAppStorage
    {
        public event Action StorageQuotaExceeded;

        public void RaiseQuota()
        {
            StorageQuotaExceeded?.Invoke();
        }

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

    //Home renders inside its real layout, the way the router composes it, because the top bar is the
    //shell's now rather than page content, and a viewer rendered alone would have no bar at all. (The
    //setup controls these walk, Import/Export and the connection pill, are the page's own since
    //2026-10-29, in the workspace's row; the layout stays for the bar around them.)
    private IRenderedComponent<MainLayout> RenderHome(IAppStorage storage = null, HttpClient http = null, string url = null, RenderFragment body = null)
    {
        Services.AddSingleton<IAppStorage>(storage ?? new NullStorage());
        Services.AddSingleton(http ?? new HttpClient());
        Services.AddScoped<WorkspaceStore>();
        Services.AddScoped<LlmConnectionStore>();
        Services.AddScoped<ShellState>();
        Services.AddSingleton(new ViewerHostOptions { AppName = "Treeality", HasServerRoute = true });

        //Home drives Monaco and Cytoscape on render. Loose mode returns default for anything unconfigured
        //rather than throwing, which is all these need — the assertions are about markup, not interop.
        JSInterop.Mode = JSRuntimeMode.Loose;

        //After the registrations, since asking for a service closes them: an embed URL is read on first render.
        if (url != null)
            Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        return Render<MainLayout>(p => p.Add(l => l.Body, body ?? HomeBody));
    }

    //The layout's Body, built by hand since a .cs file cannot hold markup: just <Home />.
    private static readonly RenderFragment HomeBody = builder =>
    {
        builder.OpenComponent<Home>(0);
        builder.CloseComponent();
    };

    //The viewer as /embed renders it (2026-10-21, Embed.razor): <Home Embedded="true" />.
    private static readonly RenderFragment EmbeddedBody = builder =>
    {
        builder.OpenComponent<Home>(0);
        builder.AddAttribute(1, nameof(Home.Embedded), true);
        builder.CloseComponent();
    };

    //A storage that remembers what it is given, as JSON, the way the real ones do: what a previous session left
    //behind, for a test that starts from it.
    private sealed class InMemoryStorage : IAppStorage
    {
        private readonly Dictionary<string, string> _data = new();

        public event Action StorageQuotaExceeded { add { } remove { } }

        public Task<T> GetAsync<T>(string key)
        {
            if (!_data.TryGetValue(key, out var json))
                return Task.FromResult<T>(default);

            return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
        }

        public Task SetAsync<T>(string key, T value)
        {
            _data[key] = System.Text.Json.JsonSerializer.Serialize(value);

            return Task.CompletedTask;
        }

        public Task<string> GetStringAsync(string key)
        {
            if (!_data.TryGetValue(key, out var text))
                return Task.FromResult<string>(null);

            return Task.FromResult(text);
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

    ///<summary>
    ///An address that carries a query is opened to see what drew the result. A tab restored on the Generated
    ///editor — something earlier staged an edit or saved an arrangement — took the query into an editor nobody
    ///was looking at, and the query ran from a page that looked empty (the open item of 2026-09-19). The Query
    ///tab comes forward with the address's query in it.
    ///</summary>
    [Fact]
    public async Task AnAddressWithAQuery_BringsTheQueryTabForward()
    {
        var storage = new InMemoryStorage();

        //A tab as a session leaves it: with a result, so the working area is on screen, and on the Generated editor.
        await storage.SetAsync("graphdbviewer:tabs", new List<TabSnapshot>
        {
            new()
            {
                Id = "t1",
                Name = "Query 1",
                QueryEditorTab = 2,
                VisualizationMode = 2,
                EditorLanguage = "gremlin",
                QueryResults = "[]",
                LastResultJson = "[]",
                GraphResultJson = "[]"
            }
        });

        var create = JSInterop.SetupVoid("monacoInterop.create", _ => true);
        var set = JSInterop.SetupVoid("monacoInterop.setValue", _ => true);

        create.SetVoidResult();
        set.SetVoidResult();

        var cut = RenderHome(storage, url: "http://localhost/?view=json&query=g.V().limit(1)&connect=false");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button.nav-link"), b => b.TextContent.Trim() == "Query" && b.ClassList.Contains("active")), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(cut.FindAll("button.nav-link"), b => b.TextContent.Trim() == "Generated" && b.ClassList.Contains("active"));

        //And the query reached the editor on screen: made with it, or told it.
        cut.WaitForAssertion(() => Assert.True(
            create.Invocations.Any(i => (string)i.Arguments[1] == "g.V().limit(1)")
            || set.Invocations.Any(i => (string)i.Arguments[0] == "g.V().limit(1)")), TimeSpan.FromSeconds(5));
    }

    ///<summary>
    ///Enters offline mode and waits for it to actually land.
    ///
    ///StartBlankCanvasAsync ends in a JS render, so the new state is not on screen the instant Click()
    ///returns. Reading the markup straight afterwards passed by luck rather than by rule, and an
    ///assertion that something is *absent* is the dangerous kind: it passes while the click is still in
    ///flight, for the wrong reason, and whatever runs next then acts on the markup the click was about to
    ///replace. That is exactly how OfflineMode_ExitStaysReachableThroughTheTopBar failed on a CI runner
    ///and never once here -- the slower the machine, the wider the window.
    ///
    ///The wait watches the toggle going, which is the observable end of the transition. The toggle is the
    ///empty page's "Offline mode" invitation, or the connection dialog's when that is open (both marked
    ///by a test id, and both go with the workspace arriving); the pill in the workspace's row that then
    ///reads "Offline mode" is marked otherwise, so the two cannot be confused.
    ///</summary>
    private void EnterOfflineMode(IRenderedComponent<MainLayout> cut)
    {
        cut.Click("[data-testid=connection-offline], [data-testid=viewer-offline]");

        //Five seconds rather than bUnit's one. The machine this guards against is a loaded two-core CI
        //runner, so the default is measured against exactly the conditions that are not the problem here.
        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid=connection-offline], [data-testid=viewer-offline]")),
            TimeSpan.FromSeconds(5));
    }

    //Opens the connection dialog from the pill in the workspace's row (2026-10-29; the top bar's button before).
    private static void OpenConnectionDialog(IRenderedComponent<MainLayout> cut)
    {
        if (cut.FindAll("[data-testid=connection-dialog]").Count == 0)
            cut.Click("[data-testid=viewer-connection]");

        cut.WaitForElement("[data-testid=connection-dialog]", TimeSpan.FromSeconds(5));
    }

    //The saved connections' picker lists its entries in a menu it opens (2026-11-04): the keys listed, opening it first.
    private static List<string> ConnectionKeys(IRenderedComponent<MainLayout> cut)
    {
        OpenConnectionDialog(cut);

        if (cut.FindAll("[data-testid=connection-picker-menu]").Count == 0)
            cut.Click("[data-testid=connection-picker-chevron]");

        return cut.FindAll("[data-testid=connection-picker-menu] [data-key]").Select(r => r.GetAttribute("data-key")).ToList();
    }

    //Chooses a saved connection from the picker's menu, as a click on its row does.
    private static void PickSavedConnection(IRenderedComponent<MainLayout> cut, string key)
    {
        ConnectionKeys(cut);

        cut.Click($"[data-testid=connection-picker-menu] [data-key='{key}']");
    }

    private List<string> OptionValues(IRenderedComponent<MainLayout> cut)
    {
        return cut.FindAll("option").Select(o => o.GetAttribute("value")).ToList();
    }

    private void OpenImportPanel(IRenderedComponent<MainLayout> cut)
    {
        //The panel is open at boot (showImportExport starts true), so an unconditional click would
        //toggle it shut — the e2e helper's retry loop hides the same trap. Click only when it's closed.
        if (cut.FindAll("textarea[placeholder^='GraphSON']").Count == 0)
            cut.Click(c => c.FindAll("button").First(b => b.TextContent.Contains("Import/Export")));
    }

    private void PasteAndGenerate(IRenderedComponent<MainLayout> cut, string dot)
    {
        cut.Input("textarea[placeholder^='GraphSON']", dot);
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Generate queries"));
    }

    //The staged Generated queries, read off the Generated tab's Monaco component — the markup itself
    //doesn't carry the text (Monaco draws it via JS), but the component's Value parameter is the buffer.
    private string GeneratedBuffer(IRenderedComponent<MainLayout> cut)
    {
        return cut.FindComponent<MonacoEditor>().Instance.Value;
    }

    //RunExportChoiceAsync branches on exportChoice == "gremlin". When the option offered "db" instead, the
    //branch was simply unreachable and every export fell through to JSON, under the wrong filename.
    [Fact]
    public void GraphExport_OffersTheValueRunExportChoiceActuallyReads()
    {
        var cut = RenderHome();

        OpenImportPanel(cut);

        var values = OptionValues(cut);

        Assert.Contains("gremlin", values);
        Assert.Contains("json", values);
    }

    //NormalizeEditorLanguage recognizes exactly these two and collapses anything else to "gremlin", so an
    //option carrying any other value is silently unselectable.
    [Fact]
    public void EditorLanguages_OfferOnlyValuesNormalizeEditorLanguageKeeps()
    {
        var cut = RenderHome();

        //The editor lives behind `@if (isConnected || offlineMode)`, so reveal the workspace first.
        EnterOfflineMode(cut);

        var values = OptionValues(cut);

        Assert.Contains("gremlin", values);
        Assert.Contains("sparql", values);
        Assert.DoesNotContain("db", values);
    }

    //Entering offline mode used to unmount its own exit button: the Offline/Exit toggle lives inside the
    //connection dialog, and StartBlankCanvasAsync closes that dialog — leaving the pill claiming
    //"Disconnected" with no visible way back out. The pill now names the mode and reopens the dialog,
    //which is the path back; this walks the whole round trip that used to dead-end.
    [Fact]
    public void OfflineMode_ExitStaysReachableThroughThePill()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);

        //The dialog closed with entry, taking the toggle with it — the pill carries the state instead.
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode");
        Assert.Contains("Offline mode", cut.Find("[data-testid=viewer-connection]").TextContent);
        cut.Click("[data-testid=viewer-connection]");

        //Reopening the dialog is the pill's whole job here, so wait for the toggle it brings back rather
        //than reaching for it and blaming the button when it is not there yet.
        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode"),
            TimeSpan.FromSeconds(5));
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Exit offline mode"));

        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Disconnected")),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode");
    }

    //On the Globe at 1280 px the status line wrapped its counts into three lines beside Label options and Node size on
    //map (2026-11-07, at the showcase's refresh): the row kept the controls at their width and squeezed the counts. The
    //row wraps instead, so the controls take the next line and the counts stay on one.
    [Fact]
    public void TheCanvasStatusRow_WrapsItsControlsUnderTheCounts_RatherThanSqueezingThem()
    {
        var cut = RenderHome();

        OpenImportPanel(cut);
        cut.Input("textarea[placeholder^='GraphSON']", DotA);
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Visualize"));

        var row = cut.WaitForElement("[data-testid=canvas-status]", TimeSpan.FromSeconds(5));

        Assert.Contains("flex-wrap", row.ClassName);
        Assert.Contains("vertices", row.TextContent);
    }

    //The page's two edge handles (2026-10-29) stand beside the split, outside an embedded viewer, each a separator
    //of its own; the middle divider is still there between them.
    [Fact]
    public void TheWorkspaceHasAnEdgeHandleOnEachSide()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);

        cut.WaitForElement("[data-testid=split-edge-left]", TimeSpan.FromSeconds(5));

        Assert.Equal("separator", cut.Find("[data-testid=split-edge-right]").GetAttribute("role"));
        Assert.NotNull(cut.Find(".split-gutter"));
    }

    //The query limits sit in the query sidebar since 2026-10-29, beside Load DB, where a Query settings dropdown in
    //the bar held them: each box behind its own label, carrying its own value, so the two cannot drift onto each
    //other unnoticed. The sidebar is only there with a workspace, so offline mode is the way in.
    [Fact]
    public void TheQueryLimitsSitInTheSidebar_EachBesideItsOwnLabel()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);

        var query = cut.WaitForElement("[data-testid=query-limit]", TimeSpan.FromSeconds(5));
        var expand = cut.Find("[data-testid=expand-limit]");

        Assert.Contains("limit", query.ParentElement.TextContent);
        Assert.Contains("expand", expand.ParentElement.TextContent);
        Assert.Contains("Expand limit", expand.ParentElement.GetAttribute("title"));
        Assert.Equal("50", expand.GetAttribute("value"));
        Assert.DoesNotContain("Query settings", cut.Markup);
    }

    //0 is no cap on an expand, as every query builder reads it. Until 2026-10-31 the box clamped what was typed to 1,
    //so the uncapped expand its comment and DOCUMENTATION promised could not be asked for.
    [Fact]
    public async Task TheExpandLimitTakesZero_ForNoCap_AndKeepsIt()
    {
        var storage = new InMemoryStorage();
        var cut = RenderHome(storage);

        EnterOfflineMode(cut);

        cut.WaitForElement("[data-testid=expand-limit]", TimeSpan.FromSeconds(5));
        cut.Change("[data-testid=expand-limit]", "0");

        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find("[data-testid=expand-limit]").GetAttribute("value")), TimeSpan.FromSeconds(5));
        Assert.Equal("0", cut.Find("[data-testid=expand-limit]").GetAttribute("min"));
        Assert.Contains("0 for no limit", cut.Find("[data-testid=expand-limit]").ParentElement.GetAttribute("title"));
        Assert.Equal(0, await storage.GetAsync<int?>("graphdbviewer:expansionLimit"));

        //Below it is still no cap, not a number of its own.
        cut.Change("[data-testid=expand-limit]", "-3");

        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find("[data-testid=expand-limit]").GetAttribute("value")), TimeSpan.FromSeconds(5));
    }

    //The DatabaseType buttons write literals that GraphDbProviders.For reads back — and For(unknown)
    //silently falls back to TinkerPop, so a drifted value wouldn't error, it would quietly change the
    //form shape and the capability gates. The form shape is the observable end of that chain.
    [Fact]
    public void SparqlButton_SwapsTheFormToAnEndpointUrl()
    {
        var cut = RenderHome();

        OpenConnectionDialog(cut);

        //TinkerPop is the default shape: host/port, no endpoint URL.
        Assert.Contains(cut.FindAll("label"), l => l.TextContent.StartsWith("Hostname"));

        ClickDatabaseType(cut, "SPARQL / RDF");

        //The endpoint input replaces host/port, and its example URL is a real SPARQL endpoint (the
        //rename leak had turned it into wikidata.org/db).
        Assert.NotNull(cut.Find("input[placeholder='https://query.wikidata.org/sparql']"));
        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.StartsWith("Hostname"));
    }

    [Fact]
    public void CosmosButton_RevealsDatabaseAndCollection()
    {
        var cut = RenderHome();

        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Collection");

        ClickDatabaseType(cut, "Cosmos DB");

        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Database");
        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Collection");
    }

    //A Gremlin server with authentication on (JanusGraph, or TinkerPop's own) logs in with a username AND a
    //password. The form offered one field, "Auth Key / Password", so there was no way to enter a login.
    [Fact]
    public void TinkerPop_AsksForAUsernameAndAPassword()
    {
        var cut = RenderHome();

        OpenConnectionDialog(cut);
        cut.WaitForElement("[aria-label=\"Database type\"]", TimeSpan.FromSeconds(5));

        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Username");
        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Password");
        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Auth Key / Password");
    }

    //Cosmos DB logs in with its key, under a username built from the database and collection, so its form
    //keeps the single field.
    [Fact]
    public void CosmosButton_KeepsTheSingleKeyField()
    {
        var cut = RenderHome();

        ClickDatabaseType(cut, "Cosmos DB");

        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Auth Key / Password");
        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Username");
    }

    //Run asks the query builder whether a query changes the graph before running it, and SPARQL has no
    //builder: every query anyone ran on a SPARQL connection threw a NullReferenceException before it was
    //sent, from 8e95993 until the live database check ran one against Fuseki. An embed URL is the way in,
    //since it connects and runs a query without the editor, which is Monaco and cannot be typed into here.
    [Fact]
    public void RunningAQueryOnASparqlConnection_ShowsWhatTheEndpointAnswered()
    {
        const string answer = @"{""head"":{""vars"":[""s""]},""results"":{""bindings"":[{""s"":{""type"":""uri"",""value"":""urn:gdbv:check-a""}}]}}";

        var query = Uri.EscapeDataString("SELECT ?s WHERE { ?s ?p ?o }");
        var endpoint = Uri.EscapeDataString("https://sparql.test/ds/sparql");
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/ds/sparql"] = answer }));

        var cut = RenderHome(http: http, url: $"http://localhost/?dbType=Sparql&endpoint={endpoint}&viaServer=false&query={query}");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Bindings", cut.Markup);
            Assert.Contains("urn:gdbv:check-a", cut.Markup);
        }, TimeSpan.FromSeconds(10));
    }

    //What a Gremlin server over GraphSON 3 answers an element map with, JanusGraph 1.1 here: each vertex a g:Map
    //keyed by g:T tokens for its id and label. Converting such a map threw, and the throw came out of a render,
    //since the page reads every answer through ToTable to decide whether it holds a graph to show.
    private const string ElementMapAnswer = """
        {"requestId":"5f7c2a9e-0d7e-4d32-9a53-1b0e6f3b8c11","status":{"message":"","code":200,"attributes":{"@type":"g:Map","@value":[]}},
         "result":{"data":{"@type":"g:List","@value":[
           {"@type":"g:Map","@value":[
             {"@type":"g:T","@value":"id"},{"@type":"g:Int64","@value":4096},
             {"@type":"g:T","@value":"label"},"Part",
             "name","M3 Screw",
             "qty",{"@type":"g:Int32","@value":250}
           ]}
         ]},"meta":{"@type":"g:Map","@value":[]}}}
        """;

    //The query box's Run, through an address that connects to a Gremlin server over HTTP and runs the query on the
    //Table view: the one way in that needs no Monaco, which cannot be typed into here.
    [Fact]
    public void AnElementMapAnswer_IsTabulated_AndThePageStaysUp()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var query = Uri.EscapeDataString("g.V().elementMap()");

        var cut = RenderHome(http: http, url: $"http://localhost/?dbType=gremlin&transport=http&host=gremlin.test&port=8182&ssl=false&viaServer=false&view=table&query={query}");

        cut.WaitForAssertion(() =>
        {
            var cells = cut.FindAll("table.gdbv-data-table td").Select(c => c.TextContent.Trim()).ToList();

            Assert.Contains("4096", cells);
            Assert.Contains("Part", cells);
            Assert.Contains("M3 Screw", cells);
            Assert.Contains("250", cells);
        }, TimeSpan.FromSeconds(10));
    }

    //What a Gremlin server over GraphSON 3 answers g.E().project('v','e','o').by(outV().id()).by(id()).by(inV().id()): a
    //map under the names the viewer reads a traversal's vertex, edge and other vertex by, each holding an id rather than
    //the element, a shape no reader of a result expects. Every one of them throws on it, the first out of a render until
    //2026-10-31.
    private const string UnreadableAnswer = """
        {"requestId":"9a1d7c3e-2b4f-4e8a-9c6d-0f1e2d3c4b5a","status":{"message":"","code":200,"attributes":{"@type":"g:Map","@value":[]}},
         "result":{"data":{"@type":"g:List","@value":[
           {"@type":"g:Map","@value":[
             "v",{"@type":"g:Int64","@value":4096},
             "e",{"@type":"g:Int64","@value":7},
             "o",{"@type":"g:Int64","@value":4097}
           ]}
         ]},"meta":{"@type":"g:Map","@value":[]}}}
        """;

    //The same answer from a server that types nothing.
    private const string UnreadableResult = """[{"v":4096,"e":7,"o":4097}]""";

    //A result the viewer cannot read as a graph says so where the table would be, and the page stays up around it: the
    //query box and the tabs are still there to change the query or close the tab. Until 2026-10-31 the throw came out of
    //the render and took the page down (GraphDBViewer issue 2).
    [Fact]
    public void AResultTheViewerCannotRead_SaysSo_AndThePageStaysUp()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = UnreadableAnswer }));
        var query = Uri.EscapeDataString("g.E().project('v','e','o').by(outV().id()).by(id()).by(inV().id())");

        var cut = RenderHome(http: http, url: $"http://localhost/?dbType=gremlin&transport=http&host=gremlin.test&port=8182&ssl=false&viaServer=false&view=table&query={query}");

        cut.WaitForAssertion(() => Assert.Contains("could not read this result as a graph", cut.Find("[data-testid=unreadable-result]").TextContent), TimeSpan.FromSeconds(10));

        Assert.NotEmpty(cut.FindAll("[data-testid=workspace-tabs]"));
        Assert.NotEmpty(cut.FindAll("button[title^='Run Query:']"));
    }

    //Such a result is still the tab's own, and kept with it like any other: drawn in 2D it is not drawn at all, and the
    //draw that never happened is where a tab's result was saved, so a reload opened on whatever the tab held before.
    [Fact]
    public async Task AResultTheViewerCannotRead_IsKeptWithItsTab()
    {
        var storage = new InMemoryStorage();
        var cut = RenderHome(storage);

        OpenImportPanel(cut);
        cut.Input("textarea[placeholder^='GraphSON']", UnreadableResult);
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Visualize"));

        cut.WaitForElement("[data-testid=unreadable-result]", TimeSpan.FromSeconds(5));

        //Kept with its connection's tabs since there can be several connections (2026-11-03).
        var kept = await storage.GetAsync<List<ConnectionSnapshot>>("graphdbviewer:openConnections");

        Assert.Contains("4097", Assert.Single(Assert.Single(kept).Tabs).LastResultJson);
    }

    //The open connections come back as they were left (the multiple-connections spec, task 3): the one in front in front
    //again, its form filled from the saved connection it was picked from, not from the copy kept with it, which a later
    //edit of the saved one would have left behind.
    [Fact]
    public async Task TheOpenConnectionsComeBack_TheirFormsFromTheSavedOnes()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", new Dictionary<string, GremlinDB.GremlinConnection>
        {
            ["Saved A"] = new("WebSocket", 8182, false, "a.example", "secret", "", "")
        });
        await storage.SetAsync("graphdbviewer:openConnections", new List<ConnectionSnapshot>
        {
            new() { Id = "a", SavedKey = "Saved A", Form = new("WebSocket", 8182, false, "stale.example", "", "", ""), Tabs = [new TabSnapshot { Id = "a1", Name = "A1" }] },
            new() { Id = "b", Form = new("WebSocket", 8182, false, "b.example", "", "", ""), Tabs = [new TabSnapshot { Id = "b1", Name = "B1" }] }
        });
        await storage.SetAsync("graphdbviewer:activeConnection", 0);

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Contains("value=\"a.example\"", cut.Markup), TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("stale.example", cut.Markup);
        Assert.Empty(cut.FindAll("input[value='b.example']"));

        //Neither connected, both are in the row all the same, each tab naming its own connection.
        var connections = cut.FindAll("[data-testid=viewer-connection]");

        Assert.Equal(2, connections.Count);
        Assert.Contains("a.example:8182", connections[0].TextContent);
        Assert.Contains("b.example:8182", connections[1].TextContent);
    }

    //And kept with its tab, the same result is what the page opens with on the next visit. It opens on the warning, and
    //Go to JSON shows the result as it came back. Until 2026-10-31 the page threw again on every reload, and could only
    //be got back by deleting the stored tabs from the browser's storage by hand (GraphDBViewer issue 2).
    [Fact]
    public async Task ATabKeptWithAResultTheViewerCannotRead_OpensOnTheWarning_AndItsJson()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:tabs", new List<TabSnapshot>
        {
            new()
            {
                Id = "t1",
                Name = "Query 1",
                VisualizationMode = 2,
                EditorLanguage = "gremlin",
                QueryResults = UnreadableResult,
                LastResultJson = UnreadableResult
            }
        });

        var cut = RenderHome(storage);

        var warning = cut.WaitForElement("[data-testid=unreadable-result]", TimeSpan.FromSeconds(5));

        Assert.Contains("could not read this result as a graph", warning.TextContent);
        Assert.Empty(cut.FindAll("#cyGraph"));

        cut.Click("[data-testid=unreadable-result] button");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindComponents<MonacoViewer>(), v => v.Instance.Value == UnreadableResult), TimeSpan.FromSeconds(5));
    }

    //Share (2026-10-31, GDS Viewer's Share popup) beside Import/Export: its Embed tab writes the iframe for the embedded
    //viewer on the connection open, with the tab's query, and without the password the connection was given, which it
    //says it left out.
    [Fact]
    public void Share_EmbedsTheConnectionOpen_WithoutItsPassword()
    {
        var cut = RenderHome(url: "http://localhost/?host=graph.example.com&port=8182&authKey=s3cret&connect=false&query=g.V().limit(5)&view=3d");

        cut.WaitForAssertion(() => Assert.Contains("graph.example.com:8182", cut.Find("[data-testid=viewer-connection]").TextContent), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-share]");
        cut.WaitForElement("[data-testid=share-dialog]", TimeSpan.FromSeconds(5));

        //A popup's slim bar, its title after the Share button's picture (2026-11-04, the user).
        Assert.Single(cut.FindAll("[data-testid=share-dialog] .gdbv-popup-bar > .gdbv-popup-title > .bi-share.gdbv-title-icon"));

        cut.Click("[data-testid=share-embed-tab]");

        var code = cut.Find("[data-testid=share-output]").GetAttribute("value");

        Assert.StartsWith("<iframe src=\"http://localhost/embed?host=graph.example.com&amp;port=8182&amp;query=", code);
        Assert.Contains("view=3d", code);
        Assert.DoesNotContain("s3cret", code);
        Assert.Contains("login is left out", cut.Find("[data-testid=share-source-note]").TextContent);

        //And Link is the full viewer's address, opened by the link beside the copy button.
        cut.Click("[data-testid=share-link-tab]");

        Assert.StartsWith("http://localhost/?host=graph.example.com", cut.Find("[data-testid=share-output]").GetAttribute("value"));

        cut.Click("[data-testid=share-dialog] .btn-close");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=share-dialog]")), TimeSpan.FromSeconds(5));
    }

    //Share links to the connection in front, with its query tab (the multiple-connections spec, story 7 and task 6):
    //with two open, each one's link names its own host and carries its own tab's query, and never the other's.
    [Fact]
    public void Share_WithTwoConnectionsOpen_LinksTheOneInFront()
    {
        var cut = RenderHome(url: "http://localhost/?host=graph.example.com&port=8182&connect=false&query=g.V().limit(5)");

        cut.WaitForAssertion(() => Assert.Contains("graph.example.com:8182", cut.Find("[data-testid=viewer-connection]").TextContent), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        var second = OpenConnections(cut)[1].Connection;

        second.Hostname = "second.example.com";
        second.Port = 8183;

        //The second, in front: its host, and no query, its one tab being empty.
        var link = ShareLinkOf(cut);

        Assert.StartsWith("http://localhost/?host=second.example.com&port=8183", link);
        Assert.DoesNotContain("graph.example.com", link);
        Assert.DoesNotContain("limit", link);

        //The first, brought in front by its query tab: its host and its tab's query.
        cut.Click(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName), TimeSpan.FromSeconds(5));

        link = ShareLinkOf(cut);

        Assert.StartsWith("http://localhost/?host=graph.example.com&port=8182", link);
        Assert.Contains("limit(5)", Uri.UnescapeDataString(link));
        Assert.DoesNotContain("second.example.com", link);
    }

    //The link the Share dialog's Link tab gives, the dialog then closed.
    private static string ShareLinkOf(IRenderedComponent<MainLayout> cut)
    {
        cut.Click("[data-testid=viewer-share]");
        cut.WaitForElement("[data-testid=share-dialog]", TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=share-link-tab]");

        var link = cut.Find("[data-testid=share-output]").GetAttribute("value");

        cut.Click("[data-testid=share-dialog] .btn-close");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=share-dialog]")), TimeSpan.FromSeconds(5));

        return link;
    }

    //An address on the given page that connects to a Gremlin server over HTTP and draws what it answers in 2D.
    private static string GraphIn2D(string page)
    {
        var query = Uri.EscapeDataString("g.V().elementMap()");

        return $"http://localhost/{page}?dbType=gremlin&transport=http&host=gremlin.test&port=8182&ssl=false&viaServer=false&view=2d&query={query}";
    }

    //Embedded in another site (2026-10-21), the viewer is the viewer alone: none of the cards that set it up, which
    //belong to somebody's own workspace, and the page marked so that its stylesheet leaves the query side out.
    [Fact]
    public void Embedded_NothingLoaded_ShowsNoCardThatSetsTheViewerUp()
    {
        var cut = RenderHome(body: EmbeddedBody);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=embed-empty]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=connection-picker]"));
        Assert.Empty(cut.FindAll("textarea[placeholder^='GraphSON']"));
        Assert.NotEmpty(cut.FindAll(".gdbv-embedded"));
    }

    //The same page outside /embed, so that what the test above finds missing is something that is there: the
    //invitations, and behind each of their first two buttons the dialog that sets the viewer up (2026-10-29).
    [Fact]
    public void NotEmbedded_NothingLoaded_ShowsTheWaysInAndTheDialogsBehindThem()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-empty]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=connection-picker]"));
        Assert.Empty(cut.FindAll("textarea[placeholder^='GraphSON']"));

        cut.Click("[data-testid=viewer-empty-connect]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=connection-picker]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=connection-dialog] .btn-close");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=connection-picker]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-empty-import]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("textarea[placeholder^='GraphSON']")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll(".gdbv-embedded"));
        Assert.Empty(cut.FindAll("[data-testid=embed-empty]"));
    }

    //Where the bar has no page tabs, the standalone viewer's, the connection and Import/Export are fixed tabs, the connection
    //before the query tabs and Import/Export after them, with no x to close them; the bar holds neither (they were its tabs on 2026-11-02). One tab is in front at
    //a time (2026-11-03, the user): a press on the connection or Import/Export brings its content where the graph's card
    //stands, in place of the page's invitation before there is a workspace and over the card after, and a query tab
    //brings the card back. Share is one of the bar's icons, between full width and Settings, and Settings is the gear
    //alone. Leaving the page takes Share out.
    [Fact]
    public void WithNoPageTabs_TheViewersControlsAreTabsWhoseContentStandsInTheGraphsCard()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-setup-row] [data-testid=viewer-tabs] [data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));

        var tabs = cut.Find("[data-testid=viewer-tabs]");

        Assert.Single(cut.FindAll("[data-testid=viewer-import-export]"));
        Assert.Single(tabs.QuerySelectorAll(".nav-item > .nav-link[data-testid=viewer-connection]"));
        Assert.Single(tabs.QuerySelectorAll(".nav-item > .nav-link[data-testid=viewer-import-export]"));
        Assert.Contains("gdbv-ws-tabs", tabs.ClassName);
        Assert.Empty(tabs.QuerySelectorAll("[title='Close tab']"));
        Assert.DoesNotContain("×", tabs.TextContent);
        Assert.Empty(cut.FindAll("[data-testid=shell-contribution]"));
        Assert.Empty(tabs.QuerySelectorAll(".nav-link.active"));

        //Share: an icon, after full width and before Settings, outside the tabs.
        var share = cut.Find("[data-testid=shell-band] [data-testid=viewer-share]");
        var bar = cut.Find("[data-testid=shell-band]").InnerHtml;

        Assert.Contains("gdbv-bar-icon", share.ClassName);
        Assert.Empty(share.TextContent.Trim());
        Assert.True(bar.IndexOf("Full-width") < bar.IndexOf("data-testid=\"viewer-share\""));
        Assert.True(bar.IndexOf("data-testid=\"viewer-share\"") < bar.IndexOf("data-testid=\"shell-settings\""));

        var settings = cut.Find("[data-testid=shell-settings]");

        Assert.Contains("gdbv-bar-icon", settings.ClassName);
        Assert.Empty(settings.TextContent.Trim());

        //Before a workspace: Import/Export's content in place of the invitation, in the page and not a dialog over it,
        //then the connection's in its place, each tab in front in turn.
        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("textarea[placeholder^='GraphSON']")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=viewer-empty]"));
        Assert.Empty(cut.FindAll(".modal [data-testid=import-export-dialog], [data-testid=import-export-dialog].modal"));
        Assert.Contains("active", cut.Find("[data-testid=viewer-import-export]").ClassName);
        Assert.Equal("true", cut.Find("[data-testid=viewer-import-export]").GetAttribute("aria-selected"));

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=import-export-dialog]"));
        Assert.Contains("active", cut.Find("[data-testid=viewer-connection]").ClassName);
        Assert.DoesNotContain("active", cut.Find("[data-testid=viewer-import-export]").ClassName);

        cut.Click("[data-testid=shell-band] [data-testid=viewer-share]");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find("[data-testid=viewer-share]").ClassName), TimeSpan.FromSeconds(5));

        EnterOfflineMode(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connection] .bi-pencil-square")), TimeSpan.FromSeconds(5));

        //With a workspace: one strip, the connection first of all (2026-11-03, the user), the query tabs, the gap, then
        //Import/Export, and the spacer over the sidebar. One of each tab.
        var row = cut.Find("[data-testid=viewer-setup-row]");

        Assert.Contains("gdbv-tabstrip-row", row.ClassName);
        Assert.Equal(["viewer-connections", "gdbv-tabstrip-gap", "viewer-tabs", "gdbv-tabstrip-side"], row.Children.Select(c => c.GetAttribute("data-testid") ?? c.ClassName));
        Assert.Equal(["viewer-connection-tab", "workspace-tabs"], row.Children[0].Children.Select(c => c.GetAttribute("data-testid")));
        Assert.Single(row.QuerySelectorAll("[data-testid=viewer-connection-tab] .nav-item > .nav-link[data-testid=viewer-connection]"));
        Assert.Equal(["viewer-add-connection", "viewer-import-export"], row.QuerySelectorAll("[data-testid=viewer-tabs] .nav-item > .nav-link").Select(l => l.GetAttribute("data-testid")));
        Assert.Single(cut.FindAll("[data-testid=viewer-connection]"));
        Assert.Single(cut.FindAll("[data-testid=viewer-import-export]"));

        //Over the sidebar, its own tabs in the strip's look (2026-11-03, the user); the card's copy is for the stacked
        //layout only.
        Assert.Equal(["Query", "Generated", "Examples", "History"], row.QuerySelectorAll(".gdbv-tabstrip-side > .gdbv-ws-tabs[data-tour=query-tabs] .nav-link").Select(l => l.TextContent.Trim().TrimEnd('!').Trim()));
        Assert.Contains("d-lg-none", cut.Find(".split-side > .card [data-tour=query-tabs]").ParentElement.ClassList);

        //The connection in front, then Import/Export in its place: its content over the graph's card, and no query tab in
        //front; the query tab brings the card back.
        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".split-main > .card > .gdbv-setup-in-card[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".split-main > .card > .gdbv-setup-in-card[data-testid=import-export-dialog]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=workspace-tabs] .nav-link.active"));
        Assert.Empty(cut.FindAll("[data-testid=connection-dialog]"));

        cut.Click("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=import-export-dialog]")), TimeSpan.FromSeconds(5));

        Assert.Single(cut.FindAll("[data-testid=workspace-tabs] .nav-link.active"));
        Assert.Empty(cut.FindAll("[data-testid=viewer-setup-row] .gdbv-viewer-tabs .nav-link.active"));
        Assert.Single(cut.FindAll("[data-testid=viewer-import-export]"));

        //Pressed again, a tab in front stays there, as a tab does; a new query tab comes in front of it.
        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=import-export-dialog]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=import-export-dialog]")), TimeSpan.FromSeconds(5));

        cut.Render(p => p.Add(l => l.Body, builder => builder.AddContent(0, "another page")));

        Assert.Empty(cut.FindAll("[data-testid=viewer-share]"));
    }

    //The query tabs of the connection in front's group, and of the one behind (the multiple-connections spec, task 4).
    private const string FirstGroupsTabs = "[data-testid=viewer-connections] > [data-testid=workspace-tabs]:nth-child(2)";
    private const string SecondGroupsTabs = "[data-testid=viewer-connections] > [data-testid=workspace-tabs]:nth-child(4)";

    //The add-connection button, left of Import/Export (the multiple-connections spec, task 4): a new connection comes in
    //front with its empty form open over the graph's card, after the first one's group, with one query tab of its own.
    [Fact]
    public void TheAddConnectionButton_BringsANewConnectionInFront_WithItsFormOpenAndATabOfItsOwn()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-tabs] [data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connections]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        var groups = cut.Find("[data-testid=viewer-connections]");

        Assert.Equal(["viewer-connection-tab", "workspace-tabs", "viewer-connection-tab", "workspace-tabs"], groups.Children.Select(c => c.GetAttribute("data-testid")));

        var connections = cut.FindAll("[data-testid=viewer-connection]");

        Assert.DoesNotContain("active", connections[0].ClassName);
        Assert.Contains("Offline mode", connections[0].TextContent);
        Assert.Contains("active", connections[1].ClassName);
        Assert.Contains("Disconnected", connections[1].TextContent);
        Assert.Single(cut.FindAll(".split-main > .card > .gdbv-setup-in-card[data-testid=connection-dialog]"));
        Assert.Empty(cut.FindAll("[data-testid=workspace-tabs] .nav-link.active"));

        var theirs = cut.FindAll(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)");

        Assert.Single(theirs);
        Assert.Contains("Query 1", theirs[0].TextContent);
    }

    //A tab of another connection brings that connection in front with it, its state on its tab; its + adds a tab of its
    //own; its × closes its tab and not one of the connection in front; and a double-click renames only the one in front's.
    //Both connections are plain disconnected ones, the second added before there is a workspace: neither draws a graph,
    //whose redraw comes a moment late and stales what a click finds.
    [Fact]
    public void ATabOfAnotherConnection_BringsThatConnectionInFront()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-add-connection]")), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        //The first connection's query tab: it comes in front, and the second's form goes.
        cut.Click(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        Assert.Contains("active", cut.Find(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName);
        Assert.Single(cut.FindAll("[data-testid=workspace-tabs] .nav-link.active"));

        //The second connection's tab: it comes in front with its form open.
        cut.Click("[data-testid=viewer-connections] > [data-testid=viewer-connection-tab]:nth-child(3) [data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.FindAll("[data-testid=viewer-connection]")[1].ClassName), TimeSpan.FromSeconds(5));

        Assert.Single(cut.FindAll("[data-testid=connection-dialog]"));
        Assert.DoesNotContain("active", cut.FindAll("[data-testid=viewer-connection]")[0].ClassName);

        //The first connection's +, from behind: its new tab, in front with it.
        cut.Click(FirstGroupsTabs + " .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").Count), TimeSpan.FromSeconds(5));

        Assert.Single(cut.FindAll(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)"));
        Assert.Contains("active", cut.FindAll(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)")[1].ClassName);

        //A double-click on the second connection's tab, behind, renames nothing.
        cut.DoubleClick(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)");

        Assert.Empty(cut.FindAll("[data-testid=viewer-connections] input"));

        //On the first's, in front, one box, in its place.
        cut.DoubleClick(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=viewer-connections] input")), TimeSpan.FromSeconds(5));

        Assert.Single(cut.FindAll(FirstGroupsTabs + " input"));

        cut.KeyDown(FirstGroupsTabs + " input", "Escape");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=viewer-connections] input")), TimeSpan.FromSeconds(5));

        //The second connection gets a second tab, then the first comes in front and the second's × closes the second's.
        cut.Click(SecondGroupsTabs + " .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)").Count), TimeSpan.FromSeconds(5));
        cut.Click(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName), TimeSpan.FromSeconds(5));

        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        cut.Click(SecondGroupsTabs + " [title='Close tab']");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)")), TimeSpan.FromSeconds(5));

        Assert.Equal(2, cut.FindAll(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").Count);
        Assert.Contains("active", cut.Find(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName);
    }

    //A connection's tab and query tabs by its place in the row, counting from 1 (the multiple-connections spec).
    private static string ConnectionTabOf(int connection)
    {
        return $"[data-testid=viewer-connections] > [data-testid=viewer-connection-tab]:nth-child({2 * connection - 1})";
    }

    private static string QueryTabsOf(int connection)
    {
        return $"[data-testid=viewer-connections] > [data-testid=workspace-tabs]:nth-child({2 * connection})";
    }

    //The page's connections, which only it holds: a test puts a database of its own into one.
    private static List<ViewerConnection> OpenConnections(IRenderedComponent<MainLayout> cut)
    {
        var home = cut.FindComponent<Home>().Instance;

        return (List<ViewerConnection>)typeof(Home).GetField("openConnections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(home);
    }

    //A database that only says whether it was closed.
    private sealed class ClosableDb : IGraphDb
    {
        public bool Closed { get; private set; }

        public Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GraphDbResult.Failure("Not a database."));
        }

        public ValueTask DisposeAsync()
        {
            Closed = true;

            return ValueTask.CompletedTask;
        }
    }

    //Closing a connection (the multiple-connections spec, task 5): the × is on each connection's tab while there are
    //several, and none on the last; it asks first, naming the connection and its query tabs, then takes them and closes
    //its live link. The one in front stays in front when another goes; when the one in front goes, so does its form, and
    //the one after it comes in front, or the one before when it was the last.
    [Fact]
    public void ClosingAConnection_AsksThenTakesItsTabsAndItsLiveLink()
    {
        var storage = new InMemoryStorage();
        var cut = RenderHome(storage);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-tabs] [data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connections]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=viewer-close-connection]"));

        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        Assert.Equal(3, cut.FindAll("[data-testid=viewer-close-connection]").Count);

        //The second one in front with two query tabs and its form open, and a database of its own.
        cut.Click(QueryTabsOf(2) + " .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)").Count), TimeSpan.FromSeconds(5));
        cut.Click(ConnectionTabOf(2) + " [data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        var database = new ClosableDb();

        OpenConnections(cut)[1].Db = database;

        //Asked and declined: nothing goes.
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);

        confirm.SetResult(false);
        cut.Click(ConnectionTabOf(2) + " [data-testid=viewer-close-connection]");
        cut.WaitForAssertion(() => Assert.Single(confirm.Invocations), TimeSpan.FromSeconds(5));

        Assert.Equal("Close connection No host:443 and its 2 query tabs? Their results will be discarded.", confirm.Invocations.First().Arguments[0]);
        Assert.Equal(3, cut.FindAll("[data-testid=viewer-connection]").Count);
        Assert.Single(cut.FindAll("[data-testid=connection-dialog]"));
        Assert.False(database.Closed);

        //Agreed: it goes with its tabs, its form and its link, and the one after it comes in front.
        confirm.SetResult(true);
        cut.Click(ConnectionTabOf(2) + " [data-testid=viewer-close-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        Assert.True(database.Closed);
        Assert.Empty(cut.FindAll("[data-testid=connection-dialog]"));
        Assert.Single(cut.FindAll(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)"));
        Assert.Contains("active", cut.Find(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)").ClassName);

        //One before the one in front goes: the one in front stays, its form still open.
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));
        cut.Click(ConnectionTabOf(2) + " [data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(ConnectionTabOf(2) + " [data-testid=viewer-connection]").ClassName), TimeSpan.FromSeconds(5));
        cut.Click(ConnectionTabOf(1) + " [data-testid=viewer-close-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        Assert.Equal("Close connection No host:443 and its query tab? Their results will be discarded.", confirm.Invocations.Last().Arguments[0]);
        Assert.Contains("active", cut.Find(ConnectionTabOf(1) + " [data-testid=viewer-connection]").ClassName);
        Assert.Single(cut.FindAll("[data-testid=connection-dialog]"));

        //The last one, in front, goes: the one before it comes in front, and alone it has no ×.
        cut.Click(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)").ClassName), TimeSpan.FromSeconds(5));
        cut.Click(ConnectionTabOf(2) + " [data-testid=viewer-close-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=viewer-connection]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=viewer-close-connection]"));

        //Saved so: a reload brings back the one left, and its tab's text alone.
        Assert.Equal(1, JsonDocument.Parse(storage.GetStringAsync("graphdbviewer:openConnections").Result).RootElement.GetArrayLength());
        Assert.Equal(1, JsonDocument.Parse(storage.GetStringAsync("graphdbviewer:tabText").Result).RootElement.GetArrayLength());
    }

    //Leaving the page closes every connection's live link, not only the one in front's (the multiple-connections spec).
    [Fact]
    public void LeavingThePage_ClosesEveryConnection()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-add-connection]")), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        var behind = new ClosableDb();
        var front = new ClosableDb();

        OpenConnections(cut)[0].Db = behind;
        OpenConnections(cut)[1].Db = front;

        cut.Render(p => p.Add(l => l.Body, builder => builder.AddContent(0, "another page")));

        cut.WaitForAssertion(() => Assert.True(behind.Closed && front.Closed), TimeSpan.FromSeconds(5));
    }

    //While a query runs on one connection, another comes in front and runs its own (the multiple-connections spec, story 4,
    //2026-11-07): the first's answer lands in its own tab, not in the one on the page when it comes. A SPARQL endpoint holds
    //the query here.
    [Fact]
    public void ARunningQuery_LetsAnotherConnectionComeInFront_AndAnswersIntoItsOwnTab()
    {
        const string answer = @"{""head"":{""vars"":[""s""]},""results"":{""bindings"":[{""s"":{""type"":""uri"",""value"":""https://example.test/a""}}]}}";

        var held = new TaskCompletionSource<HttpResponseMessage>();
        var holding = false;

        HttpResponseMessage Answer()
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/sparql-results+json")) };
        }

        Task<HttpResponseMessage> Endpoint(HttpRequestMessage request)
        {
            if (request.RequestUri.AbsolutePath != "/ds/sparql")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            if (holding)
                return held.Task;

            return Task.FromResult(Answer());
        }

        var query = Uri.EscapeDataString("SELECT ?s WHERE { ?s ?p ?o }");
        var endpoint = Uri.EscapeDataString("https://sparql.test/ds/sparql");
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string>(), Endpoint));

        var cut = RenderHome(http: http, url: $"http://localhost/?dbType=Sparql&endpoint={endpoint}&viaServer=false&query={query}&run=false");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connection] .bi-plug-fill")), TimeSpan.FromSeconds(10));

        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));
        cut.Click(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        holding = true;
        cut.Click("button[title^='Run Query']");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Stop']")), TimeSpan.FromSeconds(5));

        var first = OpenConnections(cut)[0];
        var second = OpenConnections(cut)[1];

        //Running: the second connection's query tab comes in front, its Run its own (nothing of the first's holds it).
        cut.Click(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(SecondGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("button[title^='Stop']"));
        Assert.Empty(cut.FindAll("button[title^='Another query tab']"));

        //A new connection can be added meanwhile, and goes again.
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        cut.Click(ConnectionTabOf(3) + " [data-testid=viewer-close-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=viewer-connection]").Count), TimeSpan.FromSeconds(5));

        //Ended: the answer is the first connection's tab's, and the second's tab has none of it.
        held.SetResult(Answer());
        cut.WaitForAssertion(() => Assert.Null(first.RunningTab), TimeSpan.FromSeconds(5));

        Assert.NotNull(first.Tabs[0].TableResult);
        Assert.Null(first.Tabs[0].QueryCts);
        Assert.Null(second.Tabs[0].TableResult);
        Assert.Null(second.Tabs[0].QueryResults);

        //Back on the first: Run, and its answer.
        cut.Click(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find(FirstGroupsTabs + " .nav-link:not(.gdbv-ws-add)").ClassName), TimeSpan.FromSeconds(5));

        Assert.NotEmpty(cut.FindAll("button[title^='Run Query']"));
    }

    //In the graph's card the connection and Import/Export have no × and Import/Export no title bar: their tab says which
    //is in front and a query tab closes them (2026-11-03, the user). Before there is a workspace, with no query tab to go
    //back to, the × is how back to the page's invitation, and it stays.
    [Fact]
    public void WithNoPageTabs_TheViewersControlsInTheGraphsCardHaveNoTitleBarOrClose()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-tabs] [data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=import-export-dialog] .btn-close")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=connection-dialog] .btn-close")), TimeSpan.FromSeconds(5));

        EnterOfflineMode(cut);

        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card[data-testid=import-export-dialog]")), TimeSpan.FromSeconds(5));

        var importExport = cut.Find(".gdbv-setup-in-card[data-testid=import-export-dialog]");

        Assert.Empty(importExport.QuerySelectorAll(".btn-close"));
        Assert.DoesNotContain("Import/Export", importExport.TextContent);
        Assert.NotEmpty(importExport.QuerySelectorAll("textarea[placeholder^='GraphSON']"));

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        var connection = cut.Find(".gdbv-setup-in-card[data-testid=connection-dialog]");

        Assert.Empty(connection.QuerySelectorAll(".btn-close"));
        Assert.NotEmpty(connection.QuerySelectorAll("[data-testid=connection-picker]"));
    }

    //In the graph's card Import/Export is on the card's own color, not an inset's, and is two sections, Import above Export,
    //each heading with a line under it and no rule between them; Import's two columns wrap in a narrow card (2026-11-03,
    //the user). Outside the card, before there is a workspace, it keeps its inset body and its rule between the two.
    [Fact]
    public void WithNoPageTabs_ImportExportInTheGraphsCardIsTwoSectionsOnTheCardsColor()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=import-export-body]")), TimeSpan.FromSeconds(5));

        var outside = cut.Find("[data-testid=import-export-body]");

        Assert.Contains("gdbv-tint-panel", outside.ClassName);
        Assert.Single(outside.QuerySelectorAll(".vr"));
        Assert.Empty(outside.QuerySelectorAll("hr"));

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        EnterOfflineMode(cut);

        cut.Click("[data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card [data-testid=import-export-body]")), TimeSpan.FromSeconds(5));

        var body = cut.Find(".gdbv-setup-in-card [data-testid=import-export-body]");
        var lines = body.QuerySelectorAll("hr");

        Assert.DoesNotContain("gdbv-tint-panel", body.ClassName);
        Assert.Empty(body.QuerySelectorAll(".vr"));
        Assert.Contains("flex-column", body.FirstElementChild.ClassName);
        Assert.Equal(["Import", "Export"], lines.Select(hr => hr.PreviousElementSibling.TextContent.Trim()));
        Assert.Contains("flex-wrap", lines[0].NextElementSibling.ClassName);
    }

    //In the graph's card the connection's form is plain sections, not a dialog's tinted panel, with short lines under its
    //strip and over its buttons in place of the strips' own full-width borders (2026-11-03, the user).
    [Fact]
    public void TheConnectionInTheCard_IsPlainSections_WithShortLines()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connection]")), TimeSpan.FromSeconds(5));
        OpenConnectionDialog(cut);
        EnterOfflineMode(cut);
        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card [data-testid=connection-panel]")), TimeSpan.FromSeconds(5));

        var panel = cut.Find("[data-testid=connection-panel]");
        var rules = panel.QuerySelectorAll(":scope > [data-testid=connection-rule]");

        Assert.Contains("gdbv-conn-sections", panel.ClassList);
        Assert.DoesNotContain("gdbv-tint", panel.ClassList);
        Assert.Empty(panel.QuerySelectorAll(".gdbv-tint-panel"));
        Assert.Equal(2, rules.Length);
        //The strip with the title, the line, then the picker under it (2026-11-04, the user). Read from the lines alone: the
        //panel Find gave stands for the latest render, the lines for the one they were taken from, and the page still
        //renders now and then, so their nodes are not always the same objects.
        Assert.Null(rules[0].PreviousElementSibling.PreviousElementSibling);
        Assert.Equal("connection-picker-row", rules[0].NextElementSibling.GetAttribute("data-testid"));
        Assert.Null(rules[1].NextElementSibling.NextElementSibling);
        Assert.DoesNotContain("border-bottom", rules[0].PreviousElementSibling.ClassList);
        Assert.DoesNotContain("border-top", rules[1].NextElementSibling.ClassList);
    }

    //What Import and Export do is each heading's tooltip, and there is no bar below them (2026-11-03, the user): it held
    //one line for both, then small type beside each heading. Outside the card and in it.
    [Fact]
    public void ImportAndExport_SayWhatTheyDoInTheirHeadingsTooltips_WithNoBarBelow()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=import-export-body]")), TimeSpan.FromSeconds(5));

        void AssertSaid()
        {
            var body = cut.Find("[data-testid=import-export-body]");

            var import = body.QuerySelector("[data-testid=import-heading]");
            var export = body.QuerySelector("[data-testid=export-heading]");

            Assert.Equal("Imports draw onto the canvas", import.GetAttribute("title"));
            Assert.Equal("Exports use the graph loaded now", export.GetAttribute("title"));
            Assert.Equal("Import", import.TextContent.Trim());
            Assert.Equal("Export", export.TextContent.Trim());
            Assert.Null(body.NextElementSibling);
        }

        AssertSaid();

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);
        cut.Click("[data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card [data-testid=import-export-body]")), TimeSpan.FromSeconds(5));

        AssertSaid();
    }

    //A host with plugins keeps them in the workspace's row, under the bar's page tabs, and leaves the bar alone.
    [Fact]
    public void WithPlugins_TheViewersControlsStandInTheWorkspacesRow()
    {
        var cut = RenderPlatformHome(NoGraph, []);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-setup-row] [data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=shell-contribution]"));
    }

    //An embedded viewer, which has no bar of its own, puts nothing in the shell's.
    [Fact]
    public void Embedded_PutsNothingInTheBar()
    {
        var cut = RenderHome(body: EmbeddedBody);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=embed-empty]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=shell-contribution]"));
    }

    //A graph shown embedded has no tabs, which are a workspace's, and none of the tools that edit a graph, since an
    //embedded viewer reads. Style, which changes only how the graph is drawn, stays, and says the toolbar is there.
    [Fact]
    public void Embedded_AGraphShown_HasNoTabsAndNoToolThatEditsIt()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D("embed"), body: EmbeddedBody);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        Assert.Empty(cut.FindAll("[data-testid=workspace-tabs]"));
        Assert.Empty(cut.FindAll("button[title^='Add component mode']"));
        Assert.Empty(cut.FindAll("button[title^='Delete mode']"));
        Assert.Empty(cut.FindAll("button[title^='Link nodes mode']"));
    }

    //The same graph outside /embed, with its tabs and the three tools that edit it.
    [Fact]
    public void NotEmbedded_AGraphShown_HasTheTabsAndTheToolsThatEditIt()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        Assert.NotEmpty(cut.FindAll("[data-testid=workspace-tabs]"));
        Assert.NotEmpty(cut.FindAll("button[title^='Add component mode']"));
        Assert.NotEmpty(cut.FindAll("button[title^='Delete mode']"));
        Assert.NotEmpty(cut.FindAll("button[title^='Link nodes mode']"));
    }

    //Zoom out, zoom in and re-center are the bold "−", "+" and "⛶" the user knew before other icons replaced them on
    //2026-10-29, drawn to their measure rather than typed so they sit in the buttons' middle (2026-11-02, the user): a
    //bar, two bars and four corners, and no icon font's glyph.
    [Fact]
    public void TheZoomAndRecenterButtons_AreTheBoldCharactersDrawn()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title='Zoom out']")), TimeSpan.FromSeconds(10));

        foreach (var (title, bars, corners) in new[] { ("Zoom out", 1, 0), ("Zoom in", 2, 0), ("Re-center graph", 0, 1) })
        {
            var button = cut.Find($"button[title='{title}']");
            var glyph = button.QuerySelector("svg.gdbv-glyph");

            Assert.NotNull(glyph);
            Assert.Equal(bars, glyph.QuerySelectorAll("rect").Length);
            Assert.Equal(corners, glyph.QuerySelectorAll("path").Length);
            Assert.Empty(button.QuerySelectorAll("i"));
            Assert.Empty(button.TextContent.Trim());
        }
    }

    //The Style dialog's database clean-up stages writes to the database, so it stands in the database scope, beside
    //This node: database, and not in the browser scope, where nothing else writes. Until 2026-10-31 it showed in both.
    [Fact]
    public void TheStyleDialogsDatabaseCleanup_IsInTheDatabaseScopeOnly()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();

        cut.Click("button[title^='Style:']");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "This node: database"), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=style-database-cleanup]"));

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "This node: database"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=style-database-cleanup]")), TimeSpan.FromSeconds(5));
        Assert.Contains("Remove all viewer properties from the database", cut.Find("[data-testid=style-database-cleanup]").TextContent);

        //Beside Applies to rather than on a line of its own (2026-11-06, the user), and the vertex's style and its position each
        //in a card, the style's table without the expand column that only the labels' rows use.
        Assert.DoesNotContain("w-100", cut.Find("[data-testid=style-database-cleanup]").ClassName ?? "");
        Assert.Equal("Style", cut.Find("[data-testid=style-node-card] > .card-header").TextContent.Trim());
        Assert.Equal("Position", cut.Find("[data-testid=style-position-card] > .card-header").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=style-vertices] .gdbv-style-expand-col"));

        //Each shape with its picture after it on a line, the 2D over the 3D (2026-11-06, the user).
        Assert.Equal(["2D shape", "Icon URL"], cut.FindAll("[data-testid=style-look-2d] label").Select(l => l.TextContent));
        Assert.Equal(["3D shape", "3D model URL"], cut.FindAll("[data-testid=style-look-3d] label").Select(l => l.TextContent));
    }

    //Styling is in tabs, Vertices first, then Edges and Map & Globe (2026-11-04, the user's pick of three layouts). A
    //vertex label's row holds its color, size and displayed property, and opens on its shape, icon and 3D model.
    [Fact]
    public void Styling_IsInTabs_AndALabelsRowOpensOnItsShapeIconAndModel()
    {
        var cut = OpenStyling();

        Assert.Equal(["Vertices", "Edges", "Map & Globe"], cut.FindAll(".gdbv-style-tabs .nav-link").Select(t => t.TextContent.Trim()));
        Assert.Contains("active", cut.Find("[data-testid=style-tab-vertices]").ClassList);

        var row = cut.Find("[data-testid=style-row][data-label=Part]");

        Assert.NotNull(row.QuerySelector("input[title='Node color']"));
        Assert.NotNull(row.QuerySelector("input[title^='Node size']"));
        Assert.NotNull(row.QuerySelector("input[title^='Property to show']"));
        Assert.Empty(cut.FindAll("[data-testid=style-row-more]"));
        Assert.Empty(cut.FindAll("input[title^='Image URL']"));

        cut.Click("[data-testid=style-row][data-label=Part] [data-testid=style-row-toggle]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=style-row-more][data-label=Part]")), TimeSpan.FromSeconds(5));

        var more = cut.Find("[data-testid=style-row-more][data-label=Part]");

        Assert.NotNull(more.QuerySelector("select[title^='Node shape on the 2D canvas']"));
        Assert.NotNull(more.QuerySelector("select[title^='The 3D solid']"));
        Assert.NotNull(more.QuerySelector("input[title^='Image URL']"));
        Assert.NotNull(more.QuerySelector("input[title^='3D model']"));

        cut.Click("[data-testid=style-row-toggle]");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=style-row-more]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=style-tab-map]");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find("[data-testid=style-tab-map]").ClassList), TimeSpan.FromSeconds(5));

        Assert.Single(cut.FindAll("[data-testid=coordinate-mapping] tbody tr"));
        Assert.Empty(cut.FindAll("[data-testid=style-vertices]"));

        cut.Click("[data-testid=style-tab-edges]");
        cut.WaitForAssertion(() => Assert.Contains("Edge colors", cut.Find(".gdbv-style-window").TextContent), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=coordinate-mapping]"));
    }

    //Show / Hide properties is the 2D vertex's +/- button as a menu row (2026-11-06, the user): its words follow whether the
    //properties are shown, and it flips them on the canvas. A vertex without the button, and an edge, have no row.
    [Fact]
    public void ShowOrHideProperties_IsTheVertexsPlusButton_InItsMenu()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-offline], [data-testid=connection-offline]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);

        var home = cut.FindComponent<Home>();

        RightClick(cut, home, "node", "4096", "M3 Screw", props: "hidden");

        Assert.Equal("Show properties", cut.Find("[data-testid=element-menu-props]").TextContent.Trim());

        cut.Click("[data-testid=element-menu-props]");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=element-menu]")), TimeSpan.FromSeconds(5));
        Assert.Contains(JSInterop.Invocations["toggleCytoscapeNodeProps"], i => Equals(i.Arguments[0], "4096"));

        RightClick(cut, home, "node", "4096", "M3 Screw", props: "shown");

        Assert.Equal("Hide properties", cut.Find("[data-testid=element-menu-props]").TextContent.Trim());

        RightClick(cut, home, "node", "4097", "Bare");

        Assert.Empty(cut.FindAll("[data-testid=element-menu-props]"));
    }

    //Style from an edge's menu starts on the Edges tab, where an edge is styled, by its label.
    [Fact]
    public void StyleFromAnEdgesMenu_StartsOnTheEdgesTab()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-offline], [data-testid=connection-offline]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);

        var home = cut.FindComponent<Home>();

        RightClick(cut, home, "edge", "e1", "knows", """{"type":"edge","id":"e1","label":"knows","properties":{}}""");
        cut.Click("[data-testid=element-menu-style]");

        cut.WaitForAssertion(() => Assert.Contains("active", cut.Find("[data-testid=style-tab-edges]").ClassList), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("active", cut.Find("[data-testid=style-tab-vertices]").ClassList);
    }

    //Style on one vertex, in the database scope, has no tabs, which are the browser's label styles: its one row stands
    //open, with nothing to open it by, and its position under it.
    [Fact]
    public void StylingOneVertexInTheDatabase_HasNoTabs_AndItsRowStandsOpen()
    {
        var cut = OpenStyling(selectPart: true);

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "This node: database"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".gdbv-style-tabs")), TimeSpan.FromSeconds(5));
        Assert.Single(cut.FindAll("[data-testid=style-row]"));
        Assert.Single(cut.FindAll("[data-testid=style-row-more]"));
        Assert.Empty(cut.FindAll("[data-testid=style-row-toggle]"));
        Assert.Contains("Position", cut.Find(".gdbv-style-window").TextContent);
    }

    //Detailed view, first Show details on canvas (2026-11-06, the user): a switch per vertex label in Styling that draws its
    //vertices as small cards on the 2D canvas, told to the canvas where they stand; for one vertex in the database it stages
    //gdbvDetails instead. Hovering its name or its switch says what it draws and does.
    [Fact]
    public void DetailedView_IsALabelsSwitch_AndOneVertexsProperty()
    {
        var cut = OpenStyling(selectPart: true);
        var header = cut.Find("[data-testid=style-vertices] thead .gdbv-style-details-col");

        Assert.Equal("Detailed view", header.TextContent.Trim());
        Assert.Contains("drawn as a small card instead of its box", header.GetAttribute("title"));
        Assert.Equal(header.GetAttribute("title"), cut.Find("[data-testid=style-row][data-label=Part] [data-testid=style-details]").GetAttribute("title"));

        var box = cut.Find("[data-testid=style-row][data-label=Part] [data-testid=style-details]");

        Assert.False(box.HasAttribute("checked"));

        //Raised through the cut, which finds the box in the markup as it stands (RenderedEvents): the selection's render
        //is still queued when the test gets here, and a change raised on the box it found before that render fails
        //with UnknownEventHandlerIdException when its turn comes, once in the full run of 2026-11-07.
        cut.Change("[data-testid=style-row][data-label=Part] [data-testid=style-details]", true);

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations["setCytoscapeLabelDetails"], i => Equals(i.Arguments[0], "Part") && Equals(i.Arguments[1], true)), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=style-row][data-label=Part] [data-testid=style-details]").HasAttribute("checked")), TimeSpan.FromSeconds(5));

        //With no 2D canvas to tell, the graph was drawn again, which lets the selection go: the Part is chosen again.
        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "This node: database"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".gdbv-style-tabs")), TimeSpan.FromSeconds(5));

        //The label's box is ticked, so unticking it for this one vertex says so in its own property.
        Assert.True(cut.Find("[data-testid=style-details]").HasAttribute("checked"));

        cut.Change("[data-testid=style-details]", false);

        cut.WaitForAssertion(() => Assert.Contains("gdbvDetails", GeneratedText(home.Instance)), TimeSpan.FromSeconds(5));
        Assert.Contains("false", GeneratedText(home.Instance));
    }

    //Two Parts and a "uses" edge between them, as a Gremlin server answers.
    private const string EdgeAnswer = """
        {"requestId":"6a8d3b0f-1e8f-4e43-8b64-2c1f7a4c9d22","status":{"message":"","code":200,"attributes":{"@type":"g:Map","@value":[]}},
         "result":{"data":{"@type":"g:List","@value":[
           {"@type":"g:Vertex","@value":{"id":{"@type":"g:Int64","@value":1},"label":"Part"}},
           {"@type":"g:Vertex","@value":{"id":{"@type":"g:Int64","@value":2},"label":"Part"}},
           {"@type":"g:Edge","@value":{"id":{"@type":"g:Int64","@value":7},"label":"uses","inVLabel":"Part","outVLabel":"Part","inV":{"@type":"g:Int64","@value":2},"outV":{"@type":"g:Int64","@value":1}}}
         ]},"meta":{"@type":"g:Map","@value":[]}}}
        """;

    //Detailed view on edges (2026-11-07, the user's "do the loose ends"): a switch per edge label in Styling's edge table,
    //kept with the tab, and the graph drawn again with that label's edges marked for their cards. Its name and its
    //switch say what it draws.
    [Fact]
    public void DetailedViewOnEdges_IsAnEdgeLabelsSwitch_KeptWithTheTab()
    {
        var storage = new InMemoryStorage();
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = EdgeAnswer }));
        var cut = RenderHome(storage, http, GraphIn2D(""));
        const string toggle = "[data-testid=style-edges] tr[data-label=uses] [data-testid=style-edge-details]";

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));
        cut.Click("button[title^='Style:']");
        cut.Click("[data-testid=style-tab-edges]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(toggle)), TimeSpan.FromSeconds(5));

        var header = cut.Find("[data-testid=style-edges] thead .gdbv-style-details-col");

        Assert.Equal("Detailed view", header.TextContent.Trim());
        Assert.Contains("small card at its middle", header.GetAttribute("title"));
        Assert.Equal(header.GetAttribute("title"), cut.Find(toggle).GetAttribute("title"));
        Assert.False(cut.Find(toggle).HasAttribute("checked"));

        var drawn = JSInterop.Invocations["drawCytoscapeGraph"].Count;

        cut.Change(toggle, true);

        cut.WaitForAssertion(() => Assert.True(JSInterop.Invocations["drawCytoscapeGraph"].Count > drawn), TimeSpan.FromSeconds(5));
        Assert.Contains("\"details\":true", (string)JSInterop.Invocations["drawCytoscapeGraph"][^1].Arguments[0]);
        cut.WaitForAssertion(() => Assert.True(cut.Find(toggle).HasAttribute("checked")), TimeSpan.FromSeconds(5));

        //Kept with the tab, as its edge colors are.
        List<string> SavedEdgeDetails()
        {
            var saved = new WorkspaceStore(storage).LoadConnectionsAsync().GetAwaiter().GetResult();

            return Assert.Single(Assert.Single(saved.Connections).Tabs).EdgeDetails;
        }

        cut.WaitForAssertion(() => Assert.Equal(["uses"], SavedEdgeDetails()), TimeSpan.FromSeconds(5));

        drawn = JSInterop.Invocations["drawCytoscapeGraph"].Count;
        cut.Change(toggle, false);

        cut.WaitForAssertion(() => Assert.True(JSInterop.Invocations["drawCytoscapeGraph"].Count > drawn), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("\"details\":true", (string)JSInterop.Invocations["drawCytoscapeGraph"][^1].Arguments[0]);
        cut.WaitForAssertion(() => Assert.Empty(SavedEdgeDetails()), TimeSpan.FromSeconds(5));
    }

    //The stylesheets are a menu at the top right: the current styling is saved as one at its foot, choosing one applies
    //it and shuts the menu, and its trash deletes it.
    [Fact]
    public void TheStylesheetsMenu_SavesAppliesAndDeletes()
    {
        var cut = OpenStyling();

        Assert.Empty(cut.FindAll("[data-testid=style-sheets-menu]"));

        cut.Click("[data-testid=style-sheets-toggle]");

        Assert.Contains("None yet", cut.Find("[data-testid=style-sheets-menu]").TextContent);

        cut.Input("[data-testid=style-sheets-name]", "Factory floor");
        cut.Click("[data-testid=style-sheets-save]");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=style-sheets-menu] [data-key='Factory floor']")), TimeSpan.FromSeconds(5));
        Assert.Equal("", cut.Find("[data-testid=style-sheets-name]").GetAttribute("value") ?? "");

        //Applying draws the graph again, and the page renders meanwhile.
        cut.Click("[data-testid=style-sheets-menu] [data-key='Factory floor']");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=style-sheets-menu]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=style-sheets-toggle]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=style-sheets-menu] [data-key='Factory floor']")), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=style-sheets-menu] [data-key='Factory floor'] [data-act=delete]");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=style-sheets-menu] [data-key]")), TimeSpan.FromSeconds(5));

        //Enter in the name box saves as its Save does.
        cut.Input("[data-testid=style-sheets-name]", "Night shift");
        cut.KeyDown("[data-testid=style-sheets-name]", "Enter");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=style-sheets-menu] [data-key='Night shift']")), TimeSpan.FromSeconds(5));
    }

    //Opens Styling from the toolbar on a graph of one Part, with that Part selected when asked.
    private IRenderedComponent<MainLayout> OpenStyling(bool selectPart = false)
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        if (selectPart)
        {
            var home = cut.FindComponent<Home>();

            cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();
        }

        cut.Click("button[title^='Style:']");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-style-window")), TimeSpan.FromSeconds(5));

        return cut;
    }

    //The element panel's add and remove buttons are the outlined ones beside the saved lists, a green + and a red − on the
    //field's color, where they were filled green and red (2026-11-03, the user).
    [Fact]
    public void TheElementPanelsAddAndRemove_AreOutlinedAsTheSavedListsAre()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();

        foreach (var (selector, look) in new[] { ("button[title='Remove property']", "gdbv-pick-remove"), ("[data-testid=add-property]", "gdbv-pick-add"), ("button[title='Add a new connection']", "gdbv-pick-add") })
        {
            var button = cut.Find(selector);

            Assert.Contains(look, button.ClassList);
            Assert.DoesNotContain("btn-success", button.ClassList);
            Assert.DoesNotContain("btn-danger", button.ClassList);
        }
    }

    //A vertex's own style, gdbvShape and the like under Additional Viewer properties, is a row as a property's is, its name
    //in front of its value and its remove at the end (2026-11-06, the user: its name had stood over its box).
    [Fact]
    public void AViewerStyleProperty_IsARowAsAPropertysIs()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw", "gdbvShape": "oval", "gdbvShape3d": "cube"}}""")).Wait();

        var rows = cut.FindAll("[data-testid=element-style-prop]");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["gdbvShape", "gdbvShape3d"], rows.Select(r => r.QuerySelector(".input-group-text").TextContent));
        Assert.Equal(["oval", "cube"], rows.Select(r => r.QuerySelector("input").GetAttribute("value")));

        foreach (var row in rows)
        {
            Assert.Contains("input-group", row.ClassList);
            Assert.Equal(["SPAN", "INPUT", "BUTTON"], row.Children.Select(c => c.TagName));
            Assert.Empty(row.QuerySelectorAll("label"));
        }
    }

    //A new property's name, value and + are one control, as each property's row is, set apart from them; and the new
    //component's label box and Create are one too (2026-11-04, the user).
    [Fact]
    public void ANewPropertyAndANewComponent_AreEachOneControl()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();

        var property = cut.Find("[data-testid=new-property]");

        Assert.Contains("input-group", property.ClassList);
        Assert.Contains("mt-3", property.ClassList);

        //The card in parts, each a card of its own (2026-11-05, the user): the label and properties, the viewer's
        //properties, In, Out and Add connection.
        var sections = cut.FindAll("[data-testid=element-section]");

        Assert.Equal(5, sections.Count);
        Assert.NotNull(sections[0].QuerySelector("[data-testid=new-property]"));

        //Its label a row as a property's is, the name in front (2026-11-05, the user).
        var caption = sections[0].FirstElementChild;

        Assert.Equal("element-caption", caption.GetAttribute("data-testid"));
        Assert.Contains("input-group", caption.ClassList);
        Assert.Equal("Label", caption.QuerySelector(".input-group-text").TextContent);
        Assert.True(caption.QuerySelector("input").HasAttribute("disabled"));
        Assert.Equal("DETAILS", sections[1].TagName);
        Assert.Equal("In", sections[2].QuerySelector(".gdbv-conn-head").TextContent);
        Assert.Equal("Out", sections[3].QuerySelector(".gdbv-conn-head").TextContent);
        Assert.Contains("Add connection", sections[4].TextContent);
        Assert.Equal(["INPUT", "INPUT", "BUTTON"], property.Children.Select(c => c.TagName));
        Assert.Equal("add-property", property.LastElementChild.GetAttribute("data-testid"));

        var component = cut.Find("[data-testid=new-component-group]");

        Assert.Contains("input-group", component.ClassList);
        Assert.Equal("e.g. person", component.FirstElementChild.GetAttribute("placeholder"));
        Assert.Contains("Create Component", component.LastElementChild.TextContent);
        Assert.Contains("rounded-start-0", component.LastElementChild.QuerySelector("button").ClassList);
    }

    //The selected element's card stands under a line of its own (2026-11-03, the user), with the room under the line
    //that the new component has over its own and none over it, the query pane's foot being that (2026-11-04).
    [Fact]
    public void TheSelectedElementsCardStandsUnderALine()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=element-rule]"));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();

        var rule = cut.Find("[data-testid=element-rule]");

        Assert.Equal("element-card", rule.NextElementSibling.GetAttribute("data-testid"));
        Assert.Contains("mt-0", rule.ClassList);
        Assert.Contains("mb-4", rule.ClassList);

        //Examples and History fill the pane with a box down to its foot, so there the line keeps room over it.
        //The selection's connections are still being looked up and drawn, so a tab found a moment ago may be one render
        //old: each click is awaited, and found again when its handler has gone.
        foreach (var tab in new[] { "Examples", "History" })
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    cut.FindAll("[data-tour=query-tabs] .nav-link").First(l => l.TextContent.Contains(tab)).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()).GetAwaiter().GetResult();

                    break;
                }
                catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }

            cut.WaitForAssertion(() => Assert.Contains("mt-3", cut.Find("[data-testid=element-rule]").ClassList), TimeSpan.FromSeconds(5));
        }
    }

    //In the graph's card, Connections and Import/Export each start with room under the tab strip over them, the same
    //for both (2026-11-04, the user: the saved-connection picker stood right under the tabs).
    [Fact]
    public void InTheGraphsCard_ConnectionsAndImportExportStartWithRoomUnderTheTabs()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-offline], [data-testid=connection-offline]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);

        cut.Click("[data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card [data-testid=connection-strip]")), TimeSpan.FromSeconds(5));
        Assert.Contains("pt-4", cut.Find("[data-testid=connection-strip]").ClassList);

        cut.Click("[data-testid=viewer-tabs] [data-testid=viewer-import-export]");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".gdbv-setup-in-card [data-testid=import-export-body]")), TimeSpan.FromSeconds(5));
        Assert.Contains("pt-4", cut.Find("[data-testid=import-export-body]").ClassList);
    }

    //A right-click on a vertex opens its menu at the pointer (2026-11-04, the user): Expand loads its neighbors as a
    //double-click does, and Delete stages its drop on the Generated tab as the card's X does; either closes the menu.
    [Fact]
    public void ARightClickedVertex_OffersExpandAndDelete()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["drawCytoscapeGraph"]), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        RightClick(cut, home, "node", "4096", "M3 Screw");

        var menu = cut.Find("[data-testid=element-menu]");

        Assert.Contains("left: 120.5px; top: 64px", menu.GetAttribute("style"));
        Assert.Contains("M3 Screw", menu.TextContent);
        Assert.False(cut.Find("[data-testid=element-menu-expand]").HasAttribute("disabled"));
        Assert.Equal("Explore", cut.Find("[data-testid=element-menu-expand]").TextContent.Trim());

        cut.Click("[data-testid=element-menu-expand]");

        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["addToCytoscapeGraph"]), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=element-menu]"));

        RightClick(cut, home, "node", "4096", "M3 Screw");
        cut.Click("[data-testid=element-menu-delete]");

        cut.WaitForAssertion(() => Assert.Contains("4096", GeneratedText(home.Instance)), TimeSpan.FromSeconds(5));
        Assert.Contains("drop()", GeneratedText(home.Instance));
        Assert.Empty(cut.FindAll("[data-testid=element-menu]"));
    }

    //An edge's menu has Style and Delete and no Expand, since only a vertex has neighbors to load; offline a vertex's
    //Expand stands disabled, and a press anywhere else closes the menu without doing anything.
    [Fact]
    public void ARightClickedEdge_OffersNoExpand_AndOfflineAVertexCannotExpand()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-offline], [data-testid=connection-offline]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);

        var home = cut.FindComponent<Home>();

        RightClick(cut, home, "edge", "e1", "composes");

        Assert.Empty(cut.FindAll("[data-testid=element-menu-expand]"));
        Assert.Single(cut.FindAll("[data-testid=element-menu-style]"));
        Assert.Single(cut.FindAll("[data-testid=element-menu-delete]"));

        cut.MouseDown(".gdbv-element-menu-sheet");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=element-menu]")), TimeSpan.FromSeconds(5));
        Assert.True(string.IsNullOrEmpty(GeneratedText(home.Instance)));

        RightClick(cut, home, "node", "7", "Leg 1");

        Assert.True(cut.Find("[data-testid=element-menu-expand]").HasAttribute("disabled"));
    }

    //Style opens the Style dialog on the right-clicked vertex, as its card's palette does (2026-11-04, the user): it is
    //selected first, its card showing, and the dialog is on its label, scoped to it where it can be edited.
    [Fact]
    public void ARightClickedVertexsStyle_SelectsIt_AndOpensStyleOnIt()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-offline], [data-testid=connection-offline]")), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);

        var home = cut.FindComponent<Home>();

        RightClick(cut, home, "node", "7", "Leg 1", """{"type":"node","id":"7","label":"Leg 1","glabel":"Component","properties":{"name":"Leg 1"}}""");
        cut.Click("[data-testid=element-menu-style]");

        cut.WaitForAssertion(() => Assert.Contains("All Component nodes", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=element-menu]"));
        Assert.Contains(cut.FindAll("input"), i => i.GetAttribute("value") == "Leg 1");

        //Its label's row stands open on its shape, icon and 3D model in the browser's scope too (2026-11-04, Styling in tabs).
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "All Component nodes: this browser"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=style-row-more][data-label=Component]")), TimeSpan.FromSeconds(5));
        Assert.Single(cut.FindAll("[data-testid=style-row-toggle]"));
    }

    //A label's 2D shape changes its nodes on the canvas where they stand, rather than drawing the graph anew, which laid it
    //out again (2026-11-05, the user).
    [Fact]
    public void ALabels2dShape_ChangesItsNodesInPlace()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        JSInterop.Setup<bool>("setCytoscapeLabelShape", _ => true).SetResult(true);
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["drawCytoscapeGraph"]), TimeSpan.FromSeconds(10));

        cut.Click("button[title^='Style:']");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=style-row]")), TimeSpan.FromSeconds(5));

        var label = cut.Find("[data-testid=style-row]").GetAttribute("data-label");

        cut.Click($"[data-testid=style-row][data-label='{label}'] [data-testid=style-row-toggle]");

        var draws = JSInterop.Invocations["drawCytoscapeGraph"].Count;

        cut.Change($"[data-testid=style-row-more][data-label='{label}'] select", "circle");

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations["setCytoscapeLabelShape"], i => (string)i.Arguments[0] == label && (string)i.Arguments[1] == "circle"), TimeSpan.FromSeconds(5));
        Assert.Equal(draws, JSInterop.Invocations["drawCytoscapeGraph"].Count);
    }

    //Discard changes takes the staged edits back in place, as emptying the Generated tab by hand does, rather than drawing
    //the graph anew, which laid it out again (2026-11-04, the user).
    [Fact]
    public void DiscardingTheStagedChanges_PutsTheCanvasBackInPlace()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["drawCytoscapeGraph"]), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        //Previewing the staged changes, where the banner with Discard stands; the switch draws the graph once.
        if (!cut.Find("#chkReflectDb").HasAttribute("checked"))
        {
            var before = JSInterop.Invocations["drawCytoscapeGraph"].Count;

            cut.Change("#chkReflectDb", true);
            cut.WaitForAssertion(() => Assert.True(JSInterop.Invocations["drawCytoscapeGraph"].Count > before), TimeSpan.FromSeconds(5));
        }

        RightClick(cut, home, "node", "4096", "M3 Screw");
        cut.Click("[data-testid=element-menu-delete]");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Discard the staged changes']")), TimeSpan.FromSeconds(5));

        var draws = JSInterop.Invocations["drawCytoscapeGraph"].Count;
        var syncs = JSInterop.Invocations["syncCytoscapeGraph"].Count;

        //Awaited to its end, so a draw that would follow a wait is counted too.
        cut.Find("button[title^='Discard the staged changes']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()).GetAwaiter().GetResult();

        Assert.True(string.IsNullOrEmpty(GeneratedText(home.Instance)));
        Assert.True(JSInterop.Invocations["syncCytoscapeGraph"].Count > syncs);
        Assert.Equal(draws, JSInterop.Invocations["drawCytoscapeGraph"].Count);
    }

    private static void RightClick(IRenderedComponent<MainLayout> cut, IRenderedComponent<Home> home, string type, string id, string label, string selection = null, string props = null)
    {
        selection ??= $$$"""{"type":"{{{type}}}","id":"{{{id}}}","label":"{{{label}}}","properties":{}}""";

        cut.InvokeAsync(() => home.Instance.OnElementContextMenu(type, id, label, 120.5, 64, selection, props)).Wait();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=element-menu]")), TimeSpan.FromSeconds(5));
    }

    private static string GeneratedText(Home home)
    {
        return (string)typeof(Home).GetProperty("generatedQueryText", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(home);
    }

    //The debugger takes the canvas off the page while it is open, so closing it with its × draws the graph again, as
    //Visualize does; it left an empty card (2026-11-04, the user).
    [Fact]
    public void ClosingTheDebugger_DrawsTheGraphAgain()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string> { ["/gremlin"] = ElementMapAnswer }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["drawCytoscapeGraph"]), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.False(cut.Find("button[title^='Debug:']").HasAttribute("disabled")), TimeSpan.FromSeconds(5));

        cut.Click("button[title^='Debug:']");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title='Exit debugger']")), TimeSpan.FromSeconds(5));

        var draws = JSInterop.Invocations["drawCytoscapeGraph"].Count;

        cut.Click("button[title='Exit debugger']");

        cut.WaitForAssertion(() => Assert.True(JSInterop.Invocations["drawCytoscapeGraph"].Count > draws), TimeSpan.FromSeconds(5));
    }

    //What the In list's query answers for M3 Screw: two parts it is in.
    private const string InEdgesAnswer = """
        {"requestId":"5f7c2a9e-0d7e-4d32-9a53-1b0e6f3b8c12","status":{"message":"","code":200,"attributes":{"@type":"g:Map","@value":[]}},
         "result":{"data":{"@type":"g:List","@value":[
           {"@type":"g:Map","@value":["eId","e1","eLabel","composes","vId","7","vLabel","Leg 1"]},
           {"@type":"g:Map","@value":["eId","e2","eLabel","composes","vId","8","vLabel","Leg 2"]}
         ]},"meta":{"@type":"g:Map","@value":[]}}}
        """;

    //And the Out list's: none.
    private const string NoEdgesAnswer = """
        {"requestId":"5f7c2a9e-0d7e-4d32-9a53-1b0e6f3b8c13","status":{"message":"","code":200,"attributes":{"@type":"g:Map","@value":[]}},
         "result":{"data":{"@type":"g:List","@value":[]},"meta":{"@type":"g:Map","@value":[]}}}
        """;

    //The element panel's In and Out lists: a darker line under each heading, a light one under each connection, and the
    //row under the pointer shaded, so which one a remove belongs to shows (2026-11-03, the user). Each remove is a minus.
    [Fact]
    public void TheElementPanelsConnections_AreRowsUnderAHeadingWithAMinusEach()
    {
        var http = new HttpClient(new HomeRoutes(new Dictionary<string, string>(), async request =>
        {
            var body = await request.Content.ReadAsStringAsync();
            var answer = ElementMapAnswer;

            if (body.Contains("inE()"))
                answer = InEdgesAnswer;
            else if (body.Contains("outE()"))
                answer = NoEdgesAnswer;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")) };
        }));
        var cut = RenderHome(http: http, url: GraphIn2D(""));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Style:']")), TimeSpan.FromSeconds(10));

        var home = cut.FindComponent<Home>();

        cut.InvokeAsync(() => home.Instance.OnElementSelected("""{"type": "node", "id": "4096", "label": "Part", "properties": {"name": "M3 Screw"}}""")).Wait();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".gdbv-conn-row").Count), TimeSpan.FromSeconds(5));

        var rows = cut.FindAll(".gdbv-conn-row");

        Assert.Equal("In", rows[0].PreviousElementSibling.TextContent.Trim());
        Assert.Contains("gdbv-conn-head", rows[0].PreviousElementSibling.ClassList);
        Assert.Contains("Leg 1", rows[0].TextContent);
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector("button.gdbv-pick-remove > i.bi-dash-lg")));
    }

    //A line under the database's buttons and another over the new component part the query panel (2026-11-03, the user).
    [Fact]
    public void TheQueryPanelHasALineUnderTheDatabasesButtonsAndOverTheNewComponent()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gdbv-editor-pane")), TimeSpan.FromSeconds(5));

        var rules = cut.FindAll("[data-testid=side-rule]");

        Assert.Equal(2, rules.Count);
        Assert.Contains("Load DB", rules[0].PreviousElementSibling.TextContent);
        Assert.Contains("New component label", rules[1].NextElementSibling.TextContent);
    }

    //Knowledge-graph generation is Ask AI's since 2026-11-02: the ✨ entry point in the Import panel closes the panel and opens
    //Ask AI with a graph asked for in the box, the text to follow it. Ask AI is no child of the panel, so a generation outlives
    //the panel closing, which AC 12 asked of the window it replaced.
    [Fact]
    public void GenerateWithAi_OpensAskAi_WithTheWizard()
    {
        var cut = RenderHome();

        OpenImportPanel(cut);
        cut.Click("[data-testid=kg-generate]");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='kg-wizard'] [data-testid='kg-source-text']")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("textarea[placeholder^='GraphSON']"));
    }

    //A full localStorage quota used to take the whole page down: any save — loading an example was the
    //reported trigger — threw QuotaExceededError straight through the render. LocalAppStorage now swallows
    //the write and raises StorageQuotaExceeded; Home turns that into a dismissible banner instead of crashing.
    [Fact]
    public void StorageQuotaExceeded_ShowsADismissibleWarningInsteadOfCrashing()
    {
        var storage = new QuotaStorage();
        var cut = RenderHome(storage);

        Assert.DoesNotContain("Browser storage is full", cut.Markup);

        cut.InvokeAsync(storage.RaiseQuota);
        cut.WaitForAssertion(() => Assert.Contains("Browser storage is full", cut.Markup));

        cut.Click(".alert-warning .btn-close");

        //Waited for on the way in, so its disappearance is a transition and not a vacuous absence.
        cut.WaitForAssertion(() => Assert.DoesNotContain("Browser storage is full", cut.Markup), TimeSpan.FromSeconds(5));
    }

    ///<summary>
    ///Leaving the viewer takes its keyboard handler back off the document, as it put it there. Left behind, the
    ///handler held a reference the page had disposed, and a Delete pressed on the next page called into it. The 3D
    ///view goes with it: its animation loop outlives its container, and would keep drawing a canvas nobody can see.
    ///</summary>
    [Fact]
    public async Task LeavingTheViewer_DetachesItsKeyboardHandler_AndTearsDownThe3DView()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.Contains("keyboardInterop.attach", JSInterop.Invocations.Identifiers));

        //Counted rather than assumed absent: a draw that is not in 3D tears the 3D view down too.
        var tornDown = JSInterop.Invocations["graph3DInterop.destroyGraph"].Count;

        await DisposeComponentsAsync();

        JSInterop.VerifyInvoke("keyboardInterop.detach");
        Assert.Equal(tornDown + 1, JSInterop.Invocations["graph3DInterop.destroyGraph"].Count);
    }

    ///<summary>
    ///And the 2D view goes as the 3D one does, whatever view is showing, since its interop can hold an instance the page
    ///no longer shows. Cytoscape destroys itself only when its container is taken out of its own parent, which switching
    ///views does and leaving the page does not, so until 2026-10-04 a viewer left in 2D kept its instance, its frame
    ///loop, its observers and its listeners on the window, drawing for nobody until the viewer was next drawn in 2D.
    ///</summary>
    ///<remarks>
    ///Left without drawing first, as the 3D test is: disposed straight after a wait for a draw that was still under way,
    ///the page was not disposed at all in bUnit, not even a second later, though the app disposes it on navigation.
    ///</remarks>
    [Fact]
    public async Task LeavingTheViewer_TearsDownThe2DView()
    {
        var cut = RenderHome();

        cut.WaitForAssertion(() => Assert.Contains("keyboardInterop.attach", JSInterop.Invocations.Identifiers));

        //Nothing but leaving asks for it.
        Assert.DoesNotContain("destroyCytoscapeGraph", JSInterop.Invocations.Identifiers);

        await DisposeComponentsAsync();

        JSInterop.VerifyInvoke("destroyCytoscapeGraph");
    }

    //The Import panel's two buttons both overwrite the staged Generated queries. The first import has
    //nothing to lose and must not ask; importing over staged work must. Visualize is covered end to end
    //by e2e/import-confirm.spec.js; these cover Generate queries, whose miss shipped once already.
    [Fact]
    public void GenerateQueries_FirstImport_DoesNotAsk()
    {
        var cut = RenderHome();
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(true);

        OpenImportPanel(cut);
        PasteAndGenerate(cut, DotA);

        Assert.Empty(confirm.Invocations);
        Assert.Contains("Alice", GeneratedBuffer(cut));
    }

    [Fact]
    public void GenerateQueries_DecliningTheConfirm_KeepsTheStagedQueries()
    {
        var cut = RenderHome();
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(false);

        OpenImportPanel(cut);
        PasteAndGenerate(cut, DotA);
        PasteAndGenerate(cut, DotB);

        Assert.Single(confirm.Invocations);

        var buffer = GeneratedBuffer(cut);

        Assert.Contains("Alice", buffer);
        Assert.DoesNotContain("Zeta", buffer);
    }

    [Fact]
    public void GenerateQueries_AcceptingTheConfirm_ReplacesTheStagedQueries()
    {
        var cut = RenderHome();
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(true);

        OpenImportPanel(cut);
        PasteAndGenerate(cut, DotA);
        PasteAndGenerate(cut, DotB);

        Assert.Single(confirm.Invocations);

        var buffer = GeneratedBuffer(cut);

        Assert.Contains("Zeta", buffer);
        Assert.DoesNotContain("Alice", buffer);
    }

    //Committing staged queries with no connection used to silently no-op. It now warns and pulses the
    //connect button, exactly like running a query without a connection — the message lives in the stats bar.
    //Generating from an import enters offline mode (db == null) and reveals the workspace, so this is the
    //exact "offline, staged edits, no connection" state the user hits.
    [Fact]
    public void CommitOffline_WarnsToConnect_InsteadOfSilentlyDoingNothing()
    {
        var cut = RenderHome();
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(true);

        OpenImportPanel(cut);
        PasteAndGenerate(cut, DotA);

        Assert.DoesNotContain("No active connection", cut.Markup);

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Contains("Commit Changes")));

        cut.WaitForAssertion(() => Assert.Contains("No active connection", cut.Markup), TimeSpan.FromSeconds(5));
    }

    //A commit belongs to the tab it was started in (the multiple-connections spec, story 4, 2026-11-07): the person opens
    //another tab while it runs, and when it ends its status and its emptied buffer are the committing tab's, the new tab
    //untouched. A database that holds every statement until the test lets them go stands in for a live one.
    [Fact]
    public void ACommit_AnswersIntoItsOwnTab_WhileAnotherIsInFront()
    {
        var cut = RenderHome();
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(true);

        OpenImportPanel(cut);
        PasteAndGenerate(cut, DotA);
        cut.WaitForAssertion(() => Assert.Contains("Alice", GeneratedBuffer(cut)), TimeSpan.FromSeconds(5));

        var owner = OpenConnections(cut)[0];
        var committing = owner.Tabs[owner.ActiveTabIndex];
        var database = new HeldDb();

        owner.Db = database;
        owner.IsConnected = true;
        owner.OfflineMode = false;

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Contains("Commit Changes")));
        cut.WaitForAssertion(() => Assert.NotNull(committing.QueryCts), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, owner.Tabs.Count), TimeSpan.FromSeconds(5));

        var other = owner.Tabs[1];

        Assert.Same(other, owner.Tabs[owner.ActiveTabIndex]);

        //The new tab's own Commit waits for the running one, since the two share the connection's link: pressed all the
        //same, it starts nothing.
        other.GeneratedQueryText = "g.addV('waiting')";
        cut.Click(c => c.FindAll(".nav-link").First(b => b.TextContent.Trim() == "Generated"));

        var waiting = cut.WaitForElement("button.btn-warning[disabled]", TimeSpan.FromSeconds(5));

        Assert.Contains("Commit Changes", waiting.TextContent);

        Assert.True(waiting.HasAttribute("disabled"));
        waiting.Click();

        Assert.Null(other.QueryCts);
        Assert.Same(committing, owner.RunningTab);

        database.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Null(owner.RunningTab), TimeSpan.FromSeconds(5));

        Assert.EndsWith("query(ies) committed.", committing.CommitStatus);
        Assert.Equal("text-success", committing.CommitStatusClass);
        Assert.Null(committing.GeneratedQueryText);
        Assert.Null(other.CommitStatus);
        Assert.Same(other, owner.Tabs[owner.ActiveTabIndex]);
    }

    //Load DB and Run each line answer into the tab they ran in as Run does (story 4, 2026-11-07): a new tab is opened
    //while each runs, and the answer is the first tab's, the new one's empty.
    [Theory]
    [InlineData("Load DB")]
    [InlineData("Run each line")]
    public void ALoadOrALineByLineRun_AnswersIntoItsOwnTab_WhileAnotherIsInFront(string action)
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=workspace-tabs]")), TimeSpan.FromSeconds(5));

        var owner = OpenConnections(cut)[0];
        var ran = owner.Tabs[owner.ActiveTabIndex];
        var database = new HeldDb();

        ran.QueryText = "g.V()\ng.E()";
        owner.Db = database;
        owner.IsConnected = true;
        owner.OfflineMode = false;

        cut.Render();

        if (action == "Load DB")
            cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Load DB"));
        else
            cut.Click("button[title^='Run each line']");

        cut.WaitForAssertion(() => Assert.NotNull(ran.QueryCts), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, owner.Tabs.Count), TimeSpan.FromSeconds(5));

        var other = owner.Tabs[1];

        //The tab in front has a graph of its own and a status on it, which the run ending behind it must leave as they
        //are: a redraw would lay the graph out again under the person's eyes, and it clears the tab's selection and status.
        other.LastResultData = JsonDocument.Parse(@"[{""id"":""1"",""label"":""person"",""type"":""vertex""}]").RootElement;
        other.CommitStatus = "Left as it was.";
        other.VisualizationMode = 2;
        ran.VisualizationMode = 2;

        database.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Null(owner.RunningTab), TimeSpan.FromSeconds(5));

        //A redraw clears the status at once; a second is well past when it would have.
        Thread.Sleep(1000);

        Assert.Equal("Left as it was.", other.CommitStatus);
        Assert.Equal(JsonValueKind.Array, ran.LastResultData.ValueKind);
        Assert.NotNull(ran.QueryResults);
        Assert.NotNull(ran.LastQueryMs);
        Assert.Null(other.QueryResults);
        Assert.Equal(1, other.LastResultData.GetArrayLength());
        Assert.Null(other.LastQueryMs);

        if (action == "Load DB")
        {
            Assert.Equal(JsonValueKind.Array, ran.GraphResultData.ValueKind);
            Assert.Equal(JsonValueKind.Undefined, other.GraphResultData.ValueKind);
        }
    }

    //A database that answers every statement with an empty success once the test opens its gate.
    private sealed class HeldDb : IGraphDb
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
        {
            await Gate.Task.WaitAsync(cancellationToken);

            return GraphDbResult.Success(JsonDocument.Parse("[]").RootElement);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    //Offline mode is a drawing surface, not a query — entering it seeds an empty base, but the "no results
    //to visualize" / "query ran — no graph to display" banner must not appear (there was no query). The
    //companion behavior — showing that banner for a real empty result even while previewing edits with
    //nothing staged — needs a live database, so it's covered by manual / e2e verification, not here.
    [Fact]
    public void OfflineMode_DoesNotShowTheNoResultsBanner()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);

        Assert.DoesNotContain("No results could be visualized", cut.Markup);
        Assert.DoesNotContain("no graph to display", cut.Markup);
    }

    //The query panel only renders once there is something to show, and offline mode is the connection-free
    //way in. The database type is picked first, because entering offline mode closes the connection card.

    //The database-type buttons carry the query language on a quieter second line, so their text
    //content runs the two together ("Cosmos DBGremlin"). Matching on the database name alone, and
    //only within that group, keeps these tests about which database was chosen rather than about
    //how its button is laid out.
    private static void ClickDatabaseType(IRenderedComponent<MainLayout> cut, string name)
    {
        //The group is only in the markup while the connection dialog is open, so open it when it is not,
        //then wait for the group rather than reaching into a tree that is still on its way and blaming
        //the button for not being there.
        OpenConnectionDialog(cut);
        cut.WaitForElement("[aria-label=\"Database type\"]", TimeSpan.FromSeconds(5));

        cut.Click(c => c.Find("[aria-label=\"Database type\"]").QuerySelectorAll("button").First(b => b.TextContent.Contains(name)));
    }

    private IRenderedComponent<MainLayout> RenderHomeWithQueryPanel(string databaseButton = null)
    {
        var cut = RenderHome();

        //The database type is picked in the connection dialog, which is closed until asked for.
        if (databaseButton != null)
        {
            OpenConnectionDialog(cut);
            ClickDatabaseType(cut, databaseButton);
        }

        EnterOfflineMode(cut);

        return cut;
    }

    //GUN has no query language, so an editor, a language picker and an Examples tab would all be inviting
    //a query it cannot answer. The form that replaces them is the observable end of NoQueryLanguage.
    [Fact]
    public void Gun_ReplacesTheEditorWithAForm()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        //The form arriving is the positive signal that the engine switch has rendered. The two checks
        //after it are absences, and an absence reads as satisfied just as readily while the render is
        //still on its way -- so they only mean anything once something that must appear has appeared.
        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Start from key"),
            TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("select[title='Editor syntax highlighting']"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Examples");

        //Switching back brings all three straight back — this is per-engine, not a mode the viewer enters.
        //(Entering offline mode closed the connection dialog; the pill reopens it.)
        OpenConnectionDialog(cut);
        ClickDatabaseType(cut, "Apache TinkerPop");

        //The same again in reverse, and this is the one CI failed on: it read the markup before the
        //switch back had rendered, found GUN's form still standing, and reported that as the form
        //failing to leave. Wait for the editor to return, which is what the switch produces.
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("select[title='Editor syntax highlighting']")),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Start from key");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Examples");
    }

    //GUN's Generated tab holds the JavaScript the viewer will run — the read the form describes while
    //nothing is staged, and the writes themselves once something is.
    [Fact]
    public void GunGeneratedTab_ShowsTheJavaScriptThatWillRun()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        //Each change re-renders the form, so each is raised through the cut, which finds its element in the form as the
        //change before left it (RenderedEvents).
        cut.Change("input#gunStartKey", "users");
        cut.Change("input#gunMapChildren", true);

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Generated"));

        //Monaco draws the text itself, so what's pinned here is the value handed to it. Waited for rather
        //than read on the spot: the buffer reaches the editor through a render the click only schedules,
        //so reading immediately passes on a warm run and fails on a slow one — which is exactly how this
        //test behaved, roughly half the time, whenever the suite ran straight after a build.
        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindComponents<MonacoEditor>(),
            e => (e.Instance.Value ?? "").Contains("gun.get('users').map().once(")));
    }

    //Switching database types has to leave the port pointing at the engine you switched *to*. Each
    //handler used to carry a list of every other engine's port to tell a leftover default from a chosen
    //one, so an engine added later was invisible to the handlers written before it.
    [Fact]
    public void SwitchingEngines_CarriesThePortOver()
    {
        var cut = RenderHome();

        var port = () => cut.Find("[data-testid=connection-port]").GetAttribute("value");

        ClickDatabaseType(cut, "ArangoDB");
        Assert.Equal("8529", port());

        //This was the break: 8529 was not in Bolt's list, so it stayed and Neo4j dialed ArangoDB's port.
        ClickDatabaseType(cut, "Neo4j / Memgraph");
        Assert.Equal("7687", port());

        ClickDatabaseType(cut, "Dgraph");
        Assert.Equal("8080", port());

        ClickDatabaseType(cut, "Apache TinkerPop");
        Assert.Equal("8182", port());
    }

    [Fact]
    public void APortTypedByHandSurvivesTheSwitch()
    {
        //Only a default gets replaced — a number someone entered is a decision.
        var cut = RenderHome();

        ClickDatabaseType(cut, "Neo4j / Memgraph");
        cut.Change("[data-testid=connection-port]", "7688");

        ClickDatabaseType(cut, "ArangoDB");

        Assert.Equal("7688", cut.Find("[data-testid=connection-port]").GetAttribute("value"));
    }

    //── Encrypted unless somebody says otherwise (the user's rule of 2026-10-03) ─────────────────────

    //A connection nobody has touched yet dials with TLS, whichever engine it is switched to: switching keeps the
    //switch where it was, and the card opens with it on.
    [Theory]
    [InlineData("Apache TinkerPop", "wss://")]
    [InlineData("Cosmos DB", "wss://")]
    [InlineData("Neo4j / Memgraph", "bolt+s://")]
    [InlineData("ArangoDB", "https://")]
    [InlineData("Dgraph", "https://")]
    public void ANewConnection_StartsEncrypted_WhicheverEngineIsPicked(string engine, string scheme)
    {
        var cut = RenderHome();

        ClickDatabaseType(cut, engine);

        Assert.StartsWith(scheme, cut.Find("code.gdbv-url-preview").TextContent.Trim());
    }

    private static Dictionary<string, GremlinDB.GremlinConnection> Saved(GremlinDB.GremlinConnection connection)
    {
        return new Dictionary<string, GremlinDB.GremlinConnection> { ["Saved"] = connection };
    }

    private static string PasswordIn(IRenderedComponent<MainLayout> cut)
    {
        return cut.FindAll("[data-testid=connection-panel] input[type=password]").First().GetAttribute("value") ?? "";
    }

    //Turning SSL off is anybody's choice, but the password saved with an encrypted connection does not go with
    //it: it leaves the form, so neither Connect nor Save sends or saves it, until it is typed again. Before the rule
    //it stayed in the form and the next Connect sent it in the clear.
    [Fact]
    public async Task UntickingSsl_HoldsBackThePasswordSavedWithIt_UntilItIsTypedAgain()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", Saved(new GremlinDB.GremlinConnection
        {
            DatabaseType = GraphDbProviders.TinkerPop,
            Transport = "WebSocket",
            Hostname = "graph.example.com",
            Port = 8182,
            UseSSL = true,
            Username = "janus",
            AuthKey = "saved-secret"
        }));

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("saved-secret", PasswordIn(cut)), TimeSpan.FromSeconds(5));

        cut.Change("[data-testid=connection-panel] input[role=switch]", false);

        Assert.Equal("", PasswordIn(cut));
        Assert.Contains("not sent now that this one is not", cut.Find("[data-testid=password-held-back]").TextContent);

        //Encrypted again before anything was typed, the saved password is back, and nothing is said.
        cut.Change("[data-testid=connection-panel] input[role=switch]", true);

        Assert.Equal("saved-secret", PasswordIn(cut));
        Assert.Empty(cut.FindAll("[data-testid=password-held-back]"));

        //Typed again without SSL, it is somebody's own entry: it stays, whatever else changes.
        cut.Change("[data-testid=connection-panel] input[role=switch]", false);
        cut.Change(c => c.FindAll("[data-testid=connection-panel] input[type=password]").First(), "saved-secret");
        cut.Change("[data-testid=connection-port]", "8183");

        Assert.Equal("saved-secret", PasswordIn(cut));
        Assert.Empty(cut.FindAll("[data-testid=password-held-back]"));
    }

    //Saved by + while held back, the connection is saved without it: a save that weakens a connection forgets
    //the password unless the same save gives one.
    [Fact]
    public async Task ASaveWithoutSsl_ForgetsThePasswordSavedWithIt()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", Saved(new GremlinDB.GremlinConnection
        {
            DatabaseType = GraphDbProviders.Neo4j,
            Hostname = "neo.example.com",
            Port = 7687,
            UseSSL = true,
            Username = "neo4j",
            AuthKey = "saved-secret"
        }));

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("saved-secret", PasswordIn(cut)), TimeSpan.FromSeconds(5));

        cut.Change("[data-testid=connection-panel] input[role=switch]", false);
        cut.Click("[data-testid=connection-save]");

        var stored = await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>("graphdbviewer:connections");

        Assert.False(stored["Saved"].UseSSL);
        Assert.True(string.IsNullOrEmpty(stored["Saved"].AuthKey));
    }

    //A SPARQL endpoint is encrypted by its own scheme, so the same holds when its address goes from https to http.
    [Fact]
    public async Task ASparqlEndpointMadeHttp_HoldsBackThePasswordSavedWithIt()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", Saved(new GremlinDB.GremlinConnection
        {
            DatabaseType = GraphDbProviders.Sparql,
            Endpoint = "https://rdf.example.com/sparql",
            Username = "reader",
            AuthKey = "saved-secret"
        }));

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("saved-secret", PasswordIn(cut)), TimeSpan.FromSeconds(5));

        cut.Input("input[placeholder='https://query.wikidata.org/sparql']", "http://rdf.example.com/sparql");

        Assert.Equal("", PasswordIn(cut));
        Assert.NotNull(cut.Find("[data-testid=password-held-back]"));
    }

    //An address that opens a connection gives its own password, transport and all: it is not a saved one, even
    //when a saved encrypted connection happens to have the same password.
    [Fact]
    public async Task AnAddressThatGivesAPasswordWithoutSsl_IsNotTakenForASavedOne()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", Saved(new GremlinDB.GremlinConnection
        {
            DatabaseType = GraphDbProviders.TinkerPop,
            Transport = "WebSocket",
            Hostname = "graph.example.com",
            Port = 8182,
            UseSSL = true,
            AuthKey = "same-secret"
        }));

        var cut = RenderHome(storage, url: "http://localhost/?host=other.example.com&port=8182&ssl=false&authKey=same-secret&connect=false");

        OpenConnectionDialog(cut);

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("input"), i => i.GetAttribute("value") == "other.example.com"), TimeSpan.FromSeconds(5));

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Connect"));

        cut.WaitForAssertion(() => Assert.Equal("same-secret", PasswordIn(cut)), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=password-held-back]"));
    }

    //A connection saved without TLS was saved by somebody who chose that, password and all: the rule is about a
    //password given for an encrypted connection, and this one keeps its own.
    [Fact]
    public async Task AConnectionSavedUnencrypted_KeepsItsPassword()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", Saved(new GremlinDB.GremlinConnection
        {
            DatabaseType = GraphDbProviders.TinkerPop,
            Transport = "WebSocket",
            Hostname = "graph.example.com",
            Port = 8182,
            UseSSL = false,
            AuthKey = "plain-secret"
        }));

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("plain-secret", PasswordIn(cut)), TimeSpan.FromSeconds(5));

        cut.Change("[data-testid=connection-panel] input[role=switch]", true);
        cut.Change("[data-testid=connection-panel] input[role=switch]", false);

        Assert.Equal("plain-secret", PasswordIn(cut));
        Assert.Empty(cut.FindAll("[data-testid=password-held-back]"));
    }

    //The Live toggle only exists where an engine can push — GUN, and nothing else.
    [Fact]
    public void LiveToggle_ShowsOnlyForAnEngineThatKeepsAnswering()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Live");

        //Back to an engine where a result is final, and it goes. Waited for, like the group above: under
        //a full-suite load the re-render after the type click has been seen to land a beat later.
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Contains("Offline mode")));
        ClickDatabaseType(cut, "Apache TinkerPop");

        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Live"), TimeSpan.FromSeconds(5));
    }

    //Live over a read with nothing to follow says why, in the bar a failed read uses, and switches itself back
    //off. The page's refusal used to be taken for a subscription: the toggle went green over nothing, silently.
    [Fact]
    public void LiveThePageRefuses_ShowsWhy_AndSwitchesBackOff()
    {
        var cut = RenderHome();

        JSInterop.Setup<string>("gunInterop.watch", _ => true).SetResult("{\"error\":\"Nothing to watch at 'alice'.\"}");

        //No relay at all is a GUN connection, an in-browser graph, and the probe's empty answer is a peer reached.
        ClickDatabaseType(cut, "GUN");
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Connect"));

        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Live" && !b.HasAttribute("disabled")),
            TimeSpan.FromSeconds(5));

        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Live"));

        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll(".alert-danger"), a => a.TextContent.Contains("Nothing to watch at 'alice'.")),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("btn-success", cut.FindAll("button").First(b => b.TextContent.Trim() == "Live").ClassList);
    }

    //A disabled Connect button that explains nothing reads as a database that does not work -- which is
    //exactly how SPARQL looked, since selecting it leaves an empty endpoint field behind. The reason
    //lives on a wrapper, because a disabled control receives no mouse events and shows no tooltip.
    [Fact]
    public void ConnectDisabled_SaysWhatIsMissing()
    {
        var cut = RenderHome();

        ClickDatabaseType(cut, "SPARQL / RDF");

        var connect = cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect");

        Assert.True(connect.HasAttribute("disabled"));
        Assert.Contains("SPARQL endpoint URL", connect.ParentElement.GetAttribute("title"));
    }

    //TinkerPop needs nothing typed to attempt a connection, so there is nothing to explain and no
    //tooltip to leave hanging over an enabled button.
    [Fact]
    public void ConnectEnabled_ExplainsNothing()
    {
        var cut = RenderHome();

        OpenConnectionDialog(cut);

        var connect = cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect");

        Assert.False(connect.HasAttribute("disabled"));
        Assert.True(string.IsNullOrEmpty(connect.ParentElement.GetAttribute("title")));
    }

    //── The instance's own graph on the canvas ──────────────────────────────────────────────────
    //
    //The platform offering its own graph as a browsable connection (PIM's question 3, the user's
    //decision of 2026-09-04). Three conditions have to hold before it is even offered, and each of the
    //first three tests removes one of them: this is a connection nobody configured, holding everything
    //every plugin has stored, so an option that appears when it should not is the whole risk.

    private const string ConfiguredGraph = @"{""configured"":true,""databaseType"":""ApacheTinkerPop"",""host"":""graph""}";
    private const string NoGraph = @"{""configured"":false}";

    //Treeality's host options, Share off as Treeality.Client registers them; a test of the Share dialog on a host with the
    //instance graph turns it on.
    private IRenderedComponent<MainLayout> RenderPlatformHome(
        string status,
        string[] permissions,
        bool hasPlugins = true,
        bool offersShare = false,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> otherwise = null)
    {
        var account = "{\"signedIn\":true,\"email\":\"a@example.com\",\"permissions\":[" + string.Join(",", permissions.Select(p => "\"" + p + "\"")) + "]}";

        Services.AddSingleton<IAppStorage>(new NullStorage());
        Services.AddSingleton(new HttpClient(new HomeRoutes(new Dictionary<string, string>
        {
            ["/" + AccountInfo.Path] = account,
            ["/" + InstanceGraphContract.StatusPath] = status
        }, otherwise)) { BaseAddress = new Uri("http://localhost/") });
        Services.AddScoped<WorkspaceStore>();
        Services.AddScoped<LlmConnectionStore>();
        Services.AddScoped<ShellState>();
        Services.AddSingleton(new ViewerHostOptions { AppName = "Treeality", HasServerRoute = true, HasPlugins = hasPlugins, OffersShare = offersShare });

        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<MainLayout>(p => p.Add(l => l.Body, HomeBody));

        //What these look at is the connection form, in its dialog since 2026-10-29.
        OpenConnectionDialog(cut);

        return cut;
    }

    private sealed class HomeRoutes : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes;
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _otherwise;

        //What answers a path the routes do not name, when a test has something to say there.
        public HomeRoutes(Dictionary<string, string> routes, Func<HttpRequestMessage, Task<HttpResponseMessage>> otherwise = null)
        {
            _routes = routes;
            _otherwise = otherwise;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_routes.TryGetValue(request.RequestUri.AbsolutePath, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")) });

            //Given up on when the page cancels, as a real server's answer is: a connect stopped while it waits.
            if (_otherwise != null)
                return _otherwise(request).WaitAsync(cancellationToken);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    //The Plugins page's Take the tour opens the viewer with tour=start (2026-11-06, the user): the viewer starts the tour once
    //it has drawn, and takes the word off its address, so a reload does not start it again.
    [Fact]
    public void AnAddressAskingForTheTour_StartsIt_AndForgetsTheAsking()
    {
        //Every stop on the page, as tour.js would say of a drawn viewer.
        JSInterop.Setup<bool[]>("gdbvTour.present", _ => true).SetResult(Enumerable.Repeat(true, 64).ToArray());

        var cut = RenderHome(url: "/?tour=start");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tour-card]")), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("tour=", Services.GetRequiredService<NavigationManager>().Uri);
    }

    //One button through a connect (2026-11-06, the user: "there's no need to add an extra control"): Connect says Connecting…
    //while it tries, a click on it stops trying and it says Connect again, and no Cancel button comes beside it.
    [Fact]
    public void ConnectingIsTheConnectButton_WhichAClickStops()
    {
        var probe = new TaskCompletionSource<HttpResponseMessage>();

        //The instance graph never answers the connect's probe.
        Task<HttpResponseMessage> Execute(HttpRequestMessage request)
        {
            if (request.RequestUri.AbsolutePath != "/" + InstanceGraphContract.ExecutePath)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            return probe.Task;
        }

        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery], otherwise: Execute);

        cut.WaitForAssertion(() => Assert.Contains("instance-graph ", ConnectionKeys(cut)), TimeSpan.FromSeconds(5));
        PickSavedConnection(cut, "instance-graph ");
        cut.WaitForAssertion(() => Assert.Equal("Connect", cut.Find("[data-testid=connect-button]").TextContent.Trim()), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=connect-button]");

        cut.WaitForAssertion(() => Assert.Equal("Connecting…", cut.Find("[data-testid=connect-button]").TextContent.Trim()), TimeSpan.FromSeconds(5));
        Assert.False(cut.Find("[data-testid=connect-button]").HasAttribute("disabled"));
        Assert.Single(cut.FindAll("[data-testid=connect-button] .spinner-border"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Cancel");

        cut.Click("[data-testid=connect-button]");

        cut.WaitForAssertion(() => Assert.Equal("Connect", cut.Find("[data-testid=connect-button]").TextContent.Trim()), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=connect-button] .spinner-border"));
    }

    //A query running in one tab leaves the others free (the multiple-connections spec, story 4, 2026-11-07): another tab
    //of the same connection comes in front, opens and closes, but its Run waits, since the two share one live link; the
    //answer is the tab's that ran it, wherever the person is when it comes. Until 2026-11-03 a query tab switched to
    //mid-run took the other one's answer, and until 2026-11-07 the tabs waited for the run instead.
    [Fact]
    public void ARunningQuery_LeavesTheOtherTabsFree_AndAnswersIntoItsOwn()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>();
        var holding = false;

        //The instance graph answers the connect's probe and schema reads, and holds the query Run sends until the test
        //lets it go.
        Task<HttpResponseMessage> Execute(HttpRequestMessage request)
        {
            if (request.RequestUri.AbsolutePath != "/" + InstanceGraphContract.ExecutePath)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            if (holding)
                return held.Task;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GraphQueryResponse { Data = JsonDocument.Parse("[]").RootElement }) });
        }

        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery], otherwise: Execute);

        cut.WaitForAssertion(() => Assert.Contains("instance-graph ", ConnectionKeys(cut)), TimeSpan.FromSeconds(5));
        PickSavedConnection(cut, "instance-graph ");
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Connect"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=workspace-tabs]")), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)").Count), TimeSpan.FromSeconds(5));

        holding = true;
        cut.Click("button[title^='Run Query']");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Stop']")), TimeSpan.FromSeconds(5));

        var tabs = OpenConnections(cut)[0].Tabs;
        var running = tabs[1];

        //Running: the first tab comes in front, and its Run waits for the other's, saying why.
        cut.Click("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Contains("active", cut.FindAll("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)")[0].ClassName), TimeSpan.FromSeconds(5));

        var waiting = cut.Find("button[title^='Another query tab of this connection is running']");

        Assert.True(waiting.HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("button[title^='Stop']"));

        //Pressed all the same, it starts nothing.
        waiting.Click();

        Assert.Null(tabs[0].QueryCts);
        Assert.Same(running, OpenConnections(cut)[0].RunningTab);

        //A new tab opens beside them meanwhile.
        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)").Count), TimeSpan.FromSeconds(5));

        //Ended: the answer is the running tab's, the others' untouched, and Run is free again where the person is.
        held.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GraphQueryResponse { Data = JsonDocument.Parse(@"[{""id"":""1"",""label"":""person""}]").RootElement }) });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title^='Run Query']")), TimeSpan.FromSeconds(5));

        Assert.Equal(JsonValueKind.Array, running.LastResultData.ValueKind);
        Assert.Equal(1, running.LastResultData.GetArrayLength());
        Assert.Null(running.QueryCts);
        Assert.Equal(JsonValueKind.Undefined, tabs[0].LastResultData.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, tabs[2].LastResultData.ValueKind);
        Assert.Null(OpenConnections(cut)[0].RunningTab);

        //Back on the tab that ran it: its answer, and Run.
        cut.Click(c => c.FindAll("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)")[1]);
        cut.WaitForAssertion(() => Assert.Contains("active", cut.FindAll("[data-testid=workspace-tabs] .nav-link:not(.gdbv-ws-add)")[1].ClassName), TimeSpan.FromSeconds(5));

        Assert.NotEmpty(cut.FindAll("button[title^='Run Query']"));
    }

    //A connect leaves the tabs free as a query does (story 4, 2026-11-07): drawing offline with the tabs on screen, a
    //connect to the instance graph that has not answered yet lets a new tab open, and when it answers, a second connection
    //being in front by then, what it learned is its own connection's. The editor's schema follows the one in front, so a
    //schema read behind is not pushed to it.
    [Fact]
    public void AConnect_LeavesTheTabsFree_AndConnectsItsOwnConnection()
    {
        var (cut, gate, connecting, added) = ConnectWhileAnotherComesInFront(fails: false);
        var schemas = JSInterop.Invocations.Count(i => i.Identifier == "monacoInterop.setSchema");

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(connecting.IsConnecting), TimeSpan.FromSeconds(5));

        //What the connect learned is the first's; the one in front is as it was, its form still open.
        Assert.True(connecting.IsConnected);
        Assert.NotNull(connecting.Db);
        Assert.Equal("text-success", connecting.StatusClass);
        Assert.False(added.IsConnected);
        Assert.Null(added.Db);
        Assert.Null(added.StatusMessage);
        Assert.Single(cut.FindAll("[data-testid=connection-dialog]"));
        Assert.Equal(schemas, JSInterop.Invocations.Count(i => i.Identifier == "monacoInterop.setSchema"));
    }

    //A connect that fails behind says so on its own connection, and opens no dialog over the one in front: the person is
    //working there, its card closed. Its own dialog says why when its tab is pressed.
    [Fact]
    public void AConnectFailingBehind_OpensNoDialogOverTheOneInFront()
    {
        var (cut, gate, connecting, added) = ConnectWhileAnotherComesInFront(fails: true);

        //The one in front's query tab closes its form, as working on it does.
        cut.Click(QueryTabsOf(2) + " .nav-link:not(.gdbv-ws-add)");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=connection-dialog]")), TimeSpan.FromSeconds(5));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(connecting.IsConnecting), TimeSpan.FromSeconds(5));

        Assert.False(connecting.IsConnected);
        Assert.Equal("text-danger", connecting.StatusClass);
        Assert.StartsWith("Connection failed", connecting.StatusMessage);
        Assert.Null(added.StatusMessage);
        Assert.Empty(cut.FindAll("[data-testid=connection-dialog]"));

        cut.Click(ConnectionTabOf(1) + " [data-testid=viewer-connection]");
        cut.WaitForAssertion(() => Assert.Contains("Connection failed", cut.Find("[data-testid=connection-dialog]").TextContent), TimeSpan.FromSeconds(5));
    }

    //Connects the instance graph from offline mode, its answer held behind a gate, and while it dials opens a tab of its
    //own and brings a second connection in front. The answer, once the gate opens, is an empty graph or a refusal.
    private (IRenderedComponent<MainLayout> Cut, TaskCompletionSource Gate, ViewerConnection Connecting, ViewerConnection Added) ConnectWhileAnotherComesInFront(bool fails)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        HttpResponseMessage Answer()
        {
            if (fails)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("The graph is down.") };

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new GraphQueryResponse { Data = JsonDocument.Parse("[]").RootElement }) };
        }

        async Task<HttpResponseMessage> Execute(HttpRequestMessage request)
        {
            if (request.RequestUri.AbsolutePath != "/" + InstanceGraphContract.ExecutePath)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            await gate.Task;

            return Answer();
        }

        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery], otherwise: Execute);

        cut.WaitForAssertion(() => Assert.Contains("instance-graph ", ConnectionKeys(cut)), TimeSpan.FromSeconds(5));
        EnterOfflineMode(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=workspace-tabs]")), TimeSpan.FromSeconds(5));

        OpenConnectionDialog(cut);
        PickSavedConnection(cut, "instance-graph ");
        cut.Click(c => c.FindAll("button").First(b => b.TextContent.Trim() == "Connect"));

        var connecting = OpenConnections(cut)[0];

        cut.WaitForAssertion(() => Assert.True(connecting.IsConnecting), TimeSpan.FromSeconds(5));
        cut.Click("[data-testid=workspace-tabs] .gdbv-ws-add");
        cut.WaitForAssertion(() => Assert.Equal(2, connecting.Tabs.Count), TimeSpan.FromSeconds(5));

        //A second connection comes in front, its form open, while the first still dials.
        cut.Click("[data-testid=viewer-add-connection]");
        cut.WaitForAssertion(() => Assert.Equal(2, OpenConnections(cut).Count), TimeSpan.FromSeconds(5));

        return (cut, gate, connecting, OpenConnections(cut)[1]);
    }

    [Fact]
    public void TheInstanceGraphIsOfferedToAnAccountThatMayQueryIt()
    {
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery]);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("instance-graph ", ConnectionKeys(cut));
            Assert.Equal("This instance's graph (read-only)", cut.Find("[data-testid=connection-picker-menu] [data-key='instance-graph ']").TextContent.Trim());
        });

        //It is not one of theirs: nothing on its row renames, copies or deletes it.
        Assert.Empty(cut.FindAll("[data-testid=connection-picker-menu] [data-key='instance-graph '] [data-act]"));
    }

    [Fact]
    public void WithoutThePermissionItIsNotOfferedAtAll()
    {
        //Not disabled and not explained: an account that cannot query it has no business being told the
        //instance has a graph, and the endpoint would refuse it anyway.
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceConfigure]);

        cut.WaitForAssertion(() => Assert.Empty(ConnectionKeys(cut)));

        Assert.DoesNotContain("This instance's graph", cut.Markup);
    }

    [Fact]
    public void WithNoGraphConfiguredItIsNotOfferedEither()
    {
        //An option that opens onto "none is configured" is worse than no option.
        var cut = RenderPlatformHome(NoGraph, [AccountInfo.InstanceQuery]);

        cut.WaitForAssertion(() => Assert.Empty(ConnectionKeys(cut)));

        Assert.DoesNotContain("This instance's graph", cut.Markup);
    }

    [Fact]
    public void OnTheStandaloneViewerThereIsNoSuchThing()
    {
        //The web-only edition has no plugin platform and must show no trace of one.
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery], hasPlugins: false);

        cut.WaitForAssertion(() => Assert.Empty(ConnectionKeys(cut)));

        Assert.DoesNotContain("This instance's graph", cut.Markup);
    }

    [Fact]
    public void ChoosingItReplacesTheFormRatherThanEmptyingIt()
    {
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery]);

        cut.WaitForAssertion(() => Assert.Contains("instance-graph ", ConnectionKeys(cut)));

        PickSavedConnection(cut, "instance-graph ");

        //There is nothing here for anyone to fill in: the host holds the connection and dials it itself.
        Assert.DoesNotContain("Connection name", cut.Markup);
        Assert.Contains("read-only", cut.Markup);

        //And Connect is offered, though the engine's own IsConfigured would say no to a connection with
        //no hostname in it — the one case where what the form holds is not what will be dialed.
        var connect = cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect");

        Assert.False(connect.HasAttribute("disabled"));

        //Saving it is not offered: it is not one of this account's connections and never becomes one.
        Assert.Empty(cut.FindAll("[data-testid=connection-save]"));
    }

    //Share is the standalone viewer's (the user, 2026-10-31): Treeality offers no Share button, and so no dialog.
    [Fact]
    public void Treeality_OffersNoShareButton()
    {
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery]);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-import-export]")), TimeSpan.FromSeconds(5));

        Assert.Empty(cut.FindAll("[data-testid=viewer-share]"));
        Assert.Empty(cut.FindAll("[data-testid=share-dialog]"));
    }

    //On a host that offers Share and has an instance graph, the instance's graph shared while it is open is embedded by
    //name, read with the token pasted into the dialog, which goes after the #; and the dialog says where such a token is
    //made until one is pasted.
    [Fact]
    public void Share_EmbedsTheInstancesGraph_WithTheTokenAfterTheHash()
    {
        var cut = RenderPlatformHome(ConfiguredGraph, [AccountInfo.InstanceQuery], offersShare: true);

        cut.WaitForAssertion(() => Assert.Contains("instance-graph ", ConnectionKeys(cut)));
        PickSavedConnection(cut, "instance-graph ");
        cut.Click("[data-testid=connection-dialog] .btn-close");

        cut.Click("[data-testid=viewer-share]");
        cut.WaitForElement("[data-testid=share-dialog]", TimeSpan.FromSeconds(5));

        //Copy has its picture before its word (2026-11-04, the user).
        var copy = cut.Find("[data-testid=share-copy]");

        Assert.Equal("I", copy.FirstElementChild.TagName);
        Assert.Contains("bi-copy", copy.FirstElementChild.ClassList);
        Assert.Equal("Copy link", copy.TextContent.Trim());

        cut.Click("[data-testid=share-embed-tab]");

        Assert.Contains("/account", cut.Find("[data-testid=share-dialog]").TextContent);

        cut.Input("[data-testid=share-token]", "trl_abc");

        var code = cut.Find("[data-testid=share-output]").GetAttribute("value");

        Assert.StartsWith("<iframe src=\"http://localhost/embed?instance=true&amp;", code);
        Assert.Contains("#token=trl_abc\"", code);
    }

    //Each saved connection is listed by its name alone (2026-11-04, the user): no letter badge, and no line under it with
    //its transport or engine, which it carried since 2026-10-31.
    [Fact]
    public async Task ASavedConnection_IsListedByItsNameAlone()
    {
        var storage = new InMemoryStorage();

        await storage.SetAsync("graphdbviewer:connections", new Dictionary<string, GremlinDB.GremlinConnection>
        {
            ["gremlin"] = new("HTTP", 8182, false, "gremlin.test", "", "", "") { DatabaseType = GraphDbProviders.TinkerPop },
            ["bolt"] = new("WebSocket", 7687, false, "bolt.test", "", "neo4j", "") { DatabaseType = GraphDbProviders.Neo4j },
            ["wikidata"] = new("WebSocket", 443, true, "", "", "", "") { DatabaseType = GraphDbProviders.Sparql, Endpoint = "https://query.wikidata.org/sparql" }
        });

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);

        cut.WaitForAssertion(() =>
        {
            ConnectionKeys(cut);

            Assert.Equal("gremlin", PickerRowText(cut, "gremlin"));
            Assert.Equal("bolt", PickerRowText(cut, "bolt"));
            Assert.Equal("wikidata", PickerRowText(cut, "wikidata"));
        }, TimeSpan.FromSeconds(5));
    }

    //A row's text: its name and nothing more, the buttons beside it being pictures alone.
    private static string PickerRowText(IRenderedComponent<MainLayout> cut, string key)
    {
        return cut.Find($"[data-testid=connection-picker-menu] [data-key='{key}']").TextContent.Trim();
    }

    private const string ConnectionsKey = "graphdbviewer:connections";

    private static async Task<InMemoryStorage> SavedConnections(params string[] names)
    {
        var storage = new InMemoryStorage();
        var saved = new Dictionary<string, GremlinDB.GremlinConnection>();

        foreach (var name in names)
            saved[name] = new("HTTP", 8182, false, name + ".test", "", "", "") { DatabaseType = GraphDbProviders.TinkerPop };

        await storage.SetAsync(ConnectionsKey, saved);

        return storage;
    }

    private static async Task<List<string>> StoredConnectionNames(InMemoryStorage storage)
    {
        return (await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>(ConnectionsKey)).Keys.ToList();
    }

    private static string ConnectionHeadName(IRenderedComponent<MainLayout> cut)
    {
        return cut.Find("[data-testid=connection-picker] .gdbv-picker-head .gdbv-picker-name").TextContent;
    }

    //The form's host box: its first plain text box, the picker's own name box having a test id.
    private static AngleSharp.Dom.IElement HostBox(IRenderedComponent<MainLayout> cut)
    {
        return cut.FindAll("[data-testid=connection-panel] input.form-control").First(i => !i.HasAttribute("type") && !i.HasAttribute("data-testid"));
    }

    //The title stands alone on its line and the saved connections are chosen under it (2026-11-04, the user): the strip
    //holds no picker, and the form no name box, since the chosen connection's name heads the picker.
    [Fact]
    public async Task TheTitleStandsAlone_AndThePickerUnderIt_NamesTheConnection()
    {
        var cut = RenderHome(await SavedConnections("alpha"));

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("alpha", ConnectionHeadName(cut)), TimeSpan.FromSeconds(5));

        var strip = cut.Find("[data-testid=connection-strip]");

        Assert.Contains("Connections", strip.TextContent);
        Assert.Empty(strip.QuerySelectorAll("select, input, [data-testid=connection-picker]"));
        Assert.Equal("connection-picker-row", strip.NextElementSibling.GetAttribute("data-testid"));
        Assert.DoesNotContain("Connection name", cut.Find("[data-testid=connection-panel]").TextContent);
        Assert.Empty(cut.FindAll("#savedConnection"));
    }

    //Renamed in the picker's head, a saved connection keeps its place in the list; a name another one has is refused,
    //and said so under the head.
    [Fact]
    public async Task RenamingASavedConnection_KeepsItsPlace_AndRefusesATakenName()
    {
        var storage = await SavedConnections("alpha", "beta", "gamma");
        var cut = RenderHome(storage);

        PickSavedConnection(cut, "beta");
        cut.WaitForAssertion(() => Assert.Equal("beta", ConnectionHeadName(cut)), TimeSpan.FromSeconds(5));

        cut.Click("[data-testid=connection-picker-rename]");
        cut.Change("[data-testid=connection-picker-name]", "alpha");

        Assert.Contains("already a connection called \"alpha\"", cut.Find("[data-testid=connection-picker-error]").TextContent);
        Assert.Equal(["alpha", "beta", "gamma"], await StoredConnectionNames(storage));

        cut.Change("[data-testid=connection-picker-name]", "beta 2");

        cut.WaitForAssertion(() => Assert.Equal("beta 2", ConnectionHeadName(cut)), TimeSpan.FromSeconds(5));
        Assert.Equal(["alpha", "beta 2", "gamma"], await StoredConnectionNames(storage));
        Assert.Empty(cut.FindAll("[data-testid=connection-picker-error]"));
    }

    //Duplicate puts a copy right after its original and chooses it, with the original's settings.
    [Fact]
    public async Task DuplicatingASavedConnection_ChoosesTheCopy_RightAfterIt()
    {
        var storage = await SavedConnections("alpha", "beta");
        var cut = RenderHome(storage);

        ConnectionKeys(cut);
        cut.Click("[data-testid=connection-picker-menu] [data-key='alpha'] [data-act=duplicate]");

        cut.WaitForAssertion(() => Assert.Equal("alpha (copy)", ConnectionHeadName(cut)), TimeSpan.FromSeconds(5));
        Assert.Equal(["alpha", "alpha (copy)", "beta"], await StoredConnectionNames(storage));
        Assert.Equal("alpha.test", HostBox(cut).GetAttribute("value"));
    }

    //Deleting one that is not chosen leaves the chosen one as it is; deleting the chosen one leaves its settings in the
    //form under no name, to save again or start from.
    [Fact]
    public async Task DeletingASavedConnection_LeavesTheChosenOne_UnlessItWasIt()
    {
        var storage = await SavedConnections("alpha", "beta");
        var cut = RenderHome(storage);

        ConnectionKeys(cut);
        cut.Click("[data-testid=connection-picker-menu] [data-key='beta'] [data-act=delete]");

        cut.WaitForAssertion(() => Assert.Equal(["alpha"], StoredConnectionNames(storage).Result), TimeSpan.FromSeconds(5));
        Assert.Equal("alpha", ConnectionHeadName(cut));

        cut.Click("[data-testid=connection-picker-menu] [data-key='alpha'] [data-act=delete]");

        cut.WaitForAssertion(() => Assert.Empty(StoredConnectionNames(storage).Result), TimeSpan.FromSeconds(5));
        Assert.Equal("", cut.Find("[data-testid=connection-picker-name]").GetAttribute("value") ?? "");
        Assert.Equal("alpha.test", HostBox(cut).GetAttribute("value"));
    }

    //Save, beside Connect, saves the fields to the chosen connection; a new one has to be named first, and Save says so
    //under the picker rather than saving it nameless.
    [Fact]
    public async Task Save_NeedsANameForANewConnection_ThenSavesItUnderIt()
    {
        var storage = await SavedConnections("alpha");
        var cut = RenderHome(storage);

        ConnectionKeys(cut);
        cut.Click("[data-testid=connection-picker-add]");
        cut.Click("[data-testid=connection-save]");

        Assert.Equal("Name the connection first.", cut.Find("[data-testid=connection-picker-error]").TextContent);
        Assert.Equal(["alpha"], await StoredConnectionNames(storage));

        //Nor over one saved already: New is a new one.
        cut.Change("[data-testid=connection-picker-name]", "alpha");
        cut.Click("[data-testid=connection-save]");

        Assert.Contains("already a connection called \"alpha\"", cut.Find("[data-testid=connection-picker-error]").TextContent);
        Assert.Equal("alpha.test", (await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>(ConnectionsKey))["alpha"].Hostname);

        cut.Change("[data-testid=connection-picker-name]", "delta");
        cut.Input(HostBox, "delta.test");
        cut.Click("[data-testid=connection-save]");

        cut.WaitForAssertion(() => Assert.Equal("delta", ConnectionHeadName(cut)), TimeSpan.FromSeconds(5));
        Assert.Equal(["alpha", "delta"], await StoredConnectionNames(storage));

        var saved = await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>(ConnectionsKey);

        Assert.Equal("delta.test", saved["delta"].Hostname);
    }

    //Save is orange only while there is something to save (2026-11-05, the user): a change makes it so, undoing the change or
    //saving it takes it away, and a new connection has something to save once it has a name or a change of its own.
    [Fact]
    public async Task Save_IsOrangeOnlyWhileThereIsSomethingToSave()
    {
        var storage = await SavedConnections("alpha");
        var cut = RenderHome(storage);

        ConnectionKeys(cut);

        bool Unsaved()
        {
            return cut.Find("[data-testid=connection-save]").ClassList.Contains("gdbv-btn-unsaved");
        }

        Assert.False(Unsaved());
        Assert.Contains("btn-primary", cut.Find("[data-testid=connection-save]").ClassList);

        cut.Input(HostBox, "changed.test");
        Assert.True(Unsaved());

        cut.Input(HostBox, "alpha.test");
        Assert.False(Unsaved());

        cut.Input(HostBox, "changed.test");
        cut.Click("[data-testid=connection-save]");
        cut.WaitForAssertion(() => Assert.False(Unsaved()), TimeSpan.FromSeconds(5));

        //New starts from the fields as they stand, with nothing yet to save.
        cut.Click("[data-testid=connection-picker-add]");
        Assert.False(Unsaved());

        cut.Input(HostBox, "other.test");
        Assert.True(Unsaved());

        cut.Input(HostBox, "changed.test");
        Assert.False(Unsaved());

        cut.Change("[data-testid=connection-picker-name]", "delta");
        Assert.True(Unsaved());
    }

    //A restored open connection whose saved connection was deleted meanwhile (on another device, or while its tab was closed)
    //comes back unsaved, its fields kept and the old name in the picker's head as the one typed, so Save lights and saves
    //under whatever name is in the head. Until 2026-11-07 the deleted name stayed its key: the head showed an empty box
    //that dropped what was typed into it, Save never lit, and pressing it saved the fields under the deleted name.
    [Fact]
    public async Task AnOpenConnectionWhoseSavedOneWasDeleted_ComesBackUnsaved_AndSavesUnderTheNameTyped()
    {
        var storage = await SavedConnections("other");

        await storage.SetAsync("graphdbviewer:openConnections", new List<ConnectionSnapshot>
        {
            new() { Id = "a", SavedKey = "old", Form = new("HTTP", 8182, false, "old.test", "", "", "") { DatabaseType = GraphDbProviders.TinkerPop }, Tabs = [new TabSnapshot { Id = "a1", Name = "A1" }] }
        });
        await storage.SetAsync("graphdbviewer:activeConnection", 0);

        var cut = RenderHome(storage);

        OpenConnectionDialog(cut);
        cut.WaitForAssertion(() => Assert.Equal("old.test", HostBox(cut).GetAttribute("value")), TimeSpan.FromSeconds(5));

        Assert.Equal("old", cut.Find("[data-testid=connection-picker-name]").GetAttribute("value"));
        Assert.Contains("gdbv-btn-unsaved", cut.Find("[data-testid=connection-save]").ClassList);

        cut.Change("[data-testid=connection-picker-name]", "new");
        cut.Click("[data-testid=connection-save]");

        cut.WaitForAssertion(() => Assert.Equal(["other", "new"], StoredConnectionNames(storage).Result), TimeSpan.FromSeconds(5));
        Assert.Equal("new", ConnectionHeadName(cut));
        Assert.Equal("old.test", (await storage.GetAsync<Dictionary<string, GremlinDB.GremlinConnection>>(ConnectionsKey))["new"].Hostname);
    }

    //A host that routes Bolt for the viewer: every query answers, but Memgraph's procedure list only from a Memgraph.
    private sealed class BoltHost : HttpMessageHandler
    {
        public bool Memgraph { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath.TrimStart('/') != ServerProxyGraphDb.ExecutePath)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var query = System.Text.Json.JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("query").GetString();
            var answer = """{"isError":false,"data":[]}""";

            if (query.Contains("mg.procedures") && !Memgraph)
                answer = """{"isError":true,"error":"There is no procedure with the name `mg.procedures` registered for this database instance."}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    //Connecting over Bolt says which server answered, Neo4j or Memgraph, which both speak it. Until 2026-10-31 a Memgraph
    //connected saying "Connected to Neo4j.".
    [Theory]
    [InlineData(true, "Connected to Memgraph.")]
    [InlineData(false, "Connected to Neo4j.")]
    public void ConnectingOverBolt_SaysWhichServerAnswered(bool memgraph, string said)
    {
        var http = new HttpClient(new BoltHost { Memgraph = memgraph }) { BaseAddress = new Uri("http://localhost/") };
        var cut = RenderHome(http: http, url: "http://localhost/?dbType=memgraph&host=bolt.test&port=7687&ssl=false&viaServer=true&connect=true");

        //Connected: the pill's plug is the solid one.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connection] .bi-plug-fill")), TimeSpan.FromSeconds(5));

        OpenConnectionDialog(cut);

        cut.WaitForAssertion(() => Assert.Contains(said, cut.Find("[data-testid=connection-dialog]").TextContent), TimeSpan.FromSeconds(5));
    }

    //A SPARQL endpoint that answers 503; anything else the page asks for, nothing.
    private sealed class FailingEndpoint : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath == "/ds/sparql")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down for maintenance") });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    //A link to a database that fails opens the connection dialog, where why is said beside Connect. Until 2026-11-02 the
    //failure was written into the dialog with nobody opening it, so the viewer came up and said nothing (gun-connect.spec.js
    //waited for it in vain, with a GUN peer that never answers).
    [Fact]
    public void ALinkToADatabaseThatFails_OpensTheConnectionDialogSayingWhy()
    {
        var endpoint = Uri.EscapeDataString("https://sparql.test/ds/sparql");
        var cut = RenderHome(http: new HttpClient(new FailingEndpoint()), url: $"http://localhost/?dbType=Sparql&endpoint={endpoint}&viaServer=false");

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[data-testid=connection-dialog] .text-danger"), e => e.TextContent.Contains("Connection failed: HTTP 503")), TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid=viewer-connection] .bi-plug-fill"));
    }

    //The MCP server's calls are answered by the page (2026-11-03, GDS Viewer's link): describe_tab says what it has open, the
    //query tools run on the connection Ask AI's do - and a change waits for the person's tick on the card, since a write to a
    //database cannot be taken back.
    [Fact]
    public async Task TheLinksCalls_AreAnsweredByThePage_AndAChangeWaitsForThePersonsTick()
    {
        var http = new HttpClient(new BoltHost { Memgraph = true }) { BaseAddress = new Uri("http://localhost/") };
        var cut = RenderHome(http: http, url: "http://localhost/?dbType=memgraph&host=bolt.test&port=7687&ssl=false&viaServer=true&connect=true");
        var link = Services.GetRequiredService<ShellState>().Link;

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=viewer-connection] .bi-plug-fill")), TimeSpan.FromSeconds(5));

        Assert.Equal("bolt.test:7687", link.GraphName());

        var described = await link.Answer("describe_tab", "{}", CancellationToken.None);

        Assert.False(described.IsError);
        Assert.Contains("Connected to bolt.test:7687", described.Text);
        Assert.Contains("Cypher queries", described.Text);
        Assert.Contains("allows reads only", described.Text);

        var refused = await link.Answer(QueryToolRunner.WriteToolName, """{"query": "CREATE (n)"}""", CancellationToken.None);

        Assert.True(refused.IsError);
        Assert.Contains("Changes are not allowed over this link", refused.Text);

        link.AllowChanges = true;

        var allowed = await link.Answer(QueryToolRunner.WriteToolName, """{"query": "CREATE (n)"}""", CancellationToken.None);

        Assert.DoesNotContain("Changes are not allowed", allowed.Text);
        Assert.Contains("allows changes", (await link.Answer("describe_tab", "{}", CancellationToken.None)).Text);
    }

    //Offline there is no database for the link's queries, and describe_tab says so; the page gone, nothing here answers.
    [Fact]
    public async Task OfflineTheLinkSaysThereIsNoDatabase_AndThePageGoneLetsGo()
    {
        var cut = RenderHome();
        var link = Services.GetRequiredService<ShellState>().Link;

        cut.WaitForAssertion(() => Assert.NotNull(link.Answer), TimeSpan.FromSeconds(5));

        Assert.Equal("no database (offline)", link.GraphName());
        Assert.Contains("Not connected to a database", (await link.Answer("describe_tab", "{}", CancellationToken.None)).Text);

        var refused = await link.Answer(QueryToolRunner.ReadToolName, """{"query": "g.V()"}""", CancellationToken.None);

        Assert.True(refused.IsError);
        Assert.Contains("not connected to a database that runs queries", refused.Text);
        Assert.Contains("No graph is drawn", (await link.Answer(GraphPictureRunner.LookName, "{}", CancellationToken.None)).Text);

        await DisposeComponentsAsync();

        Assert.Null(link.Answer);
        Assert.Null(link.GraphName);
    }
}
