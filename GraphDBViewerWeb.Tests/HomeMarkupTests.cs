using Bunit;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;
using GraphDBViewerWeb.Pages;
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

    private IRenderedComponent<Home> RenderHome(IAppStorage storage = null)
    {
        Services.AddSingleton<IAppStorage>(storage ?? new NullStorage());
        Services.AddSingleton(new HttpClient());
        Services.AddScoped<WorkspaceStore>();
        Services.AddScoped<LlmConnectionStore>();
        Services.AddSingleton(new ViewerHostOptions { AppName = "Treeality", HasServerRoute = true });

        //Home drives Monaco and Cytoscape on render. Loose mode returns default for anything unconfigured
        //rather than throwing, which is all these need — the assertions are about markup, not interop.
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Home>();
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
    ///The wait watches the card closing, which is the observable end of the transition. The toggle whose
    ///text is exactly "Offline mode" lives in the connection card; the top bar's copy of the label
    ///carries an arrow beside it, so it never matches this and the two cannot be confused.
    ///</summary>
    private void EnterOfflineMode(IRenderedComponent<Home> cut)
    {
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Offline mode").Click();

        //Five seconds rather than bUnit's one. The machine this guards against is a loaded two-core CI
        //runner, so the default is measured against exactly the conditions that are not the problem here.
        cut.WaitForAssertion(
            () => Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Offline mode"),
            TimeSpan.FromSeconds(5));
    }

    private List<string> OptionValues(IRenderedComponent<Home> cut)
    {
        return cut.FindAll("option").Select(o => o.GetAttribute("value")).ToList();
    }

    private void OpenImportPanel(IRenderedComponent<Home> cut)
    {
        //The panel is open at boot (showImportExport starts true), so an unconditional click would
        //toggle it shut — the e2e helper's retry loop hides the same trap. Click only when it's closed.
        if (cut.FindAll("textarea[placeholder^='GraphSON']").Count == 0)
            cut.FindAll("button").First(b => b.TextContent.Contains("Import / Export")).Click();
    }

    private void PasteAndGenerate(IRenderedComponent<Home> cut, string dot)
    {
        cut.Find("textarea[placeholder^='GraphSON']").Input(dot);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Generate queries").Click();
    }

    //The staged Generated queries, read off the Generated tab's Monaco component — the markup itself
    //doesn't carry the text (Monaco draws it via JS), but the component's Value parameter is the buffer.
    private string GeneratedBuffer(IRenderedComponent<Home> cut)
    {
        return cut.FindComponent<MonacoEditor>().Instance.Value;
    }

    //RunExportChoiceAsync branches on exportChoice == "gremlin". When the option offered "db" instead, the
    //branch was simply unreachable and every export fell through to JSON, under the wrong filename.
    [Fact]
    public void GraphExport_OffersTheValueRunExportChoiceActuallyReads()
    {
        var values = OptionValues(RenderHome());

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
    //connection card, and StartBlankCanvasAsync closes that card — leaving the top bar claiming
    //"Disconnected" with no visible way back out. The top bar now names the mode and reopens the card,
    //which is the path back; this walks the whole round trip that used to dead-end.
    [Fact]
    public void OfflineMode_ExitStaysReachableThroughTheTopBar()
    {
        var cut = RenderHome();

        EnterOfflineMode(cut);

        //The card closed with entry, taking the toggle with it — the top bar carries the state instead.
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode");
        cut.FindAll("button").First(b => b.TextContent.Contains("Offline mode")).Click();

        //Reopening the card is the top bar's whole job here, so wait for the toggle it brings back rather
        //than reaching for it and blaming the button when it is not there yet.
        cut.WaitForAssertion(
            () => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode"),
            TimeSpan.FromSeconds(5));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Exit offline mode").Click();

        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Disconnected"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Exit offline mode");
    }

    //The DatabaseType buttons write literals that GraphDbProviders.For reads back — and For(unknown)
    //silently falls back to TinkerPop, so a drifted value wouldn't error, it would quietly change the
    //form shape and the capability gates. The form shape is the observable end of that chain.
    [Fact]
    public void SparqlButton_SwapsTheFormToAnEndpointUrl()
    {
        var cut = RenderHome();

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

    //The ✨ entry point lives in the Import panel and opens the knowledge-graph modal.
    [Fact]
    public void GenerateWithAi_OpensTheKgModal()
    {
        var cut = RenderHome();

        OpenImportPanel(cut);
        cut.FindAll("button").First(b => b.TextContent.Contains("Generate with AI")).Click();

        Assert.Contains("Generate a knowledge graph", cut.Markup);
    }

    //AC 12's worry — the Import panel closing under an in-flight generation — is designed out by the
    //modal: it isn't the panel's child, so the panel closing doesn't touch it. In the browser the
    //backdrop even blocks every user path to close the panel mid-generation (the e2e spec notes this);
    //bUnit dispatches the occluded click anyway, so this is where the survival property is provable.
    [Fact]
    public void KgModal_SurvivesTheImportPanelClosing()
    {
        var cut = RenderHome();

        OpenImportPanel(cut);
        cut.FindAll("button").First(b => b.TextContent.Contains("Generate with AI")).Click();

        //Close the panel underneath the open modal.
        cut.FindAll("button").First(b => b.TextContent.Contains("Import / Export")).Click();

        Assert.Empty(cut.FindAll("textarea[placeholder^='GraphSON']"));
        Assert.Contains("Generate a knowledge graph", cut.Markup);
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

        cut.Find(".alert-warning .btn-close").Click();

        Assert.DoesNotContain("Browser storage is full", cut.Markup);
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

        cut.FindAll("button").First(b => b.TextContent.Contains("Commit Changes")).Click();

        Assert.Contains("No active connection: connect to a database first.", cut.Markup);
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
    private static void ClickDatabaseType(IRenderedComponent<Home> cut, string name)
    {
        cut.Find("[aria-label=\"Database type\"]")
            .QuerySelectorAll("button")
            .First(b => b.TextContent.Contains(name))
            .Click();
    }

    private IRenderedComponent<Home> RenderHomeWithQueryPanel(string databaseButton = null)
    {
        var cut = RenderHome();

        if (databaseButton != null)
            ClickDatabaseType(cut, databaseButton);

        EnterOfflineMode(cut);

        return cut;
    }

    //GUN has no query language, so an editor, a language picker and an Examples tab would all be inviting
    //a query it cannot answer. The form that replaces them is the observable end of NoQueryLanguage.
    [Fact]
    public void Gun_ReplacesTheEditorWithAForm()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        Assert.Contains(cut.FindAll("label"), l => l.TextContent.Trim() == "Start from key");
        Assert.Empty(cut.FindAll("select[title='Editor syntax highlighting']"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Examples");

        //Switching back brings all three straight back — this is per-engine, not a mode the viewer enters.
        //(Entering offline mode closed the connection card; the top-bar button reopens it.)
        cut.FindAll("button").First(b => b.TextContent.Contains("Offline mode")).Click();
        ClickDatabaseType(cut, "Apache TinkerPop");

        Assert.DoesNotContain(cut.FindAll("label"), l => l.TextContent.Trim() == "Start from key");
        Assert.NotEmpty(cut.FindAll("select[title='Editor syntax highlighting']"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Examples");
    }

    //GUN's Generated tab holds the JavaScript the viewer will run — the read the form describes while
    //nothing is staged, and the writes themselves once something is.
    [Fact]
    public async Task GunGeneratedTab_ShowsTheJavaScriptThatWillRun()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        //Each change re-renders the form, so find and trigger in one go — otherwise the second element
        //comes from the tree the first change replaced.
        await cut.InvokeAsync(() => cut.Find("input#gunStartKey").Change("users"));
        await cut.InvokeAsync(() => cut.Find("input#gunMapChildren").Change(true));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Generated").Click();

        //Monaco draws the text itself, so what's pinned here is the value handed to it. Waited for rather
        //than read on the spot: the buffer reaches the editor through a render the click only schedules,
        //so reading immediately passes on a warm run and fails on a slow one — which is exactly how this
        //test behaved, roughly half the time, whenever the suite ran straight after a build.
        cut.WaitForAssertion(() => Assert.Contains(cut.FindComponents<MonacoEditor>(),
            e => (e.Instance.Value ?? "").Contains("gun.get('users').map().once(")));
    }

    //Switching database types has to leave the port pointing at the engine you switched *to*. Each
    //handler used to carry a list of every other engine's port to tell a leftover default from a chosen
    //one, so an engine added later was invisible to the handlers written before it.
    [Fact]
    public void SwitchingEngines_CarriesThePortOver()
    {
        var cut = RenderHome();

        var port = () => cut.Find("input[type=number]").GetAttribute("value");

        ClickDatabaseType(cut, "ArangoDB");
        Assert.Equal("8529", port());

        //This was the break: 8529 was not in Bolt's list, so it stayed and Neo4j dialled ArangoDB's port.
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
        cut.Find("input[type=number]").Change("7688");

        ClickDatabaseType(cut, "ArangoDB");

        Assert.Equal("7688", cut.Find("input[type=number]").GetAttribute("value"));
    }

    //The Live toggle only exists where an engine can push — GUN, and nothing else.
    [Fact]
    public void LiveToggle_ShowsOnlyForAnEngineThatKeepsAnswering()
    {
        var cut = RenderHomeWithQueryPanel("GUN");

        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Live");

        //Back to an engine where a result is final, and it goes.
        cut.FindAll("button").First(b => b.TextContent.Contains("Offline mode")).Click();
        ClickDatabaseType(cut, "Apache TinkerPop");

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Live");
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
        Assert.Equal("Enter the SPARQL endpoint URL first", connect.ParentElement.GetAttribute("title"));
    }

    //TinkerPop needs nothing typed to attempt a connection, so there is nothing to explain and no
    //tooltip to leave hanging over an enabled button.
    [Fact]
    public void ConnectEnabled_ExplainsNothing()
    {
        var cut = RenderHome();

        var connect = cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect");

        Assert.False(connect.HasAttribute("disabled"));
        Assert.True(string.IsNullOrEmpty(connect.ParentElement.GetAttribute("title")));
    }
}
