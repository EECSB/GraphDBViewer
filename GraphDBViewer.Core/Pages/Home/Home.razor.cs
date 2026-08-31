using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The viewer's single page. This root file holds the component lifecycle, the theme / layout
//preferences and the page-shell toggles; the feature areas live in the sibling Home.*.cs partials:
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

    private const string DarkModeKey = "graphdbviewer:darkMode";
    private const string ThemeKey = "graphdbviewer:theme";

    private const string ThemeDark = "dark";
    private const string ThemeLight = "light";
    private const string ThemeLightTinted = "light-tinted";
    private const string FullScreenKey = "graphdbviewer:fullScreen";
    private const string ExpansionLimitKey = "graphdbviewer:expansionLimit";

    //Theme
    ///<summary>Which of the three themes is on: Dark, Light 1 (ThemeLight) or Light 2 (tinted).</summary>
    private string theme = ThemeDark;

    ///<summary>Everything that only cares whether it is dark reads this rather than the theme name.</summary>
    private bool darkMode
    {
        get
        {
            return theme == ThemeDark;
        }
    }

    //Whether the layout fills the viewport (side margins dropped). Persisted; toggled from the top bar.
    private bool fullScreen;

    //The most neighbors (incident edges) a double-click node-expansion pulls in, so a high-degree node
    //doesn't flood the canvas. Persisted; set from the Settings menu. 0 or less means uncapped.
    private const int DefaultExpansionLimit = 50;
    private int expansionLimit = DefaultExpansionLimit;

    //Page-shell toggles.
    private bool showConnectionCard = true;
    private bool showImportExport = true;
    private bool showAbout;
    private bool showShowcase;//full-screen showcase (the landing page) overlay, when the host bundles one
    private bool showLlmSettings;//the AI models dialog, opened from the settings menu
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

    ///<summary>
    ///Who is signed in, for the top bar's account menu. Null until the host answers, and left null when it
    ///has no accounts at all — the menu is then absent rather than empty.
    ///</summary>
    private AccountInfo account;

    protected override void OnInitialized()
    {
        //Warn — rather than crash — when the browser's storage quota fills up mid-session. A full quota
        //makes every localStorage write throw QuotaExceededError; without this the first such write during
        //a render (e.g. saving the last query) takes the whole component down.
        Storage.StorageQuotaExceeded += OnStorageQuotaExceeded;
    }

    //Asks the host who this browser is signed in as. The viewer runs in WebAssembly and the sign-in is a
    //cookie the *server* reads, so it cannot be worked out here — it has to be asked for. A deployment
    //running without accounts does not serve this at all, and the menu simply never appears.
    private async Task LoadAccountAsync()
    {
        try
        {
            var response = await Http.GetAsync(AccountInfo.Path);

            if (!response.IsSuccessStatusCode)
                return;

            account = await response.Content.ReadFromJsonAsync<AccountInfo>();
        }
        catch
        {
            //No accounts, or the host cannot be reached. Either way there is no menu to show, and the
            //viewer works the same — everything else it needs is already in the browser.
        }
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

            //Anything a development machine has filled in, added to what is saved rather than over it.
            //There is no such file anywhere else, and none of this runs when there is not one.
            await SeedDevSecretsAsync();
            await LoadSavedQueriesAsync();
            await LoadHistoryAsync();
            await LoadTabsAsync();
            await LoadStylesheetsAsync();
            await LoadThemeAsync();
            fullScreen = await Storage.GetAsync<bool>(FullScreenKey);
            expansionLimit = await Storage.GetAsync<int?>(ExpansionLimitKey) ?? DefaultExpansionLimit;

            //Awaited, in sequence with the rest of startup, and it has to be: fired and forgotten it
            //re-renders at an unpredictable moment shortly after boot, and one that lands while the canvas
            //is being set up leaves a graph that draws but does not respond. It is a same-origin request
            //that has usually already resolved by the time the restored workspace has finished drawing.
            await LoadAccountAsync();

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
            visualizationMode = settings.View.Value;

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

    #region Theme & layout preferences

    private async Task LoadThemeAsync()
    {
        theme = await Storage.GetStringAsync(ThemeKey);

        //Nothing stored under the new key: carry across the boolean it replaced, so a choice made
        //before there were three themes survives instead of quietly reverting to the default.
        if (string.IsNullOrEmpty(theme))
        {
            var wasDark = await Storage.GetAsync<bool?>(DarkModeKey);

            if (wasDark == false)
                theme = ThemeLight;
            else
                theme = ThemeDark;
        }

        await ApplyThemeAsync();
    }

    private async Task SetThemeAsync(string value)
    {
        theme = value;
        await Storage.SetStringAsync(ThemeKey, theme);
        await ApplyThemeAsync();
    }

    private async Task ToggleFullScreenAsync()
    {
        fullScreen = !fullScreen;
        await Storage.SetAsync(FullScreenKey, fullScreen);
    }

    //Opening the showcase from the About dialog closes About first, so the two overlays do not stack.
    private void OpenShowcaseFromAbout()
    {
        showAbout = false;
        showShowcase = true;
    }

    //Sets the node-expansion neighbor cap (from the Settings menu), clamped to a sane range and persisted.
    private async Task SetExpansionLimitAsync(int value)
    {
        expansionLimit = Math.Clamp(value, 1, 5000);
        await Storage.SetAsync(ExpansionLimitKey, expansionLimit);
    }

    //Sets the query / Load DB result limit from the Settings menu — the same per-tab value the sidebar's
    //query-limit box binds (activeTab.LoadDbLimit), so the two controls stay in sync; persisted with the tab.
    private async Task SetQueryLimitAsync(int value)
    {
        loadDbLimit = Math.Max(1, value);
        await SaveTabsAsync();
    }

    private async Task ApplyThemeAsync()
    {
        string bsTheme;
        if (darkMode)
            bsTheme = "dark";
        else
            bsTheme = "light";

        await JS.InvokeVoidAsync("document.documentElement.setAttribute", "data-bs-theme", bsTheme);

        //Light 2 is Light 1 with a tint, so it rides on an attribute of its own rather than a third
        //value of the light/dark one, which Bootstrap owns and reads.
        string tint;
        if (theme == ThemeLightTinted)
            tint = "on";
        else
            tint = "off";

        await JS.InvokeVoidAsync("document.documentElement.setAttribute", "data-gdbv-tint", tint);

        try
        {
            await JS.InvokeVoidAsync("monacoInterop.setTheme", darkMode);
        }
        catch { }
    }

    #endregion


    //Blazor calls this when the component is torn down: stop any in-flight work, release the JS-held
    //.NET reference, and close the database connections.
    public async ValueTask DisposeAsync()
    {
        Storage.StorageQuotaExceeded -= OnStorageQuotaExceeded;
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
