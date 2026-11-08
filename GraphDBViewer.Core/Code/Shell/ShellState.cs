using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///The application shell's own state: the theme, the full-width toggle, whether the bar stays on screen,
///and whatever the current page contributes to the toolbar.
///
///These lived as private fields on <c>Home</c>, which meant they were loaded by whichever page happened
///to be the only page — a second page would have had no theme at all. Owned here, the shell loads them
///once and every page reads the same answers (application-shell spec, phase 1).
///
///The storage keys are unchanged from when <c>Home</c> owned them, deliberately: this is a change of
///owner, not of format, and an existing workspace must come back with the theme it had.
///</summary>
public sealed class ShellState
{
    private const string DarkModeKey = "graphdbviewer:darkMode";
    private const string ThemeKey = "graphdbviewer:theme";
    private const string FullScreenKey = "graphdbviewer:fullScreen";
    private const string StickyBarKey = "graphdbviewer:stickyBar";
    //Not "graphdbviewer:tabs", which is the viewer's own query tabs (WorkspaceStore).
    private const string TabsKey = "graphdbviewer:barTabs";
    private const string NoTourPromptKey = "graphdbviewer:noTourPrompt";

    //The three there were keep their values, so a stored choice still means what it did; the blue ones (2026-10-29) say so.
    public const string ThemeDark = "dark";
    public const string ThemeDarkBlue = "dark-blue";
    public const string ThemeLight = "light";
    public const string ThemeLightTinted = "light-tinted";
    public const string ThemeLightBlue = "light-blue";
    public const string ThemeLightBlueTinted = "light-blue-tinted";

    private readonly IAppStorage storage;
    private readonly IJSRuntime js;
    private readonly string defaultTheme;
    private ContributionHandle currentContribution;

    ///<summary>The host's options say which theme someone who has chosen none starts with; without them, Dark green.</summary>
    public ShellState(IAppStorage storage, IJSRuntime js, ViewerHostOptions options = null)
    {
        this.storage = storage;
        this.js = js;
        defaultTheme = options?.DefaultTheme ?? ThemeDark;
        Theme = defaultTheme;
        MapTiles = new MapTilesPreference(storage);
        Geocoder = new GeocoderPreference(storage);
    }

    ///<summary>
    ///Which geocoder finds places from their words, set in Map settings beside the tiles (2026-11-06), and read by everything
    ///that looks one up: Find a place, Find coordinates and the address fields.
    ///</summary>
    public GeocoderPreference Geocoder { get; }

    ///<summary>
    ///Where the Map and the Globe take their earth from: the built-in images, or a tile server set in Settings (2026-11-05).
    ///On the shell, so the dialog in the bar and the viewer page that draws the earth share one and hear each other's change.
    ///</summary>
    public MapTilesPreference MapTiles { get; }

    ///<summary>
    ///Which of the six themes is on: Dark green (ThemeDark), Dark blue, Light green 1 (ThemeLight), Light green 2
    ///(tinted), Light blue 1 or Light blue 2.
    ///</summary>
    public string Theme { get; private set; }

    ///<summary>Everything that only cares whether it is dark reads this rather than the theme name.</summary>
    public bool DarkMode => Theme == ThemeDark || Theme == ThemeDarkBlue;

    ///<summary>Whether the theme's accent is the blue rather than the brand's green.</summary>
    public bool BlueAccent => Theme == ThemeDarkBlue || Theme == ThemeLightBlue || Theme == ThemeLightBlueTinted;

    ///<summary>Whether the layout fills the viewport (side margins dropped). Persisted; toggled from the top bar.</summary>
    public bool FullScreen { get; private set; }

    ///<summary>
    ///Whether the bar stays at the top of the window while the page scrolls under it. Off unless chosen, which is
    ///how the bar always behaved. Persisted; switched in Settings, under Appearance.
    ///</summary>
    public bool StickyBar { get; private set; }

    ///<summary>
    ///Whether to ask, as the app opens, if a tour of it is wanted (2026-11-03, the user): each time it opens until the
    ///question is answered with "don't show this again" ticked, which is kept. False until the choices are loaded, so a
    ///stored "no" is never asked over, and false again once answered. The bar asks only a host with a showcase.
    ///</summary>
    public bool AskTour { get; private set; }

    ///<summary>
    ///This tab's end of the MCP server's live link (2026-11-03, GDS Viewer's): on the shell, so AI settings' card and the
    ///viewer page, which answers its calls, share one - and a link outlives a page left for another and come back to.
    ///</summary>
    public LiveLink Link { get; } = new LiveLink();

    ///<summary>
    ///The plugin pages open as tabs in the bar, by route, in the order they were opened (2026-10-28): shortcuts to switch
    ///between pages without going through the Plugins page. The bar opens a page's tab when the page is visited and
    ///closes it on its ×. Persisted with the workspace, so a signed-in account's follow it to another machine; a route
    ///nothing registers any more is simply not drawn.
    ///</summary>
    public IReadOnlyList<string> Tabs => tabs;

    private List<string> tabs = new();

    ///<summary>What the current page has put in the toolbar, or null while no page has. Rendered by the shell.</summary>
    public RenderFragment ToolbarContribution { get; private set; }

    ///<summary>
    ///What the current page has put among the bar's own icons, after the full-width toggle, or null while it has put
    ///nothing there: the standalone viewer's Share (2026-11-02). Rendered by the shell, cleared with the toolbar.
    ///</summary>
    public RenderFragment ToolbarIcons { get; private set; }

    ///<summary>Raised whenever the theme, the full-width toggle, the sticky bar or the toolbar contribution changes.</summary>
    public event Action Changed;

    ///<summary>
    ///Raised when the current contribution's <em>content</em> should re-render. The fragment reads live
    ///page state — the connection indicator's host, its pulse — but it renders inside the shell's bar,
    ///which does not see the page's renders; the page nudges this after each of its own instead. A
    ///separate event from <see cref="Changed"/> on purpose: pages re-render on Changed (the full-width
    ///class), and a page raising Changed from its own after-render would loop.
    ///</summary>
    public event Action ToolbarChanged;

    ///<summary>
    ///Raised when something asks for AI settings to be opened, which the top bar holds: Ask AI's Manage models and its
    ///settings command (2026-11-01, as GDS Viewer's assistant opens its settings).
    ///</summary>
    public event Action AiSettingsRequested;

    ///<summary>Asks whoever holds AI settings to open them.</summary>
    public void RequestAiSettings()
    {
        AiSettingsRequested?.Invoke();
    }

    ///<summary>What a page's address carries when it is opened to take its tour: the Plugins page's Take the tour adds it.</summary>
    public const string TourStartQuery = "tour=start";

    ///<summary>
    ///Raised when a page asks for the guided tour, which the top bar holds, with the page's own stops, or null for the
    ///viewer's: a page opened by the Plugins page's Take the tour (2026-11-06, the user), a plugin's with stops of its own
    ///since 2026-11-07.
    ///</summary>
    public event Action<IReadOnlyList<TourStop>?> TourRequested;

    ///<summary>Asks whoever holds the guided tour to walk these stops, or the viewer's own when there are none.</summary>
    public void RequestTour(IReadOnlyList<TourStop>? stops = null)
    {
        TourRequested?.Invoke(stops);
    }

    ///<summary>
    ///Starts a page's tour when its address asks for it (<see cref="TourStartQuery"/>), and takes the asking off the
    ///address, so a reload does not start it again. The one way every page with a tour starts it from Take the tour: called
    ///once the page has drawn the parts its stops point at, with the page's stops - the viewer's Home with none, for its own.
    ///</summary>
    public void StartTourIfAsked(NavigationManager nav, IReadOnlyList<TourStop>? stops = null)
    {
        var query = new Uri(nav.Uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);

        if (!query.Contains(TourStartQuery))
            return;

        nav.NavigateTo(nav.GetUriWithQueryParameter("tour", (string?)null), replace: true);
        RequestTour(stops);
    }

    ///<summary>
    ///Loads the persisted theme, full-width and sticky-bar choices and applies the theme to the document.
    ///Called once at startup by whoever owns the shell's lifecycle — the page today, the layout once the bar moves.
    ///</summary>
    public async Task LoadAsync()
    {
        Theme = await storage.GetStringAsync(ThemeKey);

        //Nothing stored under the new key: carry across the boolean it replaced, so a choice made
        //before there were three themes survives instead of quietly reverting to the default.
        if (string.IsNullOrEmpty(Theme))
        {
            var wasDark = await storage.GetAsync<bool?>(DarkModeKey);

            if (wasDark == false)
                Theme = ThemeLight;
            else if (wasDark == true)
                Theme = ThemeDark;
            else
                Theme = defaultTheme;
        }

        FullScreen = await storage.GetAsync<bool>(FullScreenKey);
        StickyBar = await storage.GetAsync<bool>(StickyBarKey);
        AskTour = !await storage.GetAsync<bool>(NoTourPromptKey);

        //A page visited before the stored tabs arrived has opened its tab already; it stays, after the stored ones.
        var stored = await storage.GetAsync<List<string>>(TabsKey) ?? new List<string>();

        foreach (var route in tabs)
            if (!stored.Contains(route, StringComparer.OrdinalIgnoreCase))
                stored.Add(route);

        tabs = stored;

        await ApplyThemeAsync();
        RaiseChanged();
    }

    public async Task SetThemeAsync(string value)
    {
        Theme = value;
        await storage.SetStringAsync(ThemeKey, Theme);
        await ApplyThemeAsync();
        RaiseChanged();
    }

    public async Task ToggleFullScreenAsync()
    {
        FullScreen = !FullScreen;
        await storage.SetAsync(FullScreenKey, FullScreen);
        RaiseChanged();
    }

    public async Task SetStickyBarAsync(bool value)
    {
        StickyBar = value;
        await storage.SetAsync(StickyBarKey, StickyBar);
        RaiseChanged();
    }

    ///<summary>The tour question answered, either way: not asked again this time, nor ever when <paramref name="never"/>.</summary>
    public async Task AnswerTourAsync(bool never)
    {
        AskTour = false;

        if (never)
            await storage.SetAsync(NoTourPromptKey, true);

        RaiseChanged();
    }

    ///<summary>Opens a page's tab, at the end, unless it is open already.</summary>
    public async Task OpenTabAsync(string route)
    {
        if (tabs.Contains(route, StringComparer.OrdinalIgnoreCase))
            return;

        tabs.Add(route);
        await storage.SetAsync(TabsKey, tabs);
        RaiseChanged();
    }

    public async Task CloseTabAsync(string route)
    {
        if (tabs.RemoveAll(r => string.Equals(r, route, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        await storage.SetAsync(TabsKey, tabs);
        RaiseChanged();
    }

    ///<summary>
    ///Sets what the current page shows in the toolbar, and among the bar's own icons (<see cref="ToolbarIcons"/>) when it
    ///gives any. The returned handle clears both again, and the
    ///page's <c>Dispose</c> is the place to call that — it is the one thing that reliably runs on
    ///navigation, which is why this is a handle rather than a Set/Clear pair someone has to remember
    ///to balance.
    ///</summary>
    public IDisposable Contribute(RenderFragment toolbar, RenderFragment icons = null)
    {
        var handle = new ContributionHandle(this);
        currentContribution = handle;
        ToolbarContribution = toolbar;
        ToolbarIcons = icons;
        RaiseChanged();

        return handle;
    }

    ///<summary>Re-renders the current contribution. Called by the contributing page after its own renders.</summary>
    public void RefreshToolbar()
    {
        ToolbarChanged?.Invoke();
    }

    private void ClearContribution(ContributionHandle handle)
    {
        //Only the current owner clears. A page disposing late — after the next page has already
        //contributed — must not blank the bar the user is looking at.
        if (currentContribution != handle)
            return;

        currentContribution = null;
        ToolbarContribution = null;
        ToolbarIcons = null;
        RaiseChanged();
    }

    private async Task ApplyThemeAsync()
    {
        string bsTheme;
        if (DarkMode)
            bsTheme = "dark";
        else
            bsTheme = "light";

        await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-bs-theme", bsTheme);

        //Light 2 is Light 1 with a tint, so it rides on an attribute of its own rather than a third
        //value of the light/dark one, which Bootstrap owns and reads. The blue accent rides on another.
        string tint;
        if (Theme == ThemeLightTinted || Theme == ThemeLightBlueTinted)
            tint = "on";
        else
            tint = "off";

        string accent;
        if (BlueAccent)
            accent = "blue";
        else
            accent = "green";

        //The code editors watch the attributes themselves (MonacoInterop), as the canvases read them.
        await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-gdbv-tint", tint);
        await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-gdbv-accent", accent);
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
    }

    private sealed class ContributionHandle : IDisposable
    {
        private readonly ShellState owner;

        public ContributionHandle(ShellState owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            owner.ClearContribution(this);
        }
    }
}
