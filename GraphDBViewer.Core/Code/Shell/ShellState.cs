using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///The application shell's own state: the theme, the full-width toggle, and whatever the current page
///contributes to the toolbar.
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

    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";
    public const string ThemeLightTinted = "light-tinted";

    private readonly IAppStorage storage;
    private readonly IJSRuntime js;
    private ContributionHandle currentContribution;

    public ShellState(IAppStorage storage, IJSRuntime js)
    {
        this.storage = storage;
        this.js = js;
    }

    ///<summary>Which of the three themes is on: Dark, Light 1 (ThemeLight) or Light 2 (tinted).</summary>
    public string Theme { get; private set; } = ThemeDark;

    ///<summary>Everything that only cares whether it is dark reads this rather than the theme name.</summary>
    public bool DarkMode => Theme == ThemeDark;

    ///<summary>Whether the layout fills the viewport (side margins dropped). Persisted; toggled from the top bar.</summary>
    public bool FullScreen { get; private set; }

    ///<summary>What the current page has put in the toolbar, or null while no page has. Rendered by the shell.</summary>
    public RenderFragment ToolbarContribution { get; private set; }

    ///<summary>Raised whenever the theme, the full-width toggle or the toolbar contribution changes.</summary>
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
    ///Loads the persisted theme and full-width choice and applies the theme to the document. Called once
    ///at startup by whoever owns the shell's lifecycle — the page today, the layout once the bar moves.
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
            else
                Theme = ThemeDark;
        }

        FullScreen = await storage.GetAsync<bool>(FullScreenKey);

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

    ///<summary>
    ///Sets what the current page shows in the toolbar. The returned handle clears it again, and the
    ///page's <c>Dispose</c> is the place to call that — it is the one thing that reliably runs on
    ///navigation, which is why this is a handle rather than a Set/Clear pair someone has to remember
    ///to balance.
    ///</summary>
    public IDisposable Contribute(RenderFragment toolbar)
    {
        var handle = new ContributionHandle(this);
        currentContribution = handle;
        ToolbarContribution = toolbar;
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
        //value of the light/dark one, which Bootstrap owns and reads.
        string tint;
        if (Theme == ThemeLightTinted)
            tint = "on";
        else
            tint = "off";

        await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-gdbv-tint", tint);

        try
        {
            await js.InvokeVoidAsync("monacoInterop.setTheme", DarkMode);
        }
        catch { }
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
