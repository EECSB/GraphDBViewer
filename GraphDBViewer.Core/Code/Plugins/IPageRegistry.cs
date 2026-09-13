using System.Collections.Generic;
using System.Reflection;

namespace GraphDBViewerWeb.Code;

///<summary>
///One page a plugin contributes: where it lives, what to call it, and who may see it. The route has
///to exist as a <c>@page</c> component in the plugin's assembly — a registry cannot invent routes,
///it tells the router which assemblies to look in and the shell what it will find there.
///</summary>
public sealed class PluginPage
{
    ///<summary>The route the page owns, as its <c>@page</c> directive declares it.</summary>
    public string Route { get; init; }

    ///<summary>What to call the page wherever it is listed or linked.</summary>
    public string Title { get; init; }

    ///<summary>An image URL the shell may show beside the title, or null for none.</summary>
    public string Icon { get; init; }

    ///<summary>
    ///The permission required to see the page, or null when being admitted is enough. A courtesy
    ///gate — the client hides what it names, and the server refuses independently wherever the page
    ///has endpoints, because a WebAssembly build is a file the user can read and edit.
    ///</summary>
    public string Permission { get; init; }
}

///<summary>
///A plugin as the registry knows it: its identity, the assembly its pages compile into, and the pages
///themselves. Bundled and installed plugins both take this shape — that is the point of the seam.
///</summary>
public sealed class RegisteredPlugin
{
    ///<summary>The plugin's id — its identity (ADR 0012). Reverse-DNS for third parties.</summary>
    public string Id { get; init; }

    public string Name { get; init; }

    public string Version { get; init; }

    ///<summary>
    ///Whether an administrator may uninstall it. False only for the viewer, which owns <c>/</c> and
    ///is the one plugin the app cannot be without (plugin-system criterion 9).
    ///</summary>
    public bool Removable { get; init; }

    ///<summary>The assembly holding the plugin's routable pages, handed to the router.</summary>
    public Assembly Assembly { get; init; }

    public IReadOnlyList<PluginPage> Pages { get; init; } = [];

    ///<summary>
    ///True for a bundled plugin an administrator removed: it ships in the image, so "uninstalled"
    ///means its module was not run — the composition root registers this metadata row instead, which
    ///is what lets the Plugins page still show it and offer Restore.
    ///</summary>
    public bool Disabled { get; init; }

    ///<summary>
    ///What the uninstall prompt must additionally say for this plugin, or null. The provisioning
    ///plugin uses it for ADR 0011's rule 3: removing it leaves every container it started running.
    ///</summary>
    public string UninstallNote { get; init; }
}

///<summary>
///What plugins register their pages into, and what the router and the Plugins page read back
///(plugin-system phase 1). A mutable registry rather than DI because Blazor WebAssembly seals its
///container at <c>Build()</c> — everything contributed after that goes through registries the host
///reads live (plugin-system design, "Registries, not DI").
///</summary>
public interface IPageRegistry
{
    ///<summary>Adds a plugin and its pages. A second plugin claiming a registered id is refused.</summary>
    void Register(RegisteredPlugin plugin);

    ///<summary>Every registered plugin, in registration order.</summary>
    IReadOnlyList<RegisteredPlugin> Plugins { get; }

    ///<summary>
    ///The assemblies the router scans for routes — every registered plugin's, once. This is what
    ///feeds <c>Router.AdditionalAssemblies</c>, so a plugin that is not registered has no routes,
    ///the viewer included.
    ///</summary>
    IEnumerable<Assembly> RoutableAssemblies { get; }
}
