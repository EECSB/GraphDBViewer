using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///A panel a plugin offers every page's board (ADR 0019), the way a plugin offers the DAM a preview: registered in its
///ConfigureServices, so a disabled plugin offers none. The board lists it with the page's own panels, hidden in every
///preset until a person shows it with Customize, and draws its component given the board's <c>PageKey</c>, under which
///the panel keeps what it shows for that person on that page.
///</summary>
public interface IBoardPanel
{
    ///<summary>Its id on every board, its plugin's id first so that no page's own panel has it: <c>treeality.bim/model</c>.</summary>
    string Id { get; }

    string Title { get; }

    ///<summary>A Bootstrap Icons class, such as <c>bi-box</c>.</summary>
    string Icon { get; }

    ///<summary>Needs a column at least half the page wide.</summary>
    bool Wide { get; }

    ///<summary>The permission a person needs for the board to offer it, or null when everyone may have it.</summary>
    string? Permission { get; }

    ///<summary>The component drawn in it, given the board's <c>PageKey</c> as a string parameter.</summary>
    Type Component { get; }
}

///<summary>A page's board with the panels plugins offer it: what the board lays out and draws.</summary>
public static class BoardPanels
{
    ///<summary>The panels offered to this person: each once, none whose id a page panel has, none they may not have.</summary>
    public static IReadOnlyList<IBoardPanel> OfferedTo(IEnumerable<IBoardPanel> offered, IReadOnlyList<PanelInfo> pagePanels, Func<string, bool> may)
    {
        var taken = new HashSet<string>(pagePanels.Select(p => p.Id));
        var chosen = new List<IBoardPanel>();

        foreach (var panel in offered ?? [])
        {
            if (panel?.Id == null || !taken.Add(panel.Id))
                continue;

            if (panel.Permission != null && !may(panel.Permission))
                continue;

            chosen.Add(panel);
        }

        return chosen;
    }

    ///<summary>The page's panels and then the offered ones, as the board arranges them.</summary>
    public static IReadOnlyList<PanelInfo> With(IReadOnlyList<PanelInfo> pagePanels, IReadOnlyList<IBoardPanel> offered)
    {
        return pagePanels.Concat(offered.Select(o => new PanelInfo { Id = o.Id, Title = o.Title, Icon = o.Icon, Wide = o.Wide })).ToList();
    }

    ///<summary>Each of the page's presets with the offered panels hidden in it, so that one shows only when a person asks.</summary>
    public static IReadOnlyList<PanelPreset> With(IReadOnlyList<PanelPreset> presets, IReadOnlyList<IBoardPanel> offered)
    {
        if (offered.Count == 0)
            return presets;

        return presets.Select(p => new PanelPreset
        {
            Id = p.Id,
            Name = p.Name,
            Icon = p.Icon,
            Rows = p.Rows,
            Hidden = p.Hidden.Concat(offered.Select(o => o.Id).Where(id => !p.Hidden.Contains(id))).ToList()
        }).ToList();
    }
}
