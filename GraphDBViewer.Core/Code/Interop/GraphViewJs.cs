using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Code;

///<summary>
///Mode-dispatched facade over the 2D (Cytoscape) and 3D (3d-force-graph) JS interops, for the view
///operations both canvases support. Each call goes to whichever view is live per the mode provider
///(2 = 2D, 3 = 3D) and is a no-op in any other mode. Interop failures are swallowed — a view tweak
///against a torn-down or not-yet-drawn canvas is never fatal — and the getters return null instead.
///The draw / sync / append data flows stay in Home.GraphView.cs; this class covers the small
///fire-and-forget calls that used to duplicate the mode branching at every call site.
///</summary>
public class GraphViewJs
{
    private readonly IJSRuntime _js;
    private readonly Func<int> _mode;//the active tab's visualization mode (per-tab state owned by Home)

    public GraphViewJs(IJSRuntime js, Func<int> mode)
    {
        _js = js;
        _mode = mode;
    }

    private bool Is2d => _mode() == 2;
    private bool Is3d => _mode() == 3;

    //Cytoscape: factor > 1 zooms in. 3d-force-graph: smaller camera distance zooms in, so the factors invert.
    public async Task ZoomInAsync()
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("zoomCytoscape", 1.25);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.zoom", 0.8);
        }
        catch { }
    }

    public async Task ZoomOutAsync()
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("zoomCytoscape", 0.8);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.zoom", 1.25);
        }
        catch { }
    }

    ///<summary>Re-centers/fits the live graph view.</summary>
    public async Task CenterAsync()
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("centerCytoscapeGraph");
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.centerGraph");
        }
        catch { }
    }

    ///<summary>Finds nodes matching the term (label/id/property); 2D highlights+fits, 3D centers.</summary>
    public async Task SearchAsync(string term)
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("searchCytoscape", term);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.search", term);
        }
        catch { }
    }

    ///<summary>Frames these vertices, and in 2D selects them, as a search's matches are: what an algorithm found, a path or a vertex of its top list.</summary>
    public async Task FocusAsync(IEnumerable<string> ids)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(ids ?? []);

            if (Is2d)
                await _js.InvokeVoidAsync("focusCytoscapeNodes", json);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.focusNodes", json);
        }
        catch { }
    }

    ///<summary>Applies the label filter (a JSON array of the hidden vertex type-labels).</summary>
    public async Task SetLabelFilterAsync(string hiddenLabelsJson)
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("setCytoscapeLabelVisibility", hiddenLabelsJson);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.setLabelFilter", hiddenLabelsJson);
        }
        catch { }
    }

    ///<summary>Pushes a label→{color,size} style map (JSON) onto the live view without a rebuild.</summary>
    public async Task ApplyLabelStylesAsync(string stylesJson)
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("applyCytoscapeLabelStyles", stylesJson);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.applyLabelStyles", stylesJson);
        }
        catch { }
    }

    ///<summary>
    ///Gives a label's vertices on the 2D canvas their details cards, or takes them away, where they stand, vertices with
    ///their own gdbvDetails aside. False where there is no 2D graph to change, so the caller draws the graph instead.
    ///</summary>
    public async Task<bool> SetLabelDetails2dAsync(string label, bool on)
    {
        if (!Is2d)
            return false;

        try
        {
            return await _js.InvokeAsync<bool>("setCytoscapeLabelDetails", label, on);
        }
        catch
        {
            return false;
        }
    }

    ///<summary>
    ///Gives a label's nodes on the 2D canvas a new shape where they stand, nodes with a shape of their own aside. False
    ///where there is no 2D graph to change, so the caller draws the graph instead.
    ///</summary>
    public async Task<bool> SetLabelShape2dAsync(string label, string shape)
    {
        if (!Is2d)
            return false;

        try
        {
            return await _js.InvokeAsync<bool>("setCytoscapeLabelShape", label, shape ?? "");
        }
        catch
        {
            return false;
        }
    }

    ///<summary>Switches the 2D Cytoscape layout on the already-drawn graph.</summary>
    public async Task SetLayout2dAsync(string layout)
    {
        try
        {
            await _js.InvokeVoidAsync("setCytoscapeLayout", layout);
        }
        catch { }
    }

    ///<summary>Switches the 3D force-graph layout (dagMode) on the already-drawn graph.</summary>
    public async Task SetLayout3dAsync(string layout)
    {
        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setLayout", layout);
        }
        catch { }
    }

    ///<summary>
    ///Which earth the Map and the Globe draw — "satellite" or "outline". Both views remember it, so it
    ///is told to both whatever is showing: the one on screen repaints, the other has it for later.
    ///</summary>
    public async Task SetMapStyleAsync(string style)
    {
        try
        {
            await _js.InvokeVoidAsync("setCytoscapeMapStyle", style);
        }
        catch { }

        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setGlobeStyle", style);
        }
        catch { }
    }

    ///<summary>
    ///How sharp the satellite earth is — "standard" or "high" — told to both views as the style is. True for
    ///<paramref name="retry"/> when the user chose it, which is what tries a failed download again; a
    ///redraw telling the same value again passes false, or every redraw offline would ask for 3.7 MB.
    ///</summary>
    public async Task SetMapDetailAsync(string detail, bool retry)
    {
        try
        {
            await _js.InvokeVoidAsync("setCytoscapeMapDetail", detail, retry);
        }
        catch { }

        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setGlobeDetail", detail, retry);
        }
        catch { }
    }

    ///<summary>
    ///Where the Map and the Globe take their earth from: null for the built-in images, or { template, key } for a tile
    ///server (MapTilesPreference). Told to both views, as the style is.
    ///</summary>
    public async Task SetMapTilesAsync(object source)
    {
        try
        {
            await _js.InvokeVoidAsync("setCytoscapeMapTiles", source);
        }
        catch { }

        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setGlobeTiles", source);
        }
        catch { }
    }

    ///<summary>Toggles always-on vertex/edge labels and the shown-properties overlay in the 3D view.</summary>
    public async Task Set3dLabelVisibilityAsync(bool vertexLabels, bool edgeLabels, bool props)
    {
        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setLabelVisibility", vertexLabels, edgeLabels, props);
        }
        catch { }
    }

    ///<summary>Sets the 3D label draw distance (0–100): 100 shows every label, lower fades out labels farther from the camera.</summary>
    public async Task Set3dLabelDrawDistanceAsync(int percent)
    {
        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setLabelDrawDistance", percent);
        }
        catch { }
    }

    ///<summary>Sets the 3D node draw distance (0–100): 100 shows every node, lower hides nodes (and their edges) farther from the camera.</summary>
    public async Task Set3dNodeDrawDistanceAsync(int percent)
    {
        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setNodeDrawDistance", percent);
        }
        catch { }
    }

    ///<summary>Sets Node size on map (a percentage, 100 the plain size) on the Map and the Globe alike.</summary>
    public async Task SetMapNodeSizeAsync(int percent)
    {
        try
        {
            await _js.InvokeVoidAsync("setCytoscapeMapNodeSize", percent);
        }
        catch { }

        try
        {
            await _js.InvokeVoidAsync("graph3DInterop.setGlobeNodeSize", percent);
        }
        catch { }
    }

    ///<summary>Reads the current node positions from the live view as JSON [{id,x,y(,z)}], or null.</summary>
    public async Task<string> GetPositionsAsync()
    {
        try
        {
            if (Is2d)
                return await _js.InvokeAsync<string>("getCytoscapePositions");

            if (Is3d)
                return await _js.InvokeAsync<string>("graph3DInterop.getPositions");
        }
        catch { }

        return null;
    }

    ///<summary>Places nodes at the given positions (JSON [{id,x,y(,z)}]) in the live view.</summary>
    public async Task ApplyPositionsAsync(string positionsJson)
    {
        try
        {
            if (Is2d)
                await _js.InvokeVoidAsync("applyCytoscapePositions", positionsJson);
            else if (Is3d)
                await _js.InvokeVoidAsync("graph3DInterop.applyPositions", positionsJson);
        }
        catch { }
    }

    ///<summary>
    ///The live view as a PNG data URL no longer than <paramref name="most"/> pixels a side, for Ask AI's picture tool
    ///(<see cref="GraphPictureRunner"/>), or null.
    ///</summary>
    public async Task<string> PictureAsync(int most)
    {
        try
        {
            if (Is2d)
                return await _js.InvokeAsync<string>("cytoscapePicture", most);
            else if (Is3d)
                return await _js.InvokeAsync<string>("shrinkPicture", await _js.InvokeAsync<string>("graph3DInterop.image", "png"), most);
        }
        catch { }

        return null;
    }

    ///<summary>Captures the live view as an image data-URL in the given format (png/jpeg), or null.</summary>
    public async Task<string> ImageAsync(string format)
    {
        try
        {
            if (Is2d)
                return await _js.InvokeAsync<string>("cytoscapeImage", format);

            if (Is3d)
                return await _js.InvokeAsync<string>("graph3DInterop.image", format);
        }
        catch { }

        return null;
    }
}
