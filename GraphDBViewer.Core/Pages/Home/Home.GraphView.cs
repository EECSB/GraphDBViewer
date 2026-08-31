using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The graph canvas: rendering into the 2D (Cytoscape) and 3D (3d-force-graph) views, the optimistic
//uncommitted-edits preview, zoom / search / label filtering, layouts, and saved node positions.
public partial class Home
{

    //The special layout value for the hand-pinned "user saved positions" arrangement.
    private const string UserSavedLayout = "userSaved";

    //Whether to offer the "user saved positions" entry in the layout dropdown — shown when a browser
    //arrangement exists (Save positions) or the loaded data carries committed gdbv positions. The
    //positions are only *applied* when that layout is the one the user has selected.
    private bool HasUserSavedPositions => !string.IsNullOrEmpty(CurrentSavedPositionsJson) || dataHasPinnedPositions;

    //Whether the currently loaded graph carries committed gdbv* positions. Cached on each render (the
    //check parses the graph) so HasUserSavedPositions stays cheap.
    private bool dataHasPinnedPositions;

    //Facade over the 2D/3D JS interops for the calls both views support; it dispatches on the
    //active tab's visualization mode and swallows interop failures.
    private GraphViewJs graphView;
    private GraphViewJs GraphView => graphView ??= new GraphViewJs(JS, () => visualizationMode);

    private async Task CenterGraphAsync()
    {
        await GraphView.CenterAsync();
    }

    private async Task ZoomInAsync()
    {
        await GraphView.ZoomInAsync();
    }

    private async Task ZoomOutAsync()
    {
        await GraphView.ZoomOutAsync();
    }

    //Distinct vertex type-labels in the current graph, for the label-filter checkboxes.
    private List<string> GraphLabels
    {
        get
        {
            var data = CurrentGraphData();

            if (data.ValueKind == JsonValueKind.Undefined)
                return new List<string>();

            return GraphDataConverter.VertexLabels(data);
        }
    }

    //Distinct edge labels in the current (effective) graph, for the edge-color styling list — uses the
    //effective view so edges drawn offline (staged edits) can be colored too.
    private List<string> GraphEdgeLabels
    {
        get
        {
            var data = EffectiveData();

            if (data.ValueKind == JsonValueKind.Undefined)
                return new List<string>();

            return GraphDataConverter.EdgeLabels(data);
        }
    }

    //Distinct node display labels in the current graph, for the search box's type-ahead datalist.
    private List<string> SearchSuggestions
    {
        get
        {
            var data = CurrentGraphData();

            if (data.ValueKind == JsonValueKind.Undefined)
                return new List<string>();

            return GraphDataConverter.SearchSuggestions(data, labelStyles);
        }
    }

    //Finds nodes matching the search box (label/id/property); 2D highlights+fits, 3D centers.
    private async Task OnSearchChanged(ChangeEventArgs e)
    {
        searchTerm = e.Value?.ToString();
        await RunSearchAsync();
    }

    //Runs the search when Enter is pressed, without waiting for the box to lose focus.
    private async Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key != "Enter")
            return;

        await RunSearchAsync();
    }

    //Applies the current search term to the graph and persists it.
    private async Task RunSearchAsync()
    {
        await GraphView.SearchAsync(searchTerm);
        await SaveTabsAsync();
    }

    //Toggles visibility of a vertex type-label and re-applies the filter to the graph.
    private async Task ToggleLabelAsync(string label, bool visible)
    {
        if (visible)
            hiddenLabels.Remove(label);
        else
            hiddenLabels.Add(label);

        await ApplyLabelFilterAsync();
        await SaveTabsAsync();
    }

    private async Task ApplyLabelFilterAsync()
    {
        var json = JsonSerializer.Serialize(hiddenLabels);
        await GraphView.SetLabelFilterAsync(json);
    }

    //Switches the 2D Cytoscape layout on the already-drawn graph.
    private async Task OnLayout2dChanged(ChangeEventArgs e)
    {
        cy2dLayout = e.Value?.ToString() ?? "cose";
        await SaveTabsAsync();

        //The user-saved layout needs a full redraw so committed gdbv positions are placed (and the
        //saved browser arrangement re-applied); every other layout just re-runs live on the graph.
        if (cy2dLayout == UserSavedLayout)
        {
            await RenderGraphAsync();
            return;
        }

        await GraphView.SetLayout2dAsync(cy2dLayout);
    }

    //Switches the 3D force-graph layout (dagMode) on the already-drawn graph.
    private async Task OnLayout3dChanged(ChangeEventArgs e)
    {
        g3dLayout = e.Value?.ToString() ?? "force";
        await SaveTabsAsync();

        //The user-saved layout needs a full redraw so committed gdbv positions are placed (and the
        //saved browser arrangement re-applied); every other layout just re-runs live on the graph.
        if (g3dLayout == UserSavedLayout)
        {
            await RenderGraphAsync();
            return;
        }

        await GraphView.SetLayout3dAsync(g3dLayout);
    }

    //Whether the 3D "Label options" dropdown is open. Transient view state, so it is a plain field rather
    //than a persisted per-tab setting.
    private bool showLabelMenu;

    //Toggles always-on 3D vertex labels (unchecked = labels show only on hover).
    private async Task On3dVertexLabelsChanged(ChangeEventArgs e)
    {
        show3dVertexLabels = e.Value is bool b && b;
        await SaveTabsAsync();
        await GraphView.Set3dLabelVisibilityAsync(show3dVertexLabels, show3dEdgeLabels, show3dProps);
    }

    //Toggles always-on 3D edge labels (unchecked = labels show only on hover).
    private async Task On3dEdgeLabelsChanged(ChangeEventArgs e)
    {
        show3dEdgeLabels = e.Value is bool b && b;
        await SaveTabsAsync();
        await GraphView.Set3dLabelVisibilityAsync(show3dVertexLabels, show3dEdgeLabels, show3dProps);
    }

    //Toggles the shown-properties overlay (the "show"-marked properties beneath each node) in 3D.
    private async Task On3dPropsChanged(ChangeEventArgs e)
    {
        show3dProps = e.Value is bool b && b;
        await SaveTabsAsync();
        await GraphView.Set3dLabelVisibilityAsync(show3dVertexLabels, show3dEdgeLabels, show3dProps);
    }

    //Sets the 3D label draw distance (0–100): fades out vertex/edge labels farther than that fraction of
    //the graph's reach from the camera, so a dense graph's text stays legible. 100 shows every label.
    private async Task On3dDrawDistanceChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var value))
            draw3dLabelDistance = Math.Clamp(value, 0, 100);

        await SaveTabsAsync();
        await GraphView.Set3dLabelDrawDistanceAsync(draw3dLabelDistance);
    }

    //Sets the 3D node draw distance (0–100): hides nodes — and the edges touching them — farther than that
    //fraction of the graph's reach from the camera, so a dense graph declutters. 100 shows every node.
    private async Task On3dNodeDistanceChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var value))
            draw3dNodeDistance = Math.Clamp(value, 0, 100);

        await SaveTabsAsync();
        await GraphView.Set3dNodeDrawDistanceAsync(draw3dNodeDistance);
    }

    private async Task SetVisualizationMode(int mode)
    {
        visualizationMode = mode;
        if ((mode == 2 || mode == 3) && HasGraphData)
            await RenderGraphAsync();
        else
            await SaveTabsAsync();
    }

    //The graph data to render: the loaded database baseline, or — when "reflect database state" is off
    //— that baseline with the staged (uncommitted) edits applied, so they preview on the canvas.
    private JsonElement EffectiveData()
    {
        var data = CurrentGraphData();

        if (reflectDbState || data.ValueKind == JsonValueKind.Undefined)
            return data;

        //An engine with no query builder stages nothing, so there is nothing to apply and the baseline
        //is the whole answer. SPARQL is the case: it composes no queries on the user's behalf, so Qb is
        //null, and this runs on every render rather than behind a capability gate like the other uses.
        if (Qb == null)
            return data;

        var edits = Qb.ParseEdits(generatedQueryText);

        if (edits.Count == 0)
            return data;

        return GraphDataConverter.BuildEffectiveGraphSON(data, edits);
    }

    //Reconciles whichever graph view is live to the current optimistic (uncommitted) view, in place so
    //existing nodes keep their positions. A no-op while "reflect database state" is on — the canvas then
    //mirrors the database and staged edits only appear after they are committed.
    private async Task SyncOptimisticViewAsync()
    {
        if (reflectDbState)
            return;

        if (visualizationMode != 2 && visualizationMode != 3)
            return;

        try
        {
            var effective = EffectiveData();

            if (effective.ValueKind == JsonValueKind.Undefined)
                return;

            if (visualizationMode == 2)
            {
                var elementsJson = GraphDataConverter.ToCytoscapeJson(effective, labelStyles, edgeColorMode, edgeColors);
                await JS.InvokeVoidAsync("syncCytoscapeGraph", elementsJson);
            }
            else if (visualizationMode == 3)
            {
                var graphJson = GraphDataConverter.ToForceGraphJson(effective, labelStyles, edgeColorMode, edgeColors);
                await JS.InvokeVoidAsync("graph3DInterop.syncData", graphJson);
            }
        }
        catch { }
    }

    //Toggles between mirroring the database and previewing staged edits, then redraws from that source.
    private async Task ToggleReflectDbState()
    {
        reflectDbState = !reflectDbState;
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    private async Task RenderGraphAsync()
    {
        var data = EffectiveData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return;

        selectedElement = null;
        hasPropertyChanges = false;
        commitStatus = null;

        await Task.Yield();
        StateHasChanged();
        await Task.Delay(50);

        try
        {
            //#cyGraph / #graph3d only exist while ShowGraphCanvas holds — it mirrors the markup in
            //Home.razor. Gate every draw on it, or the interops target a container that was never
            //rendered ("Cytoscape container not found" / "3D graph container not found").
            bool show3d = ShowGraphCanvas && visualizationMode == 3;

            //The 3D view owns a requestAnimationFrame loop that outlives its container, so tear it down
            //whenever #graph3d is off the page — otherwise it keeps ticking against a detached renderer
            //and throws inside 3d-force-graph ("Cannot read properties of undefined (reading 'tick')").
            if (!show3d)
                await JS.InvokeVoidAsync("graph3DInterop.destroyGraph");

            if (ShowGraphCanvas && visualizationMode == 2)
            {
                await JS.InvokeVoidAsync("setCytoscapeDotNetRef", SelfRef());
                var elementsJson = GraphDataConverter.ToCytoscapeJson(data, labelStyles, edgeColorMode, edgeColors);
                await JS.InvokeVoidAsync("drawCytoscapeGraph", elementsJson, "cyGraph", cy2dLayout);

                //A redraw builds a fresh Cytoscape instance, so re-apply grid mode if it's on.
                if (gridMode)
                    await JS.InvokeVoidAsync("setCytoscapeGridMode", true);
            }
            else if (show3d)
            {
                await JS.InvokeVoidAsync("graph3DInterop.setDotNetRef", SelfRef());
                var graphJson = GraphDataConverter.ToForceGraphJson(data, labelStyles, edgeColorMode, edgeColors);
                await JS.InvokeVoidAsync("graph3DInterop.drawGraph", "graph3d", graphJson, g3dLayout);
                await JS.InvokeVoidAsync("graph3DInterop.setLabelVisibility", show3dVertexLabels, show3dEdgeLabels, show3dProps);
                await JS.InvokeVoidAsync("graph3DInterop.setLabelDrawDistance", draw3dLabelDistance);
                await JS.InvokeVoidAsync("graph3DInterop.setNodeDrawDistance", draw3dNodeDistance);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Graph render error: {ex.Message}");
        }

        if (visualizationMode == 2 || visualizationMode == 3)
        {
            //Cache whether the loaded data carries committed gdbv positions, so the layout dropdown can
            //offer "user saved positions". The layout itself is never switched automatically — it stays
            //whatever the user selected, and positions are only placed when that layout is active.
            dataHasPinnedPositions = DataHasSavedPositions();

            //Only the user-saved layout places nodes at the stored browser arrangement; committed gdbv
            //positions are placed by the draw itself (drawCytoscapeGraph / the 3D fx/fy/fz) when active.
            var activeLayout = visualizationMode == 2 ? cy2dLayout : g3dLayout;
            if (activeLayout == UserSavedLayout)
                await ApplySavedPositionsAsync();
        }

        //Re-apply the label filter to the freshly drawn graph.
        if (hiddenLabels.Count > 0)
            await ApplyLabelFilterAsync();

        await SaveTabsAsync();
        StateHasChanged();
    }

    #region Saved node positions

    //Captures the current node arrangement from the live 2D / 3D view and stages property-set
    //queries (gdbvX / gdbvY / gdbvZ) into the Generated buffer for the user to review and commit.
    private async Task SavePositionsAsync()
    {
        var json = await GraphView.GetPositionsAsync();

        if (json == null)
            return;

        List<NodePosition> positions;

        try
        {
            positions = JsonSerializer.Deserialize<List<NodePosition>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return;
        }

        if (positions == null || positions.Count == 0)
            return;

        bool is3d = visualizationMode == 3;

        //2D and 3D pin to separate gdbv keys so the two arrangements never disturb each other.
        string xKey, yKey, zKey;
        if (is3d)
        {
            xKey = GdbvKeys.X3d;
            yKey = GdbvKeys.Y3d;
            zKey = GdbvKeys.Z3d;
        }
        else
        {
            xKey = GdbvKeys.X;
            yKey = GdbvKeys.Y;
            zKey = null;
        }

        //Only stage updates for nodes the user actually moved: compare each live position with what's
        //already stored on the graph (committed positions for this viewer) and skip the ones that match.
        var stored = StoredPositions(xKey, yKey, zKey);
        const double tolerance = 0.5;

        foreach (var p in positions)
        {
            if (string.IsNullOrEmpty(p.Id))
                continue;

            if (stored.TryGetValue(p.Id, out var s)
                && Math.Abs(p.X - s.X) <= tolerance
                && Math.Abs(p.Y - s.Y) <= tolerance
                && (!is3d || (s.Z.HasValue && Math.Abs(p.Z - s.Z.Value) <= tolerance)))
                continue;

            AppendGeneratedQuery(Qb.SetProperty("node", p.Id, xKey, p.X.ToString(CultureInfo.InvariantCulture), null));
            AppendGeneratedQuery(Qb.SetProperty("node", p.Id, yKey, p.Y.ToString(CultureInfo.InvariantCulture), null));

            if (is3d)
                AppendGeneratedQuery(Qb.SetProperty("node", p.Id, zKey, p.Z.ToString(CultureInfo.InvariantCulture), null));
        }

        //Keep the arrangement in the browser (separately per viewer) and select it as the "user saved
        //positions" layout, so it survives a reload even before the gdbv* queries are committed to the DB.
        CurrentSavedPositionsJson = json;
        if (is3d)
            g3dLayout = UserSavedLayout;
        else
            cy2dLayout = UserSavedLayout;

        //Reveal the Generated tab so the user can review and Commit the position updates.
        queryEditorTab = 2;

        await SaveTabsAsync();
        await PersistTabTextAsync();
    }

    //Whether the loaded graph data already carries committed positions for the current viewer (gdbvX for
    //2D, gdbvX3d for 3D) on any vertex.
    private bool DataHasSavedPositions()
    {
        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return false;

        string xKey;
        if (visualizationMode == 3)
            xKey = GdbvKeys.X3d;
        else
            xKey = GdbvKeys.X;

        try
        {
            var table = GraphDataConverter.ToTable(data);
            return table.Nodes.Any(n => n.Properties.ContainsKey(xKey));
        }
        catch
        {
            return false;
        }
    }

    //The positions currently stored on the loaded graph for the given viewer's keys, keyed by node id, so
    //a Save-positions run can stage updates only for the nodes that have actually moved.
    private Dictionary<string, (double X, double Y, double? Z)> StoredPositions(string xKey, string yKey, string zKey)
    {
        var result = new Dictionary<string, (double X, double Y, double? Z)>();

        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return result;

        try
        {
            var table = GraphDataConverter.ToTable(data);
            foreach (var n in table.Nodes)
            {
                if (string.IsNullOrEmpty(n.Id))
                    continue;

                if (!n.Properties.TryGetValue(xKey, out var xs) || !n.Properties.TryGetValue(yKey, out var ys))
                    continue;

                if (!double.TryParse(xs, NumberStyles.Any, CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(ys, NumberStyles.Any, CultureInfo.InvariantCulture, out var y))
                    continue;

                double? z = null;
                if (zKey != null && n.Properties.TryGetValue(zKey, out var zs)
                    && double.TryParse(zs, NumberStyles.Any, CultureInfo.InvariantCulture, out var zv))
                    z = zv;

                result[n.Id] = (x, y, z);
            }
        }
        catch { }

        return result;
    }

    //Places nodes at the browser-stored "user saved positions" for whichever graph view is live (the 2D
    //and 3D arrangements are cached separately).
    private async Task ApplySavedPositionsAsync()
    {
        if (string.IsNullOrEmpty(CurrentSavedPositionsJson))
            return;

        await GraphView.ApplyPositionsAsync(CurrentSavedPositionsJson);
    }

    #endregion
}
