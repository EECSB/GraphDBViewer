using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The viewer's single page. This root file holds the component lifecycle, the shell handoff (the
//toolbar contribution) and the page-shell toggles; the feature areas live in the sibling Home.*.cs partials:
//  Home.State.cs          — the query tabs + the active-tab delegating properties
//  Home.Connections.cs    — saved connections + connect / disconnect
//  Home.Persistence.cs    — localStorage (tabs, history, saved queries, editor text)
//  Home.Query.cs          — query execution (Gremlin + SPARQL) and Load DB / Schema
//  Home.Debugger.cs       — the step-through query debugger
//  Home.Styling.cs        — the Style dialog + stylesheets
//  Home.ImportExport.cs   — clipboard / file exports and pasted-graph imports
//  Home.Tabs.cs           — tab add / rename / switch / close
//  Home.GraphView.cs      — 2D/3D canvas rendering, layouts, filters, saved positions
//  Home.ElementEditing.cs — graph-click selection, property editing, staged-query commit
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

    //The handle for this page's toolbar contribution — the connection indicator, import/export and the
    //query limits, rendered by the shell's TopBar. Disposing it empties the region again.
    private IDisposable toolbarContribution;

    //The most neighbors (incident edges) a double-click node-expansion pulls in, so a high-degree node
    //doesn't flood the canvas. Persisted; set from the Settings menu. 0 or less means uncapped.
    private const int DefaultExpansionLimit = 50;
    private int expansionLimit = DefaultExpansionLimit;

    //Page-shell toggles. About, the showcase and AI settings are the shell's own dialogs now — they
    //moved into TopBar with the menu that opens them.
    private bool showConnectionCard = true;
    private bool showImportExport = true;
    private bool showNlModal;//"Ask AI" (natural-language query) popup
    private bool showKgModal;//"Generate with AI" (knowledge-graph generation) popup

    //Set when a storage write is rejected because the browser's quota is full — surfaces a dismissible
    //banner so the user knows their tabs / edits are no longer being saved (see IndexedDbAppStorage).
    private bool storageFull;

    //"X MB of Y MB used", filled in from navigator.storage.estimate() when the banner appears.
    private string storageUsage;

    //The main workspace (results / canvas / query area) stays hidden on load until the user either
    //connects or enters offline mode (the Offline mode button, or importing/pasting a graph to draw).
    private bool offlineMode;

    //The split-row element, handed to the splitter interop so it can measure the container while dragging.
    private ElementReference splitRowRef;

    //One shared .NET reference handed to the JS interops (keyboard shortcuts + graph-click callbacks),
    //created on first use and disposed with the component.
    private DotNetObjectReference<Home> selfRef;

    private DotNetObjectReference<Home> SelfRef()
    {
        selfRef ??= DotNetObjectReference.Create(this);

        return selfRef;
    }

    protected override void OnInitialized()
    {
        //Warn — rather than crash — when the browser's storage quota fills up mid-session. A full quota
        //makes every localStorage write throw QuotaExceededError; without this the first such write during
        //a render (e.g. saving the last query) takes the whole component down.
        Storage.StorageQuotaExceeded += OnStorageQuotaExceeded;

        //Hand the shell this page's third of the toolbar. Registered before the first render so the bar
        //never paints without it.
        toolbarContribution = Shell.Contribute(ViewerToolbarFragment);

        //The container below reads the shell's full-width flag, and the toggle for it lives in the bar
        //now — re-render when the shell's state changes so the class applies.
        Shell.Changed += OnShellStateChanged;
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
                storageUsage = $"{FormatMb(est.Usage)} of {FormatMb(est.Quota)} used";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch { }
    }

    private static string FormatMb(long bytes)
    {
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
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
            //seen list with what is installed, so this is only ever a list worth showing.
            await LoadTakedownsAsync();

            //Anything a development machine has filled in, added to what is saved rather than over it.
            //There is no such file anywhere else, and none of this runs when there is not one.
            await SeedDevSecretsAsync();
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

        //The toolbar contribution renders inside the shell's bar, not inside this page, so the shell
        //never sees this page render. Nudge it after every render: the fragment reads live fields (the
        //connection state, the pulse, the panel arrows), and this is what keeps the bar current.
        Shell.RefreshToolbar();
    }

    //Applies settings passed in the page URL's query string, used when the viewer is embedded (e.g. in an
    //iframe): connection details, an initial query, and the initial view mode. Runs once on first render,
    //after the persisted state has loaded, so the URL settings take precedence over the restored tab.
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
        if (settings.Query != null)
            queryText = settings.Query;

        if (settings.Language != null)
            editorLanguage = WorkspaceStore.NormalizeEditorLanguage(settings.Language);
        else if (settings.DatabaseType == "Sparql" || settings.Endpoint != null)
            editorLanguage = "sparql";

        //Flush the new view/query into the DOM before anything renders into it.
        StateHasChanged();

        //Connection details — open the connection unless connect=false.
        if (settings.HasConnection)
        {
            connection = settings.BuildConnection();
            selectedConnectionKey = null;

            if (settings.Connect != false)
                await ConnectAsync();
        }

        //Run the initial query once connected (unless run=false); otherwise just draw any restored graph.
        if (isConnected && !string.IsNullOrEmpty(queryText) && settings.AutoRun != false)
            await RunQueryAsync();
        else if (ShowGraphCanvas)
            await RenderGraphAsync();

        StateHasChanged();
    }

    #region Query limits

    //Sets the node-expansion neighbor cap (from the toolbar's Query settings), clamped to a sane range
    //and persisted.
    private async Task SetExpansionLimitAsync(int value)
    {
        expansionLimit = Math.Clamp(value, 1, 5000);
        await Storage.SetAsync(ExpansionLimitKey, expansionLimit);
    }

    //Sets the query / Load DB result limit from the toolbar's Query settings — the same per-tab value the
    //sidebar's query-limit box binds (activeTab.LoadDbLimit), so the two controls stay in sync; persisted
    //with the tab.
    private async Task SetQueryLimitAsync(int value)
    {
        loadDbLimit = Math.Max(1, value);
        await SaveTabsAsync();
    }

    #endregion


    //Blazor calls this when the component is torn down: stop any in-flight work, release the JS-held
    //.NET reference, and close the database connections.
    public async ValueTask DisposeAsync()
    {
        Storage.StorageQuotaExceeded -= OnStorageQuotaExceeded;
        Shell.Changed -= OnShellStateChanged;

        //Take this page's controls back out of the shell's bar. The handle is owner-checked, so a late
        //dispose after another page has contributed cannot blank that page's bar.
        toolbarContribution?.Dispose();
        textSaveCts?.Cancel();
        textSaveCts?.Dispose();
        queryCts?.Cancel();
        queryCts?.Dispose();
        connectCts?.Cancel();
        connectCts?.Dispose();
        selfRef?.Dispose();

        try
        {
            await DisposeDbAsync();
        }
        catch { }
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

    #endregion
}
