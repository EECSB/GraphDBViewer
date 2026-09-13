namespace GraphDBViewerWeb.Code;

///<summary>What happened to one installed plugin at this boot.</summary>
public enum PluginBootOutcome
{
    ///<summary>Loaded and registered; its pages route.</summary>
    Loaded,

    ///<summary>In the list with <c>enabled: false</c> — kept, not loaded.</summary>
    Disabled,

    ///<summary>Skipped because the whole boot ran with <c>?safemode=1</c>.</summary>
    SafeMode,

    ///<summary>Skipped because a previous boot marked it — the mark's reason rides in the detail.</summary>
    Quarantined,

    ///<summary>Failed at this boot; now marked, so the next boot skips it instead of failing again.</summary>
    Failed
}

public sealed class PluginBootEntry
{
    public string Id { get; init; }

    public PluginBootOutcome Outcome { get; init; }

    ///<summary>The reason, for the outcomes that have one; null for a plain load or a disable.</summary>
    public string Detail { get; init; }
}

///<summary>
///How the boot-time plugin load went, kept as a singleton for the Plugins page: which loaded, which
///were skipped and why — the quarantine reasons an administrator acts on live here. On a host that
///loads no foreign code (the web-only edition, or simply nothing installed) it is empty and silent.
///</summary>
public sealed class PluginBootReport
{
    ///<summary>Whether this boot ran with <c>?safemode=1</c> and loaded no installed plugins at all.</summary>
    public bool SafeMode { get; init; }

    ///<summary>
    ///Whether the installed list itself could not be fetched at boot — a timeout, an unreachable host —
    ///as opposed to an answer of none. Nothing loaded and nothing is known, which is different from
    ///"nothing installed", and the Plugins and not-found pages say so rather than calling every record
    ///"waiting for a reload" (ledger items 19 and 29, 2026-09-05).
    ///</summary>
    public bool ListUnavailable { get; init; }

    public IReadOnlyList<PluginBootEntry> Entries { get; init; } = [];

    ///<summary>The report for a boot with nothing to load.</summary>
    public static readonly PluginBootReport Empty = new();

    ///<summary>This boot's answer for one plugin id, or null when the boot never saw it — which is
    ///what a record added after boot looks like: installed, waiting for the reload.</summary>
    public PluginBootEntry For(string id)
    {
        foreach (var entry in Entries)
        {
            if (entry.Id == id)
                return entry;
        }

        return null;
    }
}
