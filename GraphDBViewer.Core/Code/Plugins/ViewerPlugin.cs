namespace GraphDBViewerWeb.Code;

///<summary>
///The graph viewer, as the bundled plugin it now is (plugin-system phase 1, task 5): it declares its
///route through the same seam a third-party page will use, and the router only knows <c>/</c>
///because this ran. Nothing about how the viewer works changes, and its pages stay where they are; it
///puts nothing in the shell's bar (<see cref="ShellState.ToolbarContribution"/>), its controls being in
///its own workspace since 2026-10-29. Not removable, deliberately: it owns <c>/</c>, which embeds depend on
///(plugin-system criterion 9).
///</summary>
public sealed class ViewerPlugin : IPluginModule
{
    public const string PluginId = "treeality.viewer";

    ///<summary>
    ///The viewer's feature showcase, which the core carries for every host that serves it (2026-11-06), as the standalone
    ///web edition shows it from its own menu: the standalone app's page, its live demo and how to run it included.
    ///</summary>
    public const string ShowcasePath = "_content/GraphDBViewer.Core/showcase/index.html";

    ///<summary>
    ///The same page as a plugin's Features shows it, inside another app (2026-11-07): told by its address to leave out the
    ///standalone app's own words - the live demo, that it needs no backend, how to clone and run it - for ones that fit.
    ///</summary>
    public const string PluginShowcasePath = $"{ShowcasePath}?shown=plugin";

    public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        //Nothing: the viewer's services are the host's own registrations, made where they always
        //were. A third-party module is what this method exists for.
    }

    public void Register(IPageRegistry pages)
    {
        //The app carries no version of its own yet, so the assembly's is the honest stand-in.
        var assemblyVersion = typeof(ViewerPlugin).Assembly.GetName().Version;

        var version = "1.0.0";

        if (assemblyVersion != null)
            version = assemblyVersion.ToString(3);

        pages.Register(new RegisteredPlugin
        {
            Id = PluginId,
            Name = "Graph viewer",
            Version = version,
            Removable = false,
            Assembly = typeof(GraphDBViewerWeb.Pages.Home).Assembly,
            Pages =
            [
                new PluginPage { Route = "/", Title = "Graph viewer", Icon = "_content/GraphDBViewer.Core/graph-viewer-icon.svg", Permission = null }
            ],
            ShowcaseUrl = PluginShowcasePath,
            TourRoute = "/"
        });
    }
}
