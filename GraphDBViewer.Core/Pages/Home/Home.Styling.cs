using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The Style dialog: per-label styles (browser-stored, applied live or via redraw), the per-node
//database scope (gdbv* properties staged through the Generated tab), and named reusable stylesheets.
public partial class Home
{

    private const string StylesheetsKey = "graphdbviewer:stylesheets";

    //Named, reusable per-label style sets (name → label→style), shared across tabs and persisted.
    private Dictionary<string, Dictionary<string, LabelStyle>> savedStylesheets = new();
    private string newStylesheetName;

    private bool showStyle;//per-label styling panel open
    private string styleFocusLabel;//vertex-label row to highlight when the style panel is opened for a node
    private string styleFocusEdgeLabel;//edge label the style panel is focused on (opened from a selected edge)

    //Tracks where the mouse was pressed so a selection drag from inside the dialog to the backdrop
    //doesn't close the Style modal — only a press that begins on the backdrop does.
    private bool styleBackdropMouseDown;

    private void CloseStyleIfBackdrop()
    {
        if (styleBackdropMouseDown)
            showStyle = false;
    }

    //Style dialog scope: false = per-label, saved in this browser (default); true = per-node, saved
    //on the selected vertex in the database (written as gdbv* properties via the Generated tab).
    private bool styleToNode;

    //Opens the style panel for all labels (graph toolbar) or focused on the selected vertex's label.
    private void OpenStyleAll()
    {
        styleFocusLabel = null;
        styleFocusEdgeLabel = null;
        styleToNode = false;
        showStyle = true;
    }

    private void OpenStyleForSelected()
    {
        //A selected edge colors by its label — open the panel focused on the edge-color controls.
        if (selectedElement != null && selectedElement.Type == "edge")
        {
            styleFocusEdgeLabel = selectedElement.Label;
            styleFocusLabel = null;
            styleToNode = false;
            showStyle = true;
            return;
        }

        styleFocusEdgeLabel = null;
        styleFocusLabel = selectedElement?.GLabel;
        //Opened from a selected component — default to styling that one node (per-node/database scope).
        styleToNode = HasSelectedNode;
        showStyle = true;
    }

    //True when a single vertex is selected — the per-node ("This node — database") style scope needs one.
    private bool HasSelectedNode => selectedElement != null && selectedElement.Type == "node";

    //Switches the Style dialog to the per-node scope, but only when a node is selected. The toggle button
    //is shown grayed-out with a tooltip otherwise, so this is a no-op in that case.
    private void SwitchToNodeScope()
    {
        if (!HasSelectedNode)
            return;

        styleToNode = true;
    }

    //The style panel shows just the focused component's label, or every label when opened from the toolbar.
    //In database (per-node) mode it collapses to the selected node's label so edits target that one node,
    //even when the dialog was opened from the toolbar's all-labels view.
    private List<string> StyleLabels
    {
        get
        {
            if (styleToNode && selectedElement != null && !string.IsNullOrEmpty(selectedElement.GLabel))
                return new List<string> { selectedElement.GLabel };

            if (!string.IsNullOrEmpty(styleFocusLabel))
                return new List<string> { styleFocusLabel };

            return GraphLabels;
        }
    }

    //Title for the Style dialog: the node in database mode, the focused vertex/edge label from the
    //sidebar, else all labels.
    private string StyleModalTitle
    {
        get
        {
            if (styleToNode && selectedElement != null)
                return $"Style: {selectedElement.GLabel} · {selectedElement.Id}";

            if (!string.IsNullOrEmpty(styleFocusEdgeLabel))
                return $"Style edge: {styleFocusEdgeLabel}";

            if (!string.IsNullOrEmpty(styleFocusLabel))
                return $"Style: {styleFocusLabel}";

            return "Styling";
        }
    }

    //The edge labels shown in the edge-color list: just the focused edge's label when the panel was
    //opened from a selected edge, otherwise every edge label in the current graph.
    private List<string> StyleEdgeLabels
    {
        get
        {
            if (!string.IsNullOrEmpty(styleFocusEdgeLabel))
                return new List<string> { styleFocusEdgeLabel };

            return GraphEdgeLabels;
        }
    }

    private LabelStyle EnsureStyle(string label)
    {
        if (!labelStyles.TryGetValue(label, out var style))
        {
            style = new LabelStyle();
            labelStyles[label] = style;
        }

        return style;
    }

    private string StyleColorOf(string label)
    {
        if (styleToNode)
        {
            var v = selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Color);
            if (!string.IsNullOrWhiteSpace(v))
                return v;

            return "#0d6efd";
        }

        if (labelStyles.TryGetValue(label, out var s) && !string.IsNullOrWhiteSpace(s.Color))
            return s.Color;

        return "#0d6efd";
    }

    private int StyleSizeOf(string label)
    {
        if (styleToNode)
        {
            if (int.TryParse(selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Size), out var n))
                return n;

            return 0;
        }

        if (labelStyles.TryGetValue(label, out var s))
            return s.Size;

        return 0;
    }

    //The selected 2D canvas shape (default rectangle).
    private string StyleShape2dOf(string label)
    {
        if (styleToNode)
        {
            var v = selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Shape);
            if (!string.IsNullOrWhiteSpace(v))
                return v;

            return "rectangle";
        }

        if (labelStyles.TryGetValue(label, out var s) && !string.IsNullOrWhiteSpace(s.Shape))
            return s.Shape;

        return "rectangle";
    }

    //The selected 3D solid (default sphere).
    private string StyleShape3dOf(string label)
    {
        if (styleToNode)
        {
            var v = selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Shape3d);
            if (!string.IsNullOrWhiteSpace(v))
                return v;

            return "sphere";
        }

        if (labelStyles.TryGetValue(label, out var s) && !string.IsNullOrWhiteSpace(s.Shape3d))
            return s.Shape3d;

        return "sphere";
    }

    private string StyleDisplayOf(string label)
    {
        if (styleToNode)
            return selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Display);

        if (labelStyles.TryGetValue(label, out var s))
            return s.DisplayProperty;

        return null;
    }

    private string StyleIconOf(string label)
    {
        if (styleToNode)
            return selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Image);

        if (labelStyles.TryGetValue(label, out var s))
            return s.Icon;

        return null;
    }

    private string StyleModelOf(string label)
    {
        if (styleToNode)
            return selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Model);

        if (labelStyles.TryGetValue(label, out var s))
            return s.Model;

        return null;
    }

    //Color/size apply live (no relayout); display-property and icon recompute node data so they re-render.
    //In per-node (database) scope every change instead writes a gdbv* property on the selected vertex,
    //which surfaces in the Generated tab to review and Commit.
    private async Task SetLabelColorAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            OnPropertyChanged(GdbvKeys.Color, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).Color = e.Value?.ToString();
        await SaveTabsAsync();
        await ApplyLabelStylesLiveAsync();
    }

    private async Task SetLabelSizeAsync(string label, ChangeEventArgs e)
    {
        int.TryParse(e.Value?.ToString(), out int size);

        if (styleToNode)
        {
            if (size > 0)
                OnPropertyChanged(GdbvKeys.Size, size.ToString());
            else
                RemovePropertyIfPresent(GdbvKeys.Size);

            return;
        }

        EnsureStyle(label).Size = size;
        await SaveTabsAsync();
        await ApplyLabelStylesLiveAsync();
    }

    //2D canvas shape. Redraws the graph — the shape reads through node data.
    private async Task SetLabelShape2dAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            SetOrRemoveNodeProperty(GdbvKeys.Shape, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).Shape = e.Value?.ToString();
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //3D solid (built when no model is set). Redraws the graph — the 3D view rebuilds its per-node meshes.
    private async Task SetLabelShape3dAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            SetOrRemoveNodeProperty(GdbvKeys.Shape3d, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).Shape3d = e.Value?.ToString();
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task SetLabelDisplayAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            SetOrRemoveNodeProperty(GdbvKeys.Display, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).DisplayProperty = e.Value?.ToString();
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task SetLabelIconAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            SetOrRemoveNodeProperty(GdbvKeys.Image, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).Icon = e.Value?.ToString();
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task SetLabelModelAsync(string label, ChangeEventArgs e)
    {
        if (styleToNode)
        {
            SetOrRemoveNodeProperty(GdbvKeys.Model, e.Value?.ToString());
            return;
        }

        EnsureStyle(label).Model = e.Value?.ToString();
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task ResetLabelStyleAsync(string label)
    {
        if (styleToNode)
        {
            foreach (var key in new[] { GdbvKeys.Color, GdbvKeys.Size, GdbvKeys.Shape, GdbvKeys.Shape3d, GdbvKeys.Display, GdbvKeys.Image, GdbvKeys.Model })
                RemovePropertyIfPresent(key);

            return;
        }

        if (labelStyles.Remove(label))
        {
            await SaveTabsAsync();
            await RenderGraphAsync();
        }
    }

    //Sets a per-node gdbv* property, or removes it when cleared, generating the matching commit query.
    private void SetOrRemoveNodeProperty(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            RemovePropertyIfPresent(key);
        else
            OnPropertyChanged(key, value);
    }

    //Drops a property only when the selected element actually carries it, so clearing an already-empty
    //field doesn't queue a pointless drop query.
    private void RemovePropertyIfPresent(string key)
    {
        if (selectedElement != null && selectedElement.EditableProperties.ContainsKey(key))
            RemoveProperty(key);
    }

    //Resets a node's pinned position for the current viewer — drops its 2D gdbvX/Y (or the 3D
    //gdbvX3d/Y3d/Z3d) so the node is no longer placed by the user-saved-positions layout. Staged in the
    //Generated tab like any other edit; the other viewer's pin is left untouched.
    //Clears the given position keys (unpinning the node in that viewer). The 2D (gdbvX/Y) and 3D
    //(gdbvX3d/Y3d/Z3d) positions are kept separately, so each group's reset drops only its own keys; the
    //drops queue in the Generated tab like any edit.
    private void ResetPositions(string[] keys)
    {
        foreach (var key in keys)
            RemovePropertyIfPresent(key);
    }

    //Stages queries that strip every viewer-reserved (gdbv*) property from all vertices and edges, so the
    //user can clean their data of viewer-only styling / position metadata. Queued in the Generated tab for
    //review and Commit — nothing hits the database until then.
    private async Task RemoveAllViewerPropertiesAsync()
    {
        //No confirm here: this only stages the drop queries in the Generated tab — the user still has
        //to review and Commit them, which is the real point of no return. An engine that cannot visit
        //every node (GUN) refuses, and says why.
        var cleanup = ComposedOrExplained(() => Qb.DropAllViewerProperties());

        if (cleanup == null)
            return;

        AppendGeneratedQuery(cleanup);

        //Close the dialog and reveal the Generated tab so the user can review and Commit.
        showStyle = false;
        queryEditorTab = 2;

        await SaveTabsAsync();
        await PersistTabTextAsync();
    }

    //Pushes the current color/size map to whichever graph view is live (no rebuild).
    private async Task ApplyLabelStylesLiveAsync()
    {
        var map = new Dictionary<string, object>();
        foreach (var kv in labelStyles)
            map[kv.Key] = new { color = kv.Value.Color, size = kv.Value.Size };

        var json = JsonSerializer.Serialize(map);
        await GraphView.ApplyLabelStylesAsync(json);
    }

    #region Edge colors

    //The color an edge label currently draws with. Defers to the converter's own resolution so a picker
    //can never disagree with what's on the canvas.
    private string EdgeColorOf(string label)
    {
        return GraphDataConverter.ResolveEdgeColor(label, edgeColorMode, edgeColors);
    }

    //Switches the edge-coloring mode (0 = auto, 1 = off/gray, 2 = custom) and redraws — edge colors are
    //baked into the converter output, so this needs a full rebuild rather than a live restyle.
    private async Task SetEdgeColorModeAsync(int mode)
    {
        edgeColorMode = mode;
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //Sets a per-edge-label custom color and redraws. In auto mode the pick implies custom mode, so the edit
    //takes effect immediately; off mode stays off, where a picked color applies to just that label and every
    //other label keeps drawing gray.
    private async Task SetEdgeColorAsync(string edgeLabel, ChangeEventArgs e)
    {
        edgeColors[edgeLabel] = e.Value?.ToString();

        if (edgeColorMode == 0)
            edgeColorMode = 2;

        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //Clears a per-edge-label picked color and redraws, so the label falls back to what the current mode
    //gives it — the default gray while coloring is off, otherwise its auto color.
    private async Task ResetEdgeColorAsync(string edgeLabel)
    {
        if (edgeColors.Remove(edgeLabel))
        {
            await SaveTabsAsync();
            await RenderGraphAsync();
        }
    }

    #endregion

    #region Stylesheets

    private async Task LoadStylesheetsAsync()
    {
        var stored = await Storage.GetAsync<Dictionary<string, Dictionary<string, LabelStyle>>>(StylesheetsKey);

        if (stored is { Count: > 0 })
            savedStylesheets = stored;
    }

    private async Task PersistStylesheetsAsync()
    {
        await Storage.SetAsync(StylesheetsKey, savedStylesheets);
    }

    //Deep-copies a label→style map so a saved stylesheet and a tab's live styles never alias.
    private static Dictionary<string, LabelStyle> CloneStyles(Dictionary<string, LabelStyle> src)
    {
        var copy = new Dictionary<string, LabelStyle>();

        foreach (var kv in src)
            copy[kv.Key] = new LabelStyle { Color = kv.Value.Color, Size = kv.Value.Size, Shape = kv.Value.Shape, Shape3d = kv.Value.Shape3d, DisplayProperty = kv.Value.DisplayProperty, Icon = kv.Value.Icon, Model = kv.Value.Model };

        return copy;
    }

    //Saves the current tab's per-label styling as a named, reusable stylesheet.
    private async Task SaveStylesheetAsync()
    {
        var name = (newStylesheetName ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(name))
            return;

        savedStylesheets[name] = CloneStyles(labelStyles);
        newStylesheetName = null;
        await PersistStylesheetsAsync();
    }

    //Applies a saved stylesheet to the current tab (replacing its per-label styles) and redraws.
    private async Task ApplyStylesheetAsync(string name)
    {
        if (!savedStylesheets.TryGetValue(name, out var styles))
            return;

        labelStyles = CloneStyles(styles);
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task DeleteStylesheetAsync(string name)
    {
        if (savedStylesheets.Remove(name))
            await PersistStylesheetsAsync();
    }

    #endregion
}
