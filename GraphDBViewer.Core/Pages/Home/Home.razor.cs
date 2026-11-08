using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Components;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The viewer's single page. This root file holds the component lifecycle, the shell handoff (the
//toolbar contribution) and the page-shell toggles; the feature areas live in the sibling Home.*.cs partials:
//  Home.State.cs          — the query tabs + the active-tab delegating properties
//  Home.Connections.cs    — saved connections + connect / disconnect
//  Home.Persistence.cs    — the workspace store (tabs, history, saved queries, editor text)
//  Home.Query.cs          — query execution (Gremlin + SPARQL) and Load DB / Schema
//  Home.Debugger.cs       — the step-through query debugger
//  Home.Styling.cs        — the Style dialog + stylesheets
//  Home.ImportExport.cs   — clipboard / file exports and pasted-graph imports
//  Home.Tabs.cs           — tab add / rename / switch / close
//  Home.GraphView.cs      — 2D/3D canvas rendering, layouts, filters, saved positions
//  Home.ElementEditing.cs — graph-click selection, property editing, staged-query commit
//  Home.Algorithms.cs     — the Graph algorithms panel's results, drawn beside the graph
//  Home.EmbedMessages.cs  — the embedded viewer's messages with the page that embeds it (postMessage)
//  Home.Share.cs          — the Share dialog: a link to the viewer set up as this one is, or an embed's iframe
public partial class Home : IAsyncDisposable
{

    [Inject]
    private NavigationManager Nav { get; set; }

    ///<summary>
    ///Who is signed in, cascaded from the layout — null on the standalone viewer, and on the platform
    ///until the host answers. Read at connect time: routing through the host takes a permission, and
    ///a connection that would only be refused is put on the browser route instead (the connection row
    ///says why).
    ///</summary>
    [CascadingParameter]
    public AccountInfo Account { get; set; }

    ///<summary>
    ///Whether this is the viewer embedded in another site (2026-10-21, Embed.razor): the results alone, without the cards
    ///that set the viewer up, its tabs, the query side or the tools that edit the graph, showing what the address says
    ///(EmbedSettings), and reading with the token the embedding page passed (EmbedSession).
    ///</summary>
    [Parameter]
    public bool Embedded { get; set; }

    ///<summary>Whether the host would route a connection for this account. Unknown reads as yes.</summary>
    private bool MayRouteThroughHost
    {
        get
        {
            if (Account == null)
                return true;

            return Account.Can(AccountInfo.ConnectionsRoute);
        }
    }

    private const string ExpansionLimitKey = "graphdbviewer:expansionLimit";

    //Full-width belongs to the shell now (ShellState, application-shell spec); this delegates so the
    //markup keeps reading the plain name it always has. The theme moved out entirely with the top bar.
    private bool fullScreen => Shell.FullScreen;

    //Whether the workspace (the query tabs, the results and the query sidebar) is on the page: connected, drawing
    //offline, or holding a graph that was restored, imported or pasted. Without it the page shows its invitations. With
    //several connections it is always there, so the row holds each one's tab, a new one's included (the
    //multiple-connections spec, task 4).
    private bool ShowWorkspace => isConnected || offlineMode || HasGraphData || openConnections.Count > 1;

    //The most neighbors (incident edges) a double-click node-expansion pulls in, so a high-degree node
    //doesn't flood the canvas. Persisted; set in the query sidebar, beside the query limit. 0 means uncapped.
    private const int DefaultExpansionLimit = 50;
    private int expansionLimit = DefaultExpansionLimit;

    //The setup dialogs (2026-10-29; cards above the workspace before, and the viewer's toolbar contribution opened
    //them): the connection form and Import/Export. Closed until asked for; a disconnect or leaving offline mode
    //opens the first, since connecting is what comes next. About, the showcase and AI settings are the shell's own
    //dialogs — they moved into TopBar with the menu that opens them.
    private bool showConnectionCard;
    private bool showImportExport;
    private bool setupBackdropMouseDown;
    private bool showNlModal;//"Ask AI" (natural-language query, and knowledge-graph generation since 2026-11-02) popup
    private NlQueryModal askAi;
    private bool showGeocode;//"Find coordinates" (geocoding the unplaced vertices) popup

    //Set when a storage write is rejected because the browser's quota is full — surfaces a dismissible
    //banner so the user knows their tabs / edits are no longer being saved (see IndexedDbAppStorage).
    private bool storageFull;

    //"X MB of Y MB used", filled in from navigator.storage.estimate() when the banner appears.
    private string storageUsage;

    //The main workspace (results / canvas / query area) stays hidden on load until the user either
    //connects or enters offline mode (the Offline mode button, or importing/pasting a graph to draw). The connection in
    //front's.
    private bool offlineMode { get => activeConnection.OfflineMode; set => activeConnection.OfflineMode = value; }

    //The split-row element, handed to the splitter interop so it can measure the container while dragging.
    private ElementReference splitRowRef;

    //Whether the cards' split has been checked against the window since they were first on screen.
    private bool splitSettled;

    //One shared .NET reference handed to the JS interops (keyboard shortcuts + graph-click callbacks),
    //created on first use and disposed with the component.
    private DotNetObjectReference<Home> selfRef;

    private DotNetObjectReference<Home> SelfRef()
    {
        selfRef ??= DotNetObjectReference.Create(this);

        return selfRef;
    }

    ///<summary>
    ///Whether the connection and Import/Export are tabs in the workspace's row, the connection first and Import/Export after
    ///the gap, with the add-connection button beside it and Share, where the host offers it, one of the shell's bar icons
    ///(2026-11-02, the user). On every host since 2026-11-06 (the user: Treeality the same as the standalone viewer, with
    ///several connections); until then only on one without plugins, from when the tabs stood in the bar, which Treeality's
    ///page tabs hold, rather than in the workspace's row. Never for an embedded viewer, which has neither row nor bar.
    ///</summary>
    private bool SetupAsTabs => !Embedded;

    //The handle for Share among the shell's bar icons, when it is there (SetupAsTabs).
    private IDisposable toolbarContribution;

    ///<summary>Whether the standalone viewer's connection or Import/Export tab is in front, over the graph's card.</summary>
    private bool SetupTabInFront => SetupAsTabs && (showConnectionCard || showImportExport);

    ///<summary>
    ///Whether the standalone viewer's connection and Import/Export stand in the graph's card, where their tab says which is
    ///in front and a query tab closes them, so they carry no title bar or × of their own (2026-11-03, the user). Before
    ///there is a workspace there is no query tab to go back to, and the × stays.
    ///</summary>
    private bool SetupInCard => SetupAsTabs && ShowWorkspace;

    //Opens the connection dialog. On the standalone viewer it is a tab, and Import/Export's gives way to it, as one tab
    //gives way to another (2026-11-03, the user).
    private void OpenConnectionCard()
    {
        showConnectionCard = true;

        if (SetupAsTabs)
            showImportExport = false;
    }

    private void OpenImportExport()
    {
        showImportExport = true;

        if (SetupAsTabs)
            showConnectionCard = false;
    }

    //The pill and button open and close their dialogs; a tab only comes to the front, as a tab does.
    private void ToggleConnectionCard()
    {
        if (SetupAsTabs || !showConnectionCard)
            OpenConnectionCard();
        else
            showConnectionCard = false;
    }

    private void ToggleImportExport()
    {
        if (SetupAsTabs || !showImportExport)
            OpenImportExport();
        else
            showImportExport = false;
    }

    //A query tab brought to the front puts the standalone viewer's connection and Import/Export tabs behind it.
    private void CloseSetupTabs()
    {
        if (!SetupAsTabs)
            return;

        showConnectionCard = false;
        showImportExport = false;
    }

    protected override void OnInitialized()
    {
        //Warn — rather than crash — when the browser's storage quota fills up mid-session. A full quota
        //makes every localStorage write throw QuotaExceededError; without this the first such write during
        //a render (e.g. saving the last query) takes the whole component down.
        Storage.StorageQuotaExceeded += OnStorageQuotaExceeded;

        //Registered before the first render, so the bar never paints without them.
        if (SetupAsTabs && HostOptions.OffersShare)
            toolbarContribution = Shell.Contribute(null, ShareIconFragment);

        //The container below reads the shell's full-width flag, and the toggle for it lives in the bar
        //now — re-render when the shell's state changes so the class applies.
        Shell.Changed += OnShellStateChanged;

        //A tile server set or cleared in Settings changes the earth of a map or globe on screen at once.
        Shell.MapTiles.Changed += OnMapTilesChangedAsync;

        AnswerTheLink();
    }

    private void OnShellStateChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    //A persistence write hit the storage quota: raise the banner once (later failures are no-ops until dismissed).
    private void OnStorageQuotaExceeded()
    {
        if (storageFull)
            return;

        storageFull = true;
        _ = ShowStorageUsageAsync();
        InvokeAsync(StateHasChanged);
    }

    //Fills the banner with how much of the origin's storage is in use, when the browser exposes it.
    private async Task ShowStorageUsageAsync()
    {
        try
        {
            var est = await JS.InvokeAsync<StorageEstimate>("gdbvIdb.estimate");

            if (est != null && est.Quota > 0)
            {
                storageUsage = $"{ByteSizes.Human(est.Usage)} of {ByteSizes.Human(est.Quota)} used";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch { }
    }

    private void DismissStorageWarning()
    {
        storageFull = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadConnectionsAsync();

            //Whether this instance has a graph of its own to offer. One GET, no secret in the answer.
            await LoadInstanceGraphOfferAsync();

            //Plugins withdrawn from the registry that are still installed here. The server crosses the
            //seen list with what is installed, so this is only ever a list worth showing. And anything a
            //development machine has filled in, added to what is saved rather than over it: there is no such
            //file anywhere else, and none of this runs when there is not one. Neither for an embedded viewer,
            //which shows neither, and whose token reaches only the instance graph.
            if (!Embedded)
            {
                await LoadTakedownsAsync();
                await SeedDevSecretsAsync();
            }

            await LoadSavedQueriesAsync();
            await LoadHistoryAsync();
            await LoadTabsAsync();
            await LoadStylesheetsAsync();
            expansionLimit = await Storage.GetAsync<int?>(ExpansionLimitKey) ?? DefaultExpansionLimit;

            //Wire up the global keyboard shortcut listener (Delete key)
            await JS.InvokeVoidAsync("keyboardInterop.attach", SelfRef());

            StateHasChanged();

            //Redraw the restored active tab's graph now that its container is in the DOM. Gate on
            //ShowGraphCanvas (the same condition the markup uses), not HasGraphData alone — restored data
            //with the pane hidden (disconnected, no offline mode) would otherwise draw into no container.
            if (ShowGraphCanvas)
                await RenderGraphAsync();

            //Apply any URL query-string settings last, so an embed's parameters win over restored state.
            await ApplyEmbedSettingsAsync();

            //Opened to take the tour (the Plugins page's Take the tour, 2026-11-06): asked for once the page has drawn the
            //parts the tour shows, and taken off the address, so a reload does not start it again. The viewer's own stops,
            //by the way every page with a tour starts it.
            Shell.StartTourIfAsked(Nav);

            //And only then is the page around an embedded viewer told it may send: what the address named is open by
            //now, and its query run, so the first query the page sends finds the graph there.
            await StartEmbedMessagesAsync();
        }

        //Focus the tab-name input right after a double-click puts it on screen.
        if (focusTabInput)
        {
            focusTabInput = false;

            try
            {
                await tabNameInput.FocusAsync();
            }
            catch { }
        }

        //And the place search's box when its button opens it, to type in straight away.
        if (focusPlaceBox)
        {
            focusPlaceBox = false;

            try
            {
                await placeBox.FocusAsync();
            }
            catch { }
        }

        //A sidebar share stored too wide to fit, from before drags stopped short or for a wider window, is taken back
        //once the cards are first on screen (splitterInterop.settle, 2026-11-03, the user).
        if (ShowWorkspace && !splitSettled)
        {
            splitSettled = true;

            try
            {
                await JS.InvokeVoidAsync("splitterInterop.settle");
            }
            catch { }
        }

        //Share in the bar renders inside the shell's bar, not inside this page, so the shell never sees this page render.
        //Nudge it after every render: the icon reads a live field (whether the Share dialog is open), and this is what
        //keeps the bar current.
        if (toolbarContribution != null)
            Shell.RefreshToolbar();

        //The page around an embedded viewer hears of a change of selection, whatever made it.
        await TellEmbedSelectionAsync();
    }

    private async Task ApplyEmbedSettingsAsync()
    {
        EmbedSettings settings;

        try
        {
            settings = EmbedSettings.Parse(new Uri(Nav.Uri).Query);
        }
        catch
        {
            return;
        }

        if (!settings.HasAny)
            return;

        //Initial view (JSON / 2D / 3D / Table).
        if (settings.View.HasValue)
            ApplyVisualizationMode(settings.View.Value);

        //Initial query text + editor language, set before connecting so LoadQueryAsync can't overwrite it.
        //And the Query tab in front: the restored tab may have been left on Generated or Examples, and an
        //address that carries a query is opened to see it — a query that runs from an editor nobody can see
        //looks like one that never arrived (2026-09-19's walk of Customers' recall).
        if (settings.Query != null)
        {
            queryText = settings.Query;
            queryEditorTab = 1;
        }

        if (settings.Language != null)
            editorLanguage = WorkspaceStore.NormalizeEditorLanguage(settings.Language);
        else if (settings.DatabaseType == "Sparql" || settings.Endpoint != null)
            editorLanguage = "sparql";

        //Flush the new view/query into the DOM before anything renders into it.
        StateHasChanged();

        //The instance's own graph, named rather than described: how a plugin's page opens the viewer on what it
        //found. Only where it is offered to this account at all; anywhere else the query is left in the editor.
        if (settings.InstanceGraph == true && OffersInstanceGraph)
        {
            if (!instanceGraph || !isConnected)
            {
                if (isConnected)
                    Disconnect("Disconnected to open this instance's graph.");

                SelectInstanceGraph();

                if (settings.Connect != false)
                    await ConnectAsync();
            }
        }
        else if (settings.HasConnection)
        {
            //Connection details — open the connection unless connect=false.
            connection = settings.BuildConnection();
            selectedConnectionKey = null;

            //Not a saved connection: whatever password the address carries was given with it, transport and all.
            savedPassword.Remember(null);

            if (settings.Connect != false)
                await ConnectAsync();
        }
        else if (settings.InstanceGraph == true && Embedded)
            statusMessage = EmbeddedInstanceRefused;

        //Run the initial query once connected (unless run=false); otherwise just draw any restored graph.
        if (isConnected && !string.IsNullOrEmpty(queryText) && settings.AutoRun != false)
            await RunQueryAsync();
        else if (ShowGraphCanvas)
            await RenderGraphAsync();

        StateHasChanged();
    }

    #region Query limits

    //Sets the node-expansion neighbor cap (the sidebar's expand limit box), clamped to a sane range and
    //persisted. 0 is in the range, since the query builders take it as no cap: until 2026-10-31 the box
    //clamped to 1, and the uncapped expand its comment promised could not be typed.
    private async Task SetExpansionLimitAsync(int value)
    {
        expansionLimit = Math.Clamp(value, 0, 5000);
        await Storage.SetAsync(ExpansionLimitKey, expansionLimit);
    }

    private async Task OnExpansionLimitInput(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var value))
            await SetExpansionLimitAsync(value);
    }

    #endregion

    //A click that began and ended on a setup dialog's backdrop closes it; one that began inside the dialog and
    //was let go outside (a drag out of a text box) does not. The same way the Style dialog closes.
    private void CloseSetupIfBackdrop()
    {
        if (!setupBackdropMouseDown)
            return;

        showConnectionCard = false;
        showImportExport = false;
    }


    //Blazor calls this when the component is torn down: stop any in-flight work, take back what the page
    //handed the browser, release the JS-held .NET reference, and close the database connections.
    public async ValueTask DisposeAsync()
    {
        Storage.StorageQuotaExceeded -= OnStorageQuotaExceeded;
        Shell.Changed -= OnShellStateChanged;
        Shell.MapTiles.Changed -= OnMapTilesChangedAsync;
        LetGoOfTheLink();

        //Take this page's controls back out of the shell's bar. The handle is owner-checked, so a late dispose after
        //another page has contributed cannot blank that page's bar.
        toolbarContribution?.Dispose();
        textSaveCts?.Cancel();
        textSaveCts?.Dispose();
        foreach (var open in openConnections)
        {
            foreach (var tab in open.Tabs)
            {
                tab.QueryCts?.Cancel();
                tab.QueryCts?.Dispose();
            }

            open.ConnectCts?.Cancel();
            open.ConnectCts?.Dispose();
        }

        //Before the reference goes: the handlers it was handed to come off first, so none is left holding it.
        await ReleaseBrowserSideAsync();
        selfRef?.Dispose();

        //Every connection's, not only the one in front's: those behind kept their links open past the page until
        //2026-11-03 (the multiple-connections spec).
        foreach (var owner in openConnections)
        {
            try
            {
                await owner.CloseAsync();
            }
            catch { }
        }
    }

    //Takes back what this page put on the browser's side that outlives the page. The keyboard handler sits
    //on the document: left there, a Delete pressed on the next page called into the reference disposed with
    //this one. The 3D view's animation loop outlives its container (see RenderGraphAsync) and kept drawing a
    //canvas nobody could see, and the VR / AR viewer's loop does the same while it is open. The 2D view's
    //Cytoscape destroys itself only when its container is taken out of its own parent, which switching views
    //does and leaving the page does not, so it kept its frame loop, its observers and its listeners on the
    //window until the viewer was next drawn in 2D, and its interop kept this page's reference and grid mode
    //(2026-10-04). Each call is on its own, so one that fails (the browser already gone, or a prerender that
    //never had one) does not keep the others from running.
    private async Task ReleaseBrowserSideAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("keyboardInterop.detach");
        }
        catch { }

        try
        {
            await JS.InvokeVoidAsync("graph3DInterop.destroyGraph");
        }
        catch { }

        try
        {
            await JS.InvokeVoidAsync("destroyCytoscapeGraph");
        }
        catch { }

        if (xrViewerOpen)
            try
            {
                await JS.InvokeVoidAsync("xrViewerInterop.close");
            }
            catch { }

        //The embedded viewer's listener for the page around it, which holds this page's reference too.
        if (embedMessagesOn)
        {
            embedMessagesOn = false;

            try
            {
                await JS.InvokeVoidAsync("embedMessagesInterop.detach");
            }
            catch { }
        }
    }

    #region Split view

    //Starts dragging the divider between the graph view and the sidebar; the interop handles the
    //pointer tracking, the min-width clamp, and snapping back to the default ratio.
    private async Task StartResizeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.beginResize", splitRowRef);
        }
        catch { }
    }

    //Resets the split back to the default ratio (double-click the divider).
    private async Task ResetSplitAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.resetSplit");
        }
        catch { }
    }

    //The query panel's editors: their default 15em and what the handle under them adds, kept on <html> by the interop.
    private const string PaneEditorStyle = "height: calc(15em + var(--gdbv-editor-extra, 0px)); border: 1px solid var(--bs-border-color);";

    //Starts dragging the handle under the query panel; the interop tracks the pointer and sticks at the default size.
    private async Task StartEditorResizeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.beginEditorResize");
        }
        catch { }
    }

    //Puts the query panel's editors back at their default size (double-click the handle).
    private async Task ResetEditorSizeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.resetEditorSize");
        }
        catch { }
    }

    //Starts dragging the page's edge on one side ("left" or "right") toward the window's; the interop tracks the
    //pointer, keeps the other panel its width and remembers the margin in this browser.
    private async Task StartEdgeResizeAsync(string side)
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.beginEdgeResize", splitRowRef, side);
        }
        catch { }
    }

    //Puts the page's edge on one side back where the default margin has it (double-click the handle).
    private async Task ResetEdgeAsync(string side)
    {
        try
        {
            await JS.InvokeVoidAsync("splitterInterop.resetEdge", side);
        }
        catch { }
    }

    #endregion
}
