using System.Text.Json;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The algorithms over the loaded graph (DESIGN.md Part 2, Graph algorithms): the panel computes, in this browser (tier B)
//or in the database by its own library (tier A), and what it finds is kept here as an AlgorithmOverlay beside the graph,
//drawn by the converters, listed by the table and carried by its export. One at a time, for the result it was computed
//on: a new result leaves it behind.
public partial class Home
{
    private bool showAlgorithms;
    private AlgorithmOverlay algorithmOverlay;

    ///<summary>
    ///The connected database, for the panel to look for algorithms of its own in and run them there, or null when there is
    ///none to ask. The panel sends its queries itself, past the page's query box, and down the read-only path where the
    ///driver has one, so a run neither replaces the graph drawn nor writes to the database.
    ///</summary>
    private IGraphDb AlgorithmDatabase()
    {
        if (!isConnected || db == null)
            return null;

        return db;
    }

    ///<summary>The overlay, when it was computed on the result the page holds now, or null.</summary>
    private AlgorithmOverlay ActiveOverlay()
    {
        if (algorithmOverlay != null && algorithmOverlay.AppliesTo(CurrentGraphData()))
            return algorithmOverlay;

        return null;
    }

    ///<summary>The vertex selected on the canvas, for a path's ends.</summary>
    private string SelectedNodeId()
    {
        if (selectedElement != null && selectedElement.Type == "node")
            return selectedElement.Id;

        return null;
    }

    ///<summary>A result from the panel, drawn in place on the live view: the nodes keep where they are.</summary>
    private async Task ApplyAlgorithmAsync(AlgorithmOverlay overlay)
    {
        overlay.Of = CurrentGraphData();
        algorithmOverlay = overlay;

        await RedrawOverlayAsync();

        if (overlay.Kind == AlgorithmOverlay.Kinds.Path)
            await GraphView.FocusAsync(overlay.PathNodes);
    }

    private async Task ClearAlgorithmAsync()
    {
        algorithmOverlay = null;

        await RedrawOverlayAsync();
    }

    private async Task FocusAlgorithmAsync(IReadOnlyList<string> ids)
    {
        await GraphView.FocusAsync(ids);
    }

    ///<summary>
    ///The live view reconciled to the graph with the overlay as it is now, in place, so a result is drawn without the
    ///layout moving: the converters fold the overlay in, and both views' syncs update colors and sizes where they are.
    ///Unlike SyncOptimisticViewAsync, this runs while the canvas mirrors the database too, since it changes no data.
    ///</summary>
    private async Task RedrawOverlayAsync()
    {
        if (!ShowGraphCanvas || (visualizationMode != 2 && visualizationMode != 3))
        {
            StateHasChanged();

            return;
        }

        try
        {
            var data = EffectiveData();

            if (data.ValueKind == JsonValueKind.Undefined)
                return;

            if (visualizationMode == 2)
                await JS.InvokeVoidAsync("syncCytoscapeGraph", GraphDataConverter.ToCytoscapeJson(data, labelStyles, edgeColorMode, edgeColors, ActiveOverlay(), edgeDetails));
            else
                await JS.InvokeVoidAsync("graph3DInterop.syncData", GraphDataConverter.ToForceGraphJson(data, labelStyles, edgeColorMode, edgeColors, ActiveOverlay(), edgeDetails));
        }
        catch { }

        StateHasChanged();
    }
}
