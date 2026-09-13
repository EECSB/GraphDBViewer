using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///The on-demand rewrite of a perspective's typed properties (ADR 0014, question 1; the write seam's
///task 4). After the seam learned what a property is, a vertex written before still holds text where a
///number belongs, and a range query quietly under-matches until it is rewritten — so the rewrite is
///run per perspective, on demand, by whoever holds <c>plugins.install</c>, and this is its report. A
///value that would not convert is quarantined rather than lost: the typed key absent, <c>&lt;name&gt;$unparsed</c>
///holding the raw text, and the worklist naming it so somebody can reach it. The last report is kept
///under the plugin's retype key so the Plugins page shows it.
///</summary>
public sealed class RetypeReport
{
    ///<summary>POST runs it for one plugin id, <c>api/plugins/retype?id=…</c>; GET answers the last report, or nothing.</summary>
    public const string Path = "api/plugins/retype";

    public string PluginId { get; set; }

    ///<summary>Whether this plugin has a typed vocabulary to rewrite at all — false hides the button.</summary>
    public bool Supported { get; set; } = true;

    ///<summary>When it ran, ISO 8601 UTC, and by whom.</summary>
    public string RanAt { get; set; }

    public string RanBy { get; set; }

    ///<summary>False when nothing ran — no instance graph, or a plugin with no typed vocabulary — with <see cref="Error"/> saying why.</summary>
    public bool Ran { get; set; }

    public string Error { get; set; }

    public List<RetypeLabel> Labels { get; set; } = new();

    ///<summary>Vertices scanned, across every label.</summary>
    public int Scanned { get; set; }

    ///<summary>Vertices with at least one property rewritten.</summary>
    public int Rewritten { get; set; }

    ///<summary>Values that would not convert, now beside their vertex as <c>&lt;name&gt;$unparsed</c>.</summary>
    public int Quarantined { get; set; }

    ///<summary>
    ///Vertices whose identity moved from its former name, <c>key</c>, to <c>uid</c> — over the whole
    ///graph, not this plugin's labels alone, since the identity is the platform's. Zero once it has
    ///run anywhere.
    ///</summary>
    public int Renamed { get; set; }
}

public sealed class RetypeLabel
{
    public string Label { get; set; }

    public int Scanned { get; set; }

    public int Rewritten { get; set; }

    ///<summary>Property → how many values were rewritten as their kind.</summary>
    public Dictionary<string, int> Properties { get; set; } = new();

    ///<summary>The worklist: every value that would not convert, by vertex.</summary>
    public List<RetypeQuarantine> Quarantines { get; set; } = new();
}

public sealed class RetypeQuarantine
{
    public string Key { get; set; }

    public string Property { get; set; }

    ///<summary>The text that would not read as the property's kind, exactly as it was.</summary>
    public string Raw { get; set; }
}
