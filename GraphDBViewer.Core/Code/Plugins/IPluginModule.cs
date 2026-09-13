using Microsoft.Extensions.DependencyInjection;

namespace GraphDBViewerWeb.Code;

///<summary>
///What a plugin implements to become part of the app: one entry point that registers everything the
///plugin contributes. A bundled plugin's module is constructed by the composition root directly (it
///is a build-time reference); an installed one's is found by reflection from its manifest's
///<c>entry</c> and called the same way (plugin-system phase 3). One seam, both kinds — the viewer
///goes through it too, which is what proves it carries anything.
///</summary>
public interface IPluginModule
{
    ///<summary>
    ///Registers the plugin's services. Called <em>before</em> <c>Build()</c>, because Blazor
    ///WebAssembly seals its container there and supports no late registration — which is the
    ///boot-order constraint the whole loading design bends around (ADR 0006).
    ///</summary>
    void ConfigureServices(IServiceCollection services);

    ///<summary>Registers the plugin's pages.</summary>
    void Register(IPageRegistry pages);
}
