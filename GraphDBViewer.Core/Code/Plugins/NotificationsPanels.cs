using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>The notifications page's panels and presets (panel-boards spec): the list, and the settings beside or below it.</summary>
public static class NotificationsPanels
{
    public const string PageKey = "notifications";

    public static readonly IReadOnlyList<PanelPreset> Presets =
    [
        new PanelPreset
        {
            Id = "side",
            Name = "List and settings",
            Icon = "bi-layout-sidebar-reverse",
            Rows = [new PanelRow(new PanelColumn(8, "list"), new PanelColumn(4, "settings"))]
        },
        new PanelPreset
        {
            Id = "stacked",
            Name = "Stacked",
            Icon = "bi-view-stacked",
            Rows = [new PanelRow(new PanelColumn(12, "list", "settings"))]
        }
    ];

    public static readonly IReadOnlyList<PanelInfo> Panels =
    [
        new PanelInfo { Id = "list", Title = "Notifications", Icon = "bi-bell", Required = true },
        new PanelInfo { Id = "settings", Title = "Settings", Icon = "bi-sliders" }
    ];
}
