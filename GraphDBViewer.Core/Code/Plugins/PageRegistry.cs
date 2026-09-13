using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GraphDBViewerWeb.Code;

///<summary>
///The one page registry an app has. Populated at startup — by hand for bundled plugins, and by the
///runtime loader once installed ones exist (plugin-system phase 3) — and read live from then on.
///Registration is startup work on the single WebAssembly thread, so this holds a plain list.
///</summary>
public sealed class PageRegistry : IPageRegistry
{
    private readonly List<RegisteredPlugin> _plugins = new();

    public IReadOnlyList<RegisteredPlugin> Plugins => _plugins;

    public IEnumerable<Assembly> RoutableAssemblies => _plugins.Select(plugin => plugin.Assembly).Where(assembly => assembly != null).Distinct();

    public void Register(RegisteredPlugin plugin)
    {
        //An id is a plugin's identity (ADR 0012): a second claimant is refused rather than merged,
        //because whichever of the two the reader got would be the wrong answer half the time.
        if (_plugins.Any(existing => existing.Id == plugin.Id))
            throw new InvalidOperationException($@"Two plugins claim the id ""{plugin.Id}"". An id is a plugin's identity, so the second registration is refused.");

        _plugins.Add(plugin);
    }
}
