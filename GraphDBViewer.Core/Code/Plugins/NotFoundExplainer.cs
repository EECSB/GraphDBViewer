namespace GraphDBViewerWeb.Code;

///<summary>
///What the not-found page says, and why. "Does not exist" is false for a route whose plugin is
///installed and did not load — a bookmark on the morning a plugin was quarantined, or a boot whose
///list fetch timed out. The manifests declare their routes without the plugin loading, the boot report
///knows why one did not, and this joins the two (ledger items 19 and 29, 2026-09-05). Pure, so it is
///tested without rendering anything.
///</summary>
public static class NotFoundExplainer
{
    ///<summary>A headline, a sentence, and the id of the plugin the path belongs to — null when none does.</summary>
    public sealed record Explanation(string Headline, string Detail, string PluginId);

    public static readonly Explanation Generic = new("Not Found", "Sorry, the content you are looking for does not exist.", null);

    public static Explanation Explain(string path, InstalledPlugins installed, PluginBootReport report)
    {
        var wanted = "/" + (path ?? "").Trim('/');
        var owner = OwnerOf(wanted, installed);

        if (owner == null)
        {
            if (report != null && report.ListUnavailable)
                return new Explanation("Nothing loaded", "The list of installed plugins could not be fetched when this session started, so no plugin's pages are available. Reload to try again.", null);

            return Generic;
        }

        var id = owner.Manifest.Id;
        var name = owner.Manifest.Name;

        if (string.IsNullOrWhiteSpace(name))
            name = id;

        var entry = report?.For(id);

        if (entry == null)
        {
            if (report != null && report.ListUnavailable)
                return new Explanation($"This is {name}'s page", "The list of installed plugins could not be fetched when this session started, so nothing loaded. Reload to try again.", id);

            return new Explanation($"This is {name}'s page", $"{name} was installed after this session started. Reload to load it.", id);
        }

        if (entry.Outcome == PluginBootOutcome.Loaded)
            return new Explanation($"This is {name}'s page", $"{name} is loaded, but has no page at this address.", id);

        if (entry.Outcome == PluginBootOutcome.Disabled)
            return new Explanation($"This is {name}'s page", $"{name} is installed but disabled. Restore it on the Plugins page.", id);

        if (entry.Outcome == PluginBootOutcome.SafeMode)
            return new Explanation($"This is {name}'s page", $"{name} was not loaded: this session runs in safe mode. Reload without ?safemode=1.", id);

        if (entry.Outcome == PluginBootOutcome.Quarantined)
            return new Explanation($"This is {name}'s page", $"{name} was quarantined at boot — {entry.Detail} The Plugins page can clear the mark.", id);

        return new Explanation($"This is {name}'s page", $"{name} failed to load at this boot — {entry.Detail} It will be skipped next boot until the mark is cleared.", id);
    }

    ///<summary>The installed record whose manifest declares a route the path is, or sits beneath.</summary>
    private static InstalledPluginRecord OwnerOf(string path, InstalledPlugins installed)
    {
        if (installed == null)
            return null;

        foreach (var record in installed.Records)
        {
            if (record.Manifest == null)
                continue;

            foreach (var route in record.Manifest.Routes ?? [])
            {
                if (Owns(route, path))
                    return record;
            }
        }

        return null;
    }

    ///<summary>A declared route owns a path when the path is the route or is beneath it. "/" owns nothing — a plugin declaring it would claim every unknown address.</summary>
    private static bool Owns(string route, string path)
    {
        var declared = "/" + (route ?? "").Trim('/');

        if (declared == "/")
            return false;

        if (string.Equals(path, declared, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(declared + "/", StringComparison.OrdinalIgnoreCase);
    }
}
