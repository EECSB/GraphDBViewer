using System.Text.Json;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Element selection and editing: the JS-invoked graph-click callbacks, the node-linking and delete
//pick modes, the sidebar property editor, and committing the staged (Generated tab) queries.
public partial class Home
{

    //When on, clicking a node or edge in the graph stages a drop query for it (like the sidebar delete)
    //instead of selecting it. Mutually exclusive with the node-linking picker.
    private bool deleteMode;

    //Node-linking picker: when on, picking a source and a target creates the edge immediately (with the
    //current label) and resets to the source slot, so several edges can be linked by clicking alone —
    //no need to press "Link nodes" each time. On by default.
    private bool continuousLink = true;

    //When on, clicking empty canvas drops a new component where you click (an auto-numbered temporary
    //name). A graph-click mode like delete mode / the picker — only one can be active at a time.
    private bool addNodeMode;

    //When on (2D only), a grid overlay is drawn on the canvas and components snap to it as they're
    //dragged or placed. Orthogonal to the click-modes above — it can stay on alongside them.
    private bool gridMode;

    //Used when the picker's edge-label box is left empty, so two nodes can still be linked in one
    //click. Matches the input's placeholder text.
    private const string DefaultEdgeLabel = "edge label";

    //The picker can create an edge as soon as both endpoints are chosen; the label defaults if blank.
    private bool CanCreatePickedEdge => !string.IsNullOrEmpty(pickFirstNodeId)
        && !string.IsNullOrEmpty(pickSecondNodeId);

    private string GdbvImageValue => selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Image);
    private string GdbvModelValue => selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Model);

    //Reachability of image/model URLs the user has entered (URL → result, or Reachable=null while
    //checking), used to color the input border green (available) or red (not) and to surface the error.
    private readonly Dictionary<string, UrlCheckState> urlChecks = new();

    //Whether the user has hand-toggled the image / model "show" checkbox for the current selection. Once
    //they have, a passing URL re-check won't auto-flip it back on — their choice stands. A fresh paste
    //(SetGdbvUrlAsync) and a new selection both clear these, re-arming auto-show.
    private bool imageShowUserSet;
    private bool modelShowUserSet;

    //The URL whose failure popup is currently shown above its input (null = none), and the timer that
    //auto-hides it after a few seconds.
    private string urlPopupUrl;
    private System.Threading.CancellationTokenSource urlPopupCts;

    //Result of an image/model URL reachability probe (see UrlCheckInterop.js): Reachable is null while the
    //check is in flight, then true/false; Error carries the exact failure reason when it isn't reachable.
    private class UrlCheckState
    {
        public bool? Reachable { get; set; }
        public string Error { get; set; }
    }

    #region Selection (called from JS)

    //Double-click a node to fetch its neighbors and merge them into the current graph.
    [JSInvokable]
    public async Task OnNodeExpand(string id)
    {
        //Traverse is checked explicitly: a database that can't be traversed from an id still draws a
        //graph you can double-click (a SPARQL CONSTRUCT does), and this used to be a no-op only because
        //the Gremlin client happened to be null for it.
        if (!Caps.Traverse || db == null || !isConnected || string.IsNullOrEmpty(id))
            return;

        var result = await db.ExecuteAsync(Qb.Neighbors(id, expansionLimit));
        if (result.IsError)
            return;

        //The on-screen graph may live in lastResultData (a query result) rather than graphResultData —
        //seed the working set from whichever holds it, so the neighbors merge into (not replace) what's
        //shown and the clicked node keeps its own properties. The Neighbors query returns that node only
        //as an edge endpoint, so without the merge it would collapse to a bare id.
        var current = CurrentGraphData();
        bool hasCurrent = current.ValueKind != JsonValueKind.Undefined;

        if (hasCurrent)
            graphResultData = GraphDataConverter.MergeGraphResults(current, result.Data);
        else
            graphResultData = result.Data;

        //Append the new neighbors incrementally so every existing node — including the clicked one —
        //keeps its position (the neighbors lay out around the locked nodes). Fall back to a full render
        //only when nothing is drawn yet or we're not in a graph view.
        if (!hasCurrent || (visualizationMode != 2 && visualizationMode != 3))
        {
            await RenderGraphAsync();
            return;
        }

        if (visualizationMode == 2)
            await JS.InvokeVoidAsync("addToCytoscapeGraph", GraphDataConverter.ToCytoscapeJson(graphResultData, labelStyles, edgeColorMode, edgeColors));
        else
            await JS.InvokeVoidAsync("graph3DInterop.addData", GraphDataConverter.ToForceGraphJson(graphResultData, labelStyles, edgeColorMode, edgeColors));

        await SaveTabsAsync();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnElementSelected(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString();
        var id = root.GetProperty("id").ToString();

        //Add-component mode places new nodes on empty-canvas clicks (handled in OnCanvasTapped); ignore
        //clicks that land on an existing node/edge so they don't select while you're stamping components.
        if (addNodeMode)
            return;

        //Delete mode: a graph click stages a drop query (deduped) and surfaces it in the Generated tab,
        //without selecting the element. Stays on so several can be deleted in a row. When the clicked
        //element is one of several highlighted (shift/box-selected) components, the whole highlighted group
        //is dropped at once. A node's drop also drops its loaded incident edges (StageElementDrop), so no
        //edge lingers on a database that doesn't cascade a vertex delete.
        if (deleteMode)
        {
            foreach (var el in DeleteModeTargets(root, type, id))
                StageElementDrop(el.Type, el.Id);

            queryEditorTab = 2;
            StateHasChanged();

            return;
        }

        //Node-linking picker: a graph click fills the active slot (source or target) with the node's
        //name and id, then advances from the source slot to the target slot automatically.
        if (connectionPickMode == "twoClick" && type == "node")
        {
            string clickedName;
            if (root.TryGetProperty("label", out var pl) && !string.IsNullOrWhiteSpace(pl.GetString()))
                clickedName = pl.GetString();
            else
                clickedName = id;

            if (pickActiveSlot == 2)
            {
                pickSecondNodeId = id;
                pickSecondNodeName = clickedName;

                //Continuous link: as soon as both endpoints are picked, create the edge with the current
                //label and reset to the source slot for the next pair — no need to press "Link nodes".
                if (continuousLink && CanCreatePickedEdge)
                    CreatePickedEdge();
            }
            else
            {
                pickFirstNodeId = id;
                pickFirstNodeName = clickedName;
                pickActiveSlot = 2;
            }

            StateHasChanged();

            return;
        }

        //Single-click "fill" mode: populate the "Other vertex ID" field instead of selecting.
        if (connectionPickMode == "fill" && type == "node" && selectedElement != null && selectedElement.Type == "node" && id != selectedElement.Id)
        {
            newConnectionOtherId = id;
            connectionPickMode = null;

            StateHasChanged();

            return;
        }

        string label;
        if (root.TryGetProperty("label", out var lbl))
            label = lbl.GetString();
        else
            label = "";

        string glabel;
        if (root.TryGetProperty("glabel", out var gl))
            glabel = gl.GetString();
        else
            glabel = "";

        var info = new SelectedElementInfo
        {
            Type = type,
            Id = id,
            IdType = IdTypeFor(type, id),
            Label = label,
            GLabel = glabel
        };

        if (root.TryGetProperty("source", out var src))
            info.Source = src.ToString();

        if (root.TryGetProperty("target", out var tgt))
            info.Target = tgt.ToString();

        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
                info.EditableProperties[p.Name] = p.Value.ToString();
        }

        selectedElement = info;
        originalProperties = new Dictionary<string, string>(info.EditableProperties);
        removedProperties.Clear();
        sessionEditedKeys.Clear();
        //A new selection re-arms auto-show for its own image/model (their "show" reflects this node's data).
        imageShowUserSet = false;
        modelShowUserSet = false;
        newPropertyName = null;
        newPropertyValue = null;
        hasPropertyChanges = false;
        commitStatus = null;
        //A normal selection click cancels any active pick mode (and clears the picked endpoints).
        connectionPickMode = null;
        ResetPickState();
        StateHasChanged();

        //Same as OnNodeExpand: only ask the database for the element's edges when it can actually be
        //traversed. LoadElementConnectionsAsync swallows its errors, so a wrong guess here fails silently.
        if (Caps.Traverse && db != null)
            await LoadElementConnectionsAsync(info);
    }

    [JSInvokable]
    public void OnElementDeselected()
    {
        selectedElement = null;
        hasPropertyChanges = false;
        commitStatus = null;

        StateHasChanged();
    }

    //An empty-canvas click in the 2D view, with the click's graph coordinates. In add-component mode it
    //drops a new node right there; otherwise it's a plain deselect (the previous background-tap behavior).
    [JSInvokable]
    public async Task OnCanvasTapped(double x, double y)
    {
        if (addNodeMode)
        {
            await PlaceComponentAsync(x, y);
            return;
        }

        OnElementDeselected();
    }

    //An empty-space click in the 3D view (no usable position). Adds a force-positioned node in
    //add-component mode, else deselects.
    [JSInvokable]
    public async Task OnCanvasTapped3d()
    {
        if (addNodeMode)
        {
            await PlaceComponentAsync(null, null);
            return;
        }

        OnElementDeselected();
    }

    [JSInvokable]
    public async Task OnDeleteKeyPressed()
    {
        //The 2D canvas can hold several shift / box-selected components at once, while selectedElement only
        //tracks the last-clicked one. Delete everything Cytoscape currently has selected; each element is
        //staged exactly like a single delete (a node also drops its incident edges).
        if (visualizationMode == 2)
        {
            var selection = await GetCytoscapeSelectionAsync();

            if (selection.Count > 0)
            {
                foreach (var el in selection)
                    StageElementDrop(el.Type, el.Id);

                queryEditorTab = 2;

                //The selection is staged for deletion now — close the property panel like a single delete.
                selectedElement = null;
                hasPropertyChanges = false;
                commitStatus = null;

                StateHasChanged();

                return;
            }
        }

        if (selectedElement == null)
            return;

        DeleteSelectedElement();

        StateHasChanged();
    }

    //The elements currently selected on the 2D canvas (Cytoscape's own selection — a shift / box-select can
    //hold several), as (type, id) pairs. Empty when the 2D graph isn't live or nothing is selected.
    private async Task<List<(string Type, string Id)>> GetCytoscapeSelectionAsync()
    {
        var selection = new List<(string Type, string Id)>();

        string json;
        try
        {
            json = await JS.InvokeAsync<string>("getCytoscapeSelectedElements");
        }
        catch
        {
            return selection;
        }

        if (string.IsNullOrWhiteSpace(json))
            return selection;

        try
        {
            using var doc = JsonDocument.Parse(json);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idEl))
                    continue;

                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id))
                    continue;

                string type;
                if (el.TryGetProperty("type", out var typeEl))
                    type = typeEl.GetString();
                else
                    type = "node";

                selection.Add((type, id));
            }
        }
        catch { }

        return selection;
    }

    //The elements a delete-mode click should drop: the whole highlighted group when the clicked element was
    //one of several highlighted (shift/box-selected) components, otherwise just the clicked element. The
    //group rides along in the click payload (selectedGroup) because a single-select collapses it to the
    //clicked element moments after the click, so the live selection can't be read back for it.
    private static List<(string Type, string Id)> DeleteModeTargets(JsonElement root, string type, string id)
    {
        var group = ParseSelectedGroup(root);

        if (group.Count >= 2 && group.Any(g => g.Id == id))
            return group;

        return new List<(string Type, string Id)> { (type, id) };
    }

    //Parses the click payload's selectedGroup array ([{type, id}] — the components highlighted at click
    //time) into (type, id) pairs. Empty when the field is absent (e.g. a table-view selection).
    private static List<(string Type, string Id)> ParseSelectedGroup(JsonElement root)
    {
        var group = new List<(string Type, string Id)>();

        if (!root.TryGetProperty("selectedGroup", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return group;

        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var elId = idEl.GetString();
            if (string.IsNullOrEmpty(elId))
                continue;

            string elType;
            if (el.TryGetProperty("type", out var typeEl))
                elType = typeEl.GetString();
            else
                elType = "node";

            group.Add((elType, elId));
        }

        return group;
    }

    //Selects a vertex/edge clicked in the table view, reusing the graph-click selection path
    //(populates the property editor and loads its connections).
    private async Task SelectFromTableAsync(GraphDataConverter.GraphRow row, string type)
    {
        var payload = new Dictionary<string, object>
        {
            ["type"] = type,
            ["id"] = row.Id,
            ["label"] = row.Label,
            ["glabel"] = row.Label,
            ["properties"] = row.Properties
        };

        if (type == "edge")
        {
            payload["source"] = row.Source ?? "";
            payload["target"] = row.Target ?? "";
        }

        await OnElementSelected(JsonSerializer.Serialize(payload));
    }

    private async Task LoadElementConnectionsAsync(SelectedElementInfo info)
    {
        try
        {
            if (info.Type == "node")
            {
                info.InEdges.AddRange(Qb.ParseEdgeList(await db.ExecuteAsync(Qb.InEdges(info.Id))));
                info.OutEdges.AddRange(Qb.ParseEdgeList(await db.ExecuteAsync(Qb.OutEdges(info.Id))));
            }
            else
            {
                if (!string.IsNullOrEmpty(info.Source))
                {
                    var srcResult = await db.ExecuteAsync(Qb.VertexDisplayLabel(info.Source));
                    info.SourceLabel = Qb.ParseDisplayLabel(srcResult) ?? info.SourceLabel;
                }

                if (!string.IsNullOrEmpty(info.Target))
                {
                    var tgtResult = await db.ExecuteAsync(Qb.VertexDisplayLabel(info.Target));
                    info.TargetLabel = Qb.ParseDisplayLabel(tgtResult) ?? info.TargetLabel;
                }
            }
        }
        catch { }

        StateHasChanged();
    }

    //The GraphSON id type of the vertex / edge with this id in the currently displayed graph, or null.
    //Gremlin is type-strict on edge ids, so a drop / property edit must format a Long id as 123L.
    private string IdTypeFor(string type, string id)
    {
        return GraphDataConverter.FindIdType(CurrentGraphData(), id, type == "edge");
    }

    #endregion


    #region Pick modes

    private void EnableConnectionPickMode()
    {
        if (selectedElement != null && selectedElement.Type == "node")
            connectionPickMode = "fill";
    }

    //Opens or closes the node-linking picker popup over the canvas.
    private void ToggleTwoClickPickMode()
    {
        if (connectionPickMode == "twoClick")
        {
            ClosePickPopup();
        }
        else
        {
            deleteMode = false;
            addNodeMode = false;
            connectionPickMode = "twoClick";
            ResetPickState();
        }
    }

    private void ToggleDeleteMode()
    {
        deleteMode = !deleteMode;

        //Delete mode, add-component mode and the node-linking picker all intercept graph clicks —
        //only one can be active at a time.
        if (deleteMode)
        {
            addNodeMode = false;
            connectionPickMode = null;
            ResetPickState();
        }
    }

    //Toggles add-component mode. While on, an empty-canvas click drops a new component where you click
    //(handled by OnCanvasTapped). Turning it on makes sure clicks land on a live, previewing graph
    //canvas: an empty base if nothing is loaded, the uncommitted preview on, and a 2D/3D view active.
    //Grid mode (2D only): toggles the grid overlay + snapping in the Cytoscape interop. Purely a canvas
    //editing aid — nothing is persisted; a re-render re-applies it (see RenderGraphAsync).
    private async Task ToggleGridModeAsync()
    {
        gridMode = !gridMode;
        await JS.InvokeVoidAsync("setCytoscapeGridMode", gridMode);
    }

    private async Task ToggleAddNodeMode()
    {
        addNodeMode = !addNodeMode;

        if (!addNodeMode)
            return;

        deleteMode = false;
        connectionPickMode = null;
        ResetPickState();

        //Only redraw when the canvas isn't already a live graph view with data; otherwise just flip the
        //preview on and let the first placement reconcile in place, so an existing arrangement isn't
        //re-laid-out.
        bool needRender = (visualizationMode != 2 && visualizationMode != 3)
            || CurrentGraphData().ValueKind == JsonValueKind.Undefined;

        if (CurrentGraphData().ValueKind == JsonValueKind.Undefined)
        {
            var empty = JsonDocument.Parse("[]").RootElement;
            lastResultData = empty;
            graphResultData = empty;
            queryResults = "[]";
        }

        reflectDbState = false;

        if (visualizationMode != 2 && visualizationMode != 3)
            visualizationMode = 2;

        if (needRender)
            await RenderGraphAsync();
    }

    //Queues the addE query for the picked pair, then clears the endpoints (keeping the label and the
    //open picker) so successive edges can be linked without reopening it.
    private void CreatePickedEdge()
    {
        if (!CanCreatePickedEdge)
            return;

        var label = string.IsNullOrWhiteSpace(pickEdgeLabel) ? DefaultEdgeLabel : pickEdgeLabel.Trim();
        AppendGeneratedQuery(Qb.AddEdge(pickFirstNodeId, label, pickSecondNodeId));
        queryEditorTab = 2;

        pickFirstNodeId = null;
        pickFirstNodeName = null;
        pickSecondNodeId = null;
        pickSecondNodeName = null;
        pickActiveSlot = 1;
    }

    private void ClosePickPopup()
    {
        connectionPickMode = null;
        ResetPickState();
    }

    //Clears the picked endpoints and resets the active slot back to the source.
    private void ResetPickState()
    {
        pickFirstNodeId = null;
        pickFirstNodeName = null;
        pickSecondNodeId = null;
        pickSecondNodeName = null;
        pickActiveSlot = 1;
    }

    #endregion


    #region Property editing

    private void OnPropertyChanged(string key, string newValue)
    {
        if (selectedElement == null)
            return;

        selectedElement.EditableProperties[key] = newValue ?? "";
        sessionEditedKeys.Add(key);
        hasPropertyChanges = selectedElement.EditableProperties
            .Any(kv => !originalProperties.TryGetValue(kv.Key, out var orig) || orig != kv.Value);

        RegenerateChangeQueries();
    }

    private void AddProperty()
    {
        if (selectedElement == null || string.IsNullOrWhiteSpace(newPropertyName))
            return;

        selectedElement.EditableProperties[newPropertyName.Trim()] = newPropertyValue ?? "";
        removedProperties.Remove(newPropertyName.Trim());
        sessionEditedKeys.Add(newPropertyName.Trim());
        newPropertyName = null;
        newPropertyValue = null;
        hasPropertyChanges = true;
        RegenerateChangeQueries();
    }

    private void RemoveProperty(string key)
    {
        if (selectedElement == null)
            return;

        selectedElement.EditableProperties.Remove(key);
        removedProperties.Add(key);
        sessionEditedKeys.Add(key);
        hasPropertyChanges = true;
        RegenerateChangeQueries();
    }

    //The property keys the selected node currently displays beneath itself on the canvas (its gdbvShow
    //list — a comma-separated set of property keys).
    private List<string> ShownPropertyKeys()
    {
        var raw = selectedElement?.EditableProperties.GetValueOrDefault(GdbvKeys.Show);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private bool IsPropertyShown(string key)
    {
        return ShownPropertyKeys().Contains(key);
    }

    //Toggles whether a property is shown beneath the node — updates the node's gdbvShow list, staged
    //like any edit (and reflected on the canvas immediately while "Preview data changes" is on).
    private void SetPropertyShown(string key, bool show)
    {
        var keys = ShownPropertyKeys();

        if (show)
        {
            if (!keys.Contains(key))
                keys.Add(key);
        }
        else
        {
            keys.Remove(key);
        }

        if (keys.Count == 0)
            RemovePropertyIfPresent(GdbvKeys.Show);
        else
            OnPropertyChanged(GdbvKeys.Show, string.Join(",", keys));
    }

    //The image / model "show" checkbox: toggles the linked file's visibility like SetPropertyShown, but
    //also records that the user set it by hand so a later URL re-check won't override their choice.
    private void SetImageModelShown(string key, bool show)
    {
        if (key == GdbvKeys.Image)
            imageShowUserSet = true;
        else if (key == GdbvKeys.Model)
            modelShowUserSet = true;

        SetPropertyShown(key, show);
    }

    //Auto-enables "show" for a freshly pasted image / model the moment its URL checks out — unless the
    //user has already hand-toggled that checkbox this selection, in which case their choice is left alone.
    //The reachability probe is async, so a stale pass (field changed, or a different element now selected)
    //is ignored.
    private void AutoShowIfWorking(string kind, string url)
    {
        string key;
        string current;
        bool userSet;
        if (kind == "image")
        {
            key = GdbvKeys.Image;
            current = GdbvImageValue;
            userSet = imageShowUserSet;
        }
        else if (kind == "model")
        {
            key = GdbvKeys.Model;
            current = GdbvModelValue;
            userSet = modelShowUserSet;
        }
        else
            return;

        if (current != url)
            return;

        if (userSet || IsPropertyShown(key))
            return;

        SetPropertyShown(key, true);
    }

    //Sets an image/model gdbv* property and probes the URL's availability so the input can turn
    //green or red. kind is "image" or "model". Used by both the text input and the cloud picker.
    private async Task SetGdbvUrlAsync(string key, string kind, string url)
    {
        OnPropertyChanged(key, url);

        //A fresh paste re-arms auto-show: if this new URL checks out, its "show" turns on even when the
        //user had toggled a previous URL off.
        if (kind == "image")
            imageShowUserSet = false;
        else if (kind == "model")
            modelShowUserSet = false;

        await CheckUrlAsync(url, kind);
    }

    //Probes a pasted image/model URL and records whether it downloaded (and why not), re-rendering so the
    //input border reflects the result. On failure it also pops the exact error above the input for a few
    //seconds. Each kind is probed the way it will actually be loaded — an image through an <img> load, a
    //model through a fetch — which is why both live in JavaScript; UrlCheckInterop.js says why at length.
    private async Task CheckUrlAsync(string url, string kind)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        urlChecks[url] = new UrlCheckState { Reachable = null };
        StateHasChanged();

        bool reachable;
        string error;

        try
        {
            var result = await JS.InvokeAsync<JsonElement>("urlCheck.isReachable", url, kind);
            reachable = result.TryGetProperty("reachable", out var r) && r.ValueKind == JsonValueKind.True;

            if (result.TryGetProperty("error", out var e))
                error = e.GetString();
            else
                error = null;
        }
        catch (Exception ex)
        {
            reachable = false;
            error = ex.Message;
        }

        urlChecks[url] = new UrlCheckState { Reachable = reachable, Error = error };

        //A working image/model auto-turns-on its "show" (unless the user already set that toggle by hand).
        if (reachable)
            AutoShowIfWorking(kind, url);

        StateHasChanged();

        if (!reachable)
            _ = ShowUrlErrorPopupAsync(url);
    }

    //Shows the failure popup for a URL above its input, then auto-hides it after ten seconds. A newer
    //failure cancels the pending hide so its own popup gets the full window.
    private async Task ShowUrlErrorPopupAsync(string url)
    {
        urlPopupCts?.Cancel();
        urlPopupCts = new System.Threading.CancellationTokenSource();
        var token = urlPopupCts.Token;

        urlPopupUrl = url;
        StateHasChanged();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        urlPopupUrl = null;
        StateHasChanged();
    }

    //Border class for an image/model URL input from the last reachability check: green when the file
    //downloaded, red when it failed, and none while the check is pending or the field is empty.
    private string UrlBorderClass(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (!urlChecks.TryGetValue(url, out var state) || state.Reachable == null)
            return "";

        if (state.Reachable == true)
            return "border-success";
        else
            return "border-danger";
    }

    //True when the last reachability check for this URL failed — drives the warning triangle by the label.
    private bool HasUrlError(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return urlChecks.TryGetValue(url, out var state) && state.Reachable == false;
    }

    //The exact failure message from the last reachability check for this URL, shown in the popup.
    private string UrlError(string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && urlChecks.TryGetValue(url, out var state))
            return state.Error;

        return null;
    }

    private void RegenerateChangeQueries()
    {
        if (selectedElement == null)
            return;

        //Regenerate only the current element's staged property lines for the keys this edit session
        //touched, and merge them back over the buffer — other elements' edits (and this element's keys
        //staged in an earlier session) are preserved, so edits accumulate instead of overwriting.
        var lines = new List<string>();

        var changed = selectedElement.EditableProperties
            .Where(kv => !originalProperties.TryGetValue(kv.Key, out var orig) || orig != kv.Value)
            .ToList();

        foreach (var kv in changed)
            lines.Add(Qb.SetProperty(selectedElement.Type, selectedElement.Id, kv.Key, kv.Value, selectedElement.IdType));

        foreach (var key in removedProperties)
            lines.Add(Qb.DropProperty(selectedElement.Type, selectedElement.Id, key, selectedElement.IdType));

        generatedQueryText = GremlinEditParser.MergePropertyEdits(
            generatedQueryText, selectedElement.Type, selectedElement.Id, sessionEditedKeys, lines);

        if (lines.Count > 0)
            queryEditorTab = 2;
    }

    #endregion


    #region Staged-query commit & graph mutations

    private async Task CommitGeneratedQueriesAsync()
    {
        if (!Caps.StageEdits || string.IsNullOrWhiteSpace(generatedQueryText))
            return;

        //Committing staged queries needs a live connection. Offline (db == null) mirror the Run/Load path:
        //surface the message in the stats bar and pulse the connect button rather than silently doing nothing.
        if (db == null)
        {
            WarnNoConnection();
            return;
        }

        var ct = BeginQuery();
        commitStatus = null;

        try
        {
            //Walked the way the edit parser walked it, because the stand-in ids below are named after
            //positions in that walk. Blank entries are skipped, never renumbered.
            var statements = StagedIds.Split(generatedQueryText, Provider.EditorLanguage);

            var errors = new List<string>();

            //An element created earlier in this same batch is referred to by a stand-in id, since it had
            //no real one when the statement was written. Each id the database hands back is recorded
            //here, and every later statement is substituted before it is sent.
            var realIds = new Dictionary<string, ResolvedId>();

            //Each query is its own round-trip (Cosmos rejects multi-statement scripts, so they can't be
            //batched), and a large commit can run for tens of seconds — so report live progress. Repaint
            //at most ~50 times regardless of count, since re-rendering this large page every query would
            //itself slow the commit.
            int step = Math.Max(1, statements.Count / 50);
            int ran = 0;

            for (int i = 0; i < statements.Count; i++)
            {
                var statement = StagedIds.Resolve(statements[i], realIds);

                if (string.IsNullOrWhiteSpace(statement))
                    continue;

                //Nothing recorded it, so the statement that was to create it never ran — it failed, or it
                //is not in this buffer at all. Sending it would fail at the database with a message about
                //id types that says nothing about what actually went wrong.
                if (StagedIds.MentionsPlaceholder(statement))
                {
                    errors.Add($"{statement}: refers to an element that was never created, so it was skipped.");
                    continue;
                }

                var result = await db.ExecuteAsync(statement, ct);
                ran++;

                if (result.IsError)
                    errors.Add($"{statement}: {result.Error}");
                else if (StagedIds.TryReadCreatedId(result, out var newId, out var idType))
                    StagedIds.Record(realIds, i, newId, idType, Provider.EditorLanguage);

                if ((i + 1) % step == 0 || i == statements.Count - 1)
                {
                    commitStatus = $"Committing… {i + 1} / {statements.Count}";
                    commitStatusClass = "text-muted";
                    StateHasChanged();
                }
            }

            if (errors.Count > 0)
            {
                commitStatus = string.Join("\n", errors);
                commitStatusClass = "text-danger";
            }
            else
            {
                commitStatus = $"{ran} query(ies) committed.";
                commitStatusClass = "text-success";
                generatedQueryText = null;

                if (selectedElement != null)
                {
                    originalProperties = new Dictionary<string, string>(selectedElement.EditableProperties);
                    removedProperties.Clear();
                    sessionEditedKeys.Clear();
                    hasPropertyChanges = false;
                }

                //Committed edits may add new labels/keys — refresh autocomplete (still inside the query lock).
                await RefreshSchemaVocabularyAsync();
            }
        }
        catch (OperationCanceledException)
        {
            commitStatus = "Commit canceled.";
            commitStatusClass = "text-muted";
        }
        catch (Exception ex)
        {
            commitStatus = $"Error: {ex.Message}";
            commitStatusClass = "text-danger";
        }
        finally
        {
            EndQuery();
        }

        //Auto-reload the graph from DB if the user has opted in and the commit succeeded.
        if (autoReloadAfterCommit && commitStatusClass == "text-success")
            await LoadDbAsync();
    }

    //Discards every staged (uncommitted) edit: clears the Generated buffer and the pending property
    //removals, then redraws the canvas back to the plain database state. Guarded by a confirm since
    //the staged queries are lost. RenderGraphAsync clears the selection / property-change flags.
    private async Task DiscardGeneratedQueriesAsync()
    {
        if (string.IsNullOrWhiteSpace(generatedQueryText))
            return;

        bool confirmed = await JS.InvokeAsync<bool>("confirm", "Discard all uncommitted changes? The staged queries in the Generated tab will be cleared.");
        if (!confirmed)
            return;

        generatedQueryText = null;
        removedProperties.Clear();

        await PersistTabTextAsync();
        await RenderGraphAsync();
    }

    //True when the exact query line is already staged (so repeated delete clicks don't duplicate it).
    private bool IsQueryStaged(string query)
    {
        return (generatedQueryText ?? "").Split('\n').Contains(query);
    }

    //Every loaded edge incident to the vertex (either endpoint), with its GraphSON id type, de-duplicated.
    private List<(string Id, string IdType)> LoadedIncidentEdges(string vertexId)
    {
        var edges = new List<(string Id, string IdType)>();
        var seen = new HashSet<string>();

        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return edges;

        foreach (var e in GraphDataConverter.ToTable(data).Edges)
            if (e.Id != null && (e.Source == vertexId || e.Target == vertexId) && seen.Add(e.Id))
                edges.Add((e.Id, e.IdType));

        return edges;
    }

    //Stages a vertex delete: every incident edge is dropped first (deduped), then the vertex itself, so
    //no edge is left dangling on a database that doesn't cascade a vertex delete to its edges. Each id is
    //formatted with its GraphSON type so a Long edge id drops as g.E(123L) rather than silently no-op.
    private void StageVertexDrop(string vertexId, string vertexIdType, IEnumerable<(string Id, string IdType)> incidentEdges)
    {
        foreach (var edge in incidentEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id) || edge.Id == "?")
                continue;

            var dropEdge = Qb.DropEdge(edge.Id, edge.IdType);
            if (!IsQueryStaged(dropEdge))
                AppendGeneratedQuery(dropEdge);
        }

        //Not every engine can remove a node at all: GUN's data lives at the root of its graph, which only
        //accepts nodes, so what makes a node go away there is exactly the edge drops above. The refusal
        //explains that rather than stopping the delete half-done.
        var dropVertex = ComposedOrExplained(() => Qb.DropVertex(vertexId, vertexIdType));

        if (dropVertex != null && !IsQueryStaged(dropVertex))
            AppendGeneratedQuery(dropVertex);
    }

    ///<summary>
    ///Runs a query builder that may refuse. A builder throws when the engine genuinely cannot express
    ///something — not when the viewer asked wrongly — so the reason is shown and the rest of the operation
    ///carries on, rather than the click dying on an unhandled exception.
    ///</summary>
    private string ComposedOrExplained(Func<string> compose)
    {
        try
        {
            return compose();
        }
        catch (NotSupportedException ex)
        {
            queryError = ex.Message;

            return null;
        }
    }

    private void DeleteSelectedElement()
    {
        if (selectedElement == null)
            return;

        StageElementDrop(selectedElement.Type, selectedElement.Id);

        queryEditorTab = 2;

        //Close the property panel right away — the element is now staged for deletion, so leaving it
        //open on the deleted element until the next selection is confusing.
        selectedElement = null;
        hasPropertyChanges = false;
        commitStatus = null;
    }

    //Stages the drop(s) for one selected node or edge. A node also drops its incident edges so none is left
    //dangling on a database that doesn't cascade a vertex delete: the loaded on-canvas edges always, plus —
    //when the node is the tracked selectedElement — its full DB-queried incident edges (which may reach nodes
    //that aren't on the canvas). Deduped by StageVertexDrop / IsQueryStaged; ids carry their GraphSON type.
    private void StageElementDrop(string type, string id)
    {
        if (type == "node")
        {
            IEnumerable<(string Id, string IdType)> incident = LoadedIncidentEdges(id);

            //InEdges/OutEdges are the DB-queried incident edges (complete), loaded only for the tracked
            //element; the loaded-graph edges cover the offline / pasted-data case and every other selected node.
            if (selectedElement != null && selectedElement.Type == "node" && selectedElement.Id == id)
                incident = selectedElement.InEdges.Concat(selectedElement.OutEdges)
                    .Select(e => (Id: e.EdgeId, IdType: e.EdgeIdType))
                    .Concat(incident);

            StageVertexDrop(id, IdTypeFor("node", id), incident);
        }
        else
        {
            var dropEdge = Qb.DropEdge(id, IdTypeFor("edge", id));

            if (!IsQueryStaged(dropEdge))
                AppendGeneratedQuery(dropEdge);
        }
    }

    private void CreateComponent()
    {
        if (string.IsNullOrWhiteSpace(newComponentLabel))
            return;

        AppendGeneratedQuery(Qb.AddVertex(newComponentLabel));
        newComponentLabel = null;
        queryEditorTab = 2;
    }

    //Places a fresh component with an auto-numbered temporary name (Component 1, Component 2, …), pinned
    //at (x, y) when a click position is given (2D) or force-positioned when not (3D). The name lives in
    //an editable "name" property so it can be renamed later. Reconciles the new node onto the canvas in
    //place, so existing nodes keep their positions.
    private async Task PlaceComponentAsync(double? x, double? y)
    {
        var name = NextComponentName();

        string query;
        if (x.HasValue && y.HasValue)
            query = Qb.AddVertexWithNameAt("Component", name, x.Value, y.Value);
        else
            query = Qb.AddVertexWithName("Component", name);

        AppendGeneratedQuery(query);
        queryEditorTab = 2;

        await SyncOptimisticViewAsync();
        await SaveTabsAsync();
        await PersistTabTextAsync();
        StateHasChanged();
    }

    //The next unused "Component N" temporary name, scanning both the loaded graph's display names and the
    //staged additions so repeated quick-adds keep counting up instead of colliding.
    private string NextComponentName()
    {
        int max = 0;

        void Consider(string label)
        {
            if (string.IsNullOrEmpty(label) || !label.StartsWith("Component "))
                return;

            if (int.TryParse(label.Substring("Component ".Length), out var n) && n > max)
                max = n;
        }

        var data = CurrentGraphData();
        if (data.ValueKind != JsonValueKind.Undefined)
            foreach (var name in GraphDataConverter.SearchSuggestions(data, labelStyles))
                Consider(name);

        foreach (var edit in GremlinEditParser.Parse(generatedQueryText))
            if (edit.Kind == GraphEditKind.AddNode)
                Consider(edit.Properties.GetValueOrDefault("name"));

        return $"Component {max + 1}";
    }

    private void RemoveConnection(EdgeInfo edge)
    {
        if (edge == null || string.IsNullOrWhiteSpace(edge.EdgeId) || edge.EdgeId == "?")
            return;

        AppendGeneratedQuery(Qb.DropEdge(edge.EdgeId, edge.EdgeIdType));
        queryEditorTab = 2;
    }

    private void AddConnection()
    {
        if (selectedElement == null || selectedElement.Type != "node" || string.IsNullOrWhiteSpace(newConnectionLabel) || string.IsNullOrWhiteSpace(newConnectionOtherId))
            return;

        var other = newConnectionOtherId.Trim();

        string query;
        if (newConnectionDirection == "in")
            query = Qb.AddEdge(other, newConnectionLabel, selectedElement.Id);
        else
            query = Qb.AddEdge(selectedElement.Id, newConnectionLabel, other);

        AppendGeneratedQuery(query);
        newConnectionLabel = null;
        newConnectionOtherId = null;
        queryEditorTab = 2;
    }

    private void AppendGeneratedQuery(string query)
    {
        if (!string.IsNullOrWhiteSpace(generatedQueryText))
            generatedQueryText += "\n" + query;
        else
            generatedQueryText = query;
    }

    #endregion
}
