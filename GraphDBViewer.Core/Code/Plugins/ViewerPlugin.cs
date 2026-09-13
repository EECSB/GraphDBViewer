namespace GraphDBViewerWeb.Code;

///<summary>
///The graph viewer, as the bundled plugin it now is (plugin-system phase 1, task 5): it declares its
///route through the same seam a third-party page will use, and the router only knows <c>/</c>
///because this ran. Nothing about how the viewer works changes — its toolbar contribution already
///goes through <see cref="ShellState.ToolbarContribution"/> like any page's, and its pages stay
///where they are. Not removable, deliberately: it owns <c>/</c>, which embeds depend on
///(plugin-system criterion 9).
///</summary>
public sealed class ViewerPlugin : IPluginModule
{
    public const string PluginId = "treeality.viewer";

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
                new PluginPage { Route = "/", Title = "Graph viewer", Icon = "favicon.svg", Permission = null }
            ]
        });
    }
}
