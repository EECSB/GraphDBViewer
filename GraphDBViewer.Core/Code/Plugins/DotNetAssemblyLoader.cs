using System.IO;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace GraphDBViewerWeb.Code;

///<summary>
///Loads a .NET plugin from its bytes and puts it through the seam (plugin-system phase 3): find the
///module type the manifest's <c>entry</c> names, construct it, run <c>ConfigureServices</c>, and take
///its page registrations — <em>verified against the manifest</em>, because the module is the plugin's
///own code and the manifest is what the administrator approved. Everything loads into the default
///<see cref="AssemblyLoadContext"/>: .NET in the browser cannot unload an assembly, so removal
///completes on reload, and accepting that removes a whole class of problem rather than creating one.
///</summary>
public static class DotNetAssemblyLoader
{
    ///<summary>Splits an entry like <c>Example.Erp.dll!Example.Erp.Module</c> into its two halves.</summary>
    public static (string AssemblyFile, string TypeName) ParseEntry(string entry)
    {
        var parts = (entry ?? "").Split('!');

        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new PluginLoadException($@"The entry ""{entry}"" is not the expected ""Assembly.dll!Full.Type.Name"" shape.");

        return (parts[0].Trim(), parts[1].Trim());
    }

    ///<summary>
    ///Loads the assembly and constructs the module the entry names. Every failure is a
    ///<see cref="PluginLoadException"/> whose message can face the administrator.
    ///</summary>
    public static IPluginModule LoadModule(byte[] assembly, string entry)
    {
        var (_, typeName) = ParseEntry(entry);

        System.Reflection.Assembly loaded;

        try
        {
            using var stream = new MemoryStream(assembly);

            loaded = AssemblyLoadContext.Default.LoadFromStream(stream);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($"The assembly could not be loaded: {ex.Message}");
        }

        var type = loaded.GetType(typeName, throwOnError: false);

        if (type == null)
            throw new PluginLoadException($@"The assembly holds no type named ""{typeName}"" — check the manifest's entry.");

        object module;

        try
        {
            module = Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($@"Constructing ""{typeName}"" failed: {ex.Message}");
        }

        if (module is not IPluginModule pluginModule)
            throw new PluginLoadException($@"""{typeName}"" does not implement IPluginModule, which is the one thing an entry type must do.");

        return pluginModule;
    }

    ///<summary>
    ///Runs the module through the seam and holds it to its manifest: services registered, pages
    ///taken — and then the registrations are checked. The module is the plugin's own code; the
    ///manifest is what the administrator saw and approved. A module registering under a different id
    ///is impersonation (ADR 0012), and a route the manifest never declared is a page nobody agreed
    ///to, so either refuses the whole load.
    ///</summary>
    public static void RegisterVerified(IPluginModule module, PluginManifest manifest, IServiceCollection services, IPageRegistry pages)
    {
        try
        {
            module.ConfigureServices(services);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($"Its ConfigureServices threw: {ex.Message}");
        }

        var before = pages.Plugins.Count;

        try
        {
            module.Register(pages);
        }
        catch (Exception ex)
        {
            throw new PluginLoadException($"Its Register threw: {ex.Message}");
        }

        var added = pages.Plugins.Count - before;

        if (added != 1)
            throw new PluginLoadException($"The module registered {added} plugins; a module registers exactly itself, once.");

        var registered = pages.Plugins[before];

        if (registered.Id != manifest.Id)
            throw new PluginLoadException($@"The module registered the id ""{registered.Id}"" but its manifest says ""{manifest.Id}"" — a plugin is its manifest's id, nothing else.");

        foreach (var page in registered.Pages)
        {
            if (!(manifest.Routes ?? []).Contains(page.Route, StringComparer.Ordinal))
                throw new PluginLoadException($@"The module registered the route ""{page.Route}"", which its manifest never declared. Declared routes: {string.Join(", ", manifest.Routes ?? [])}.");
        }
    }

    ///<summary>
    ///Load-to-prove, for install time (task 13): the host is published trimmed and a plugin is not,
    ///so a plugin can reference a method trimming removed — and finding that out during install is
    ///recoverable, while finding out at the next boot is the quarantine doing damage control. Runs
    ///the whole load against throwaway registries and answers with the refusal, or null when the
    ///plugin proved out. The assembly stays loaded — nothing unloads in the browser — which is fine:
    ///activation is by full reload, and nothing consults it until then.
    ///</summary>
    public static string Prove(byte[] assembly, PluginManifest manifest)
    {
        try
        {
            var module = LoadModule(assembly, manifest.Entry);

            RegisterVerified(module, manifest, new ServiceCollection(), new PageRegistry());

            return null;
        }
        catch (PluginLoadException ex)
        {
            return ex.Message;
        }
    }
}

///<summary>Why a plugin did not load, worded for the administrator who has to decide what to do.</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message) : base(message)
    {
    }
}
