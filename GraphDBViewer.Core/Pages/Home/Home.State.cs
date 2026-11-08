using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The page's workspace state: the open query tabs and the delegating properties that surface the
//active tab's members, so the rest of the code reads/writes plain fields.
public partial class Home
{

    #region Connections

    //The viewer's connections, each with its own query tabs, one in front (the multiple-connections spec, 2026-11-03).
    //The page reads the one in front's members through the delegating fields below and in Home.Connections.cs.
    private List<ViewerConnection> openConnections = new() { new ViewerConnection() };
    private int activeConnectionIndex;
    private ViewerConnection activeConnection => openConnections[activeConnectionIndex];

    #endregion


    #region Query tabs

    //Each tab is an independent query workspace (query text + its own results, view mode and selection), the connection
    //in front's.
    private List<QueryTab> tabs { get => activeConnection.Tabs; set => activeConnection.Tabs = value; }
    private int activeTabIndex { get => activeConnection.ActiveTabIndex; set => activeConnection.ActiveTabIndex = value; }
    private QueryTab activeTab => tabs[activeTabIndex];

    #endregion


    #region Active-tab query workspace

    //These delegate to the active QueryTab so the rest of the code is unchanged.
    private string queryText { get => activeTab.QueryText; set => activeTab.QueryText = value; }
    private string editorLanguage { get => activeTab.EditorLanguage; set => activeTab.EditorLanguage = value; }//Monaco highlighting: gremlin/cypher/sparql
    private string generatedQueryText
    {
        get => activeTab.GeneratedQueryText;
        set
        {
            activeTab.GeneratedQueryText = value;

            //Every change to the staged buffer refreshes the optimistic canvas preview (a no-op while
            //"reflect database state" is on). Fire-and-forget — the reconcile is guarded and idempotent.
            _ = SyncOptimisticViewAsync();
        }
    }
    private int queryEditorTab { get => activeTab.QueryEditorTab; set => activeTab.QueryEditorTab = value; }//1 = Query, 2 = Generated, 3 = Examples
    private string queryResults { get => activeTab.QueryResults; set => activeTab.QueryResults = value; }
    private string queryError { get => activeTab.QueryError; set => activeTab.QueryError = value; }//last-run error, shown as a red bar over the stats
    private GraphDbTable tableResult { get => activeTab.TableResult; set => activeTab.TableResult = value; }//last row/boolean result (a graph answer flows through lastResultData instead)
    private bool jsonCopied { get => activeTab.JsonCopied; set => activeTab.JsonCopied = value; }
    private int visualizationMode { get => activeTab.VisualizationMode; set => activeTab.VisualizationMode = value; }
    private string searchTerm { get => activeTab.SearchTerm; set => activeTab.SearchTerm = value; }//graph search box
    private bool showFilter { get => activeTab.ShowFilter; set => activeTab.ShowFilter = value; }//label-filter dropdown open
    private HashSet<string> hiddenLabels { get => activeTab.HiddenLabels; set => activeTab.HiddenLabels = value; }//vertex type-labels currently hidden
    private JsonElement lastResultData { get => activeTab.LastResultData; set => activeTab.LastResultData = value; }
    private JsonElement graphResultData { get => activeTab.GraphResultData; set => activeTab.GraphResultData = value; }
    private bool resultsCleared { get => activeTab.ResultsCleared; set => activeTab.ResultsCleared = value; }//canvas emptied via "Clear results" to build a new graph
    private double? lastQueryMs { get => activeTab.LastQueryMs; set => activeTab.LastQueryMs = value; }//elapsed ms of the last query that produced the shown results
    private int loadDbLimit { get => activeTab.LoadDbLimit; set => activeTab.LoadDbLimit = value; }
    //Past this many vertices a load is heavy enough to make the app sluggish, so the query-limit control
    //turns red, pulses, and warns on hover. "More than 250", so the check is strictly greater.
    private const int QueryLimitWarnThreshold = 250;
    private bool queryLimitHigh => loadDbLimit > QueryLimitWarnThreshold;

    //Hover text for the query-limit control: the slow-app warning when the limit is high, else the plain description.
    private string queryLimitTitle
    {
        get
        {
            if (queryLimitHigh)
                return "The query limit is high: the app may become slow. Lower it for large graphs.";

            return "Max vertices to load from the database";
        }
    }
    private bool autoReloadAfterCommit { get => activeTab.AutoReloadAfterCommit; set => activeTab.AutoReloadAfterCommit = value; }
    private bool reflectDbState { get => activeTab.ReflectDbState; set => activeTab.ReflectDbState = value; }//false = preview uncommitted edits on the canvas
    private string cy2dLayout { get => activeTab.Cy2dLayout; set => activeTab.Cy2dLayout = value; }//selected Cytoscape layout for 2D mode
    private string g3dLayout { get => activeTab.G3dLayout; set => activeTab.G3dLayout = value; }//selected 3d-force-graph dagMode for 3D mode
    private string mapStyle { get => activeTab.MapStyle; set => activeTab.MapStyle = value; }//which earth the Map and the Globe draw: satellite or outline
    private string mapDetail { get => activeTab.MapDetail; set => activeTab.MapDetail = value; }//how sharp the satellite earth is: standard or high
    private string saved2dPositionsJson { get => activeTab.Saved2dPositionsJson; set => activeTab.Saved2dPositionsJson = value; }//hand-pinned 2D arrangement (browser-stored)
    private string saved3dPositionsJson { get => activeTab.Saved3dPositionsJson; set => activeTab.Saved3dPositionsJson = value; }//hand-pinned 3D arrangement (browser-stored)

    //The browser-cached arrangement for the viewer currently shown, so the 2D and 3D pins stay independent.
    private string CurrentSavedPositionsJson
    {
        get
        {
            if (visualizationMode == 3)
                return saved3dPositionsJson;
            else
                return saved2dPositionsJson;
        }
        set
        {
            if (visualizationMode == 3)
                saved3dPositionsJson = value;
            else
                saved2dPositionsJson = value;
        }
    }
    private bool show3dVertexLabels { get => activeTab.Show3dVertexLabels; set => activeTab.Show3dVertexLabels = value; }//3D: always show vertex labels (off = hover only)
    private bool show3dEdgeLabels { get => activeTab.Show3dEdgeLabels; set => activeTab.Show3dEdgeLabels = value; }//3D: always show edge labels (off = hover only)
    private bool show3dProps { get => activeTab.Show3dProps; set => activeTab.Show3dProps = value; }//3D: show the "show"-marked properties beneath each node
    private int draw3dLabelDistance { get => activeTab.Draw3dLabelDistance; set => activeTab.Draw3dLabelDistance = value; }//3D: label draw distance 0–100 (100 = show all)
    private int draw3dNodeDistance { get => activeTab.Draw3dNodeDistance; set => activeTab.Draw3dNodeDistance = value; }//3D: node draw distance 0–100 (100 = show all)
    private int mapNodeSize { get => activeTab.MapNodeSize; set => activeTab.MapNodeSize = value; }//Map and Globe: node size, percent of the size kept at every zoom
    private string g3dExportFormat { get => activeTab.G3dExportFormat; set => activeTab.G3dExportFormat = value; }//selected 3D export format (obj/ply/stl)
    private string imageExportFormat { get => activeTab.ImageExportFormat; set => activeTab.ImageExportFormat = value; }//selected image export format (png/jpeg/svg)
    private string tableExportFormat { get => activeTab.TableExportFormat; set => activeTab.TableExportFormat = value; }//selected table export format (csv/xlsx)
    private Dictionary<string, LabelStyle> labelStyles { get => activeTab.LabelStyles; set => activeTab.LabelStyles = value; }//per-vertex-label color/size/display/icon
    private int edgeColorMode { get => activeTab.EdgeColorMode; set => activeTab.EdgeColorMode = value; }//edge coloring: 0=auto, 1=off, 2=custom
    private Dictionary<string, string> edgeColors { get => activeTab.EdgeColors; set => activeTab.EdgeColors = value; }//per-edge-label custom colors (custom mode)
    private List<string> edgeDetails { get => activeTab.EdgeDetails; set => activeTab.EdgeDetails = value; }//edge labels drawn as cards (Detailed view)

    #endregion


    #region Active-tab selection & editing state

    //Delegates to the active QueryTab, like the query workspace above.
    private SelectedElementInfo selectedElement { get => activeTab.SelectedElement; set => activeTab.SelectedElement = value; }
    private Dictionary<string, string> originalProperties { get => activeTab.OriginalProperties; set => activeTab.OriginalProperties = value; }
    private bool hasPropertyChanges { get => activeTab.HasPropertyChanges; set => activeTab.HasPropertyChanges = value; }
    private string commitStatus { get => activeTab.CommitStatus; set => activeTab.CommitStatus = value; }
    private string commitStatusClass { get => activeTab.CommitStatusClass; set => activeTab.CommitStatusClass = value; }
    private string newPropertyName { get => activeTab.NewPropertyName; set => activeTab.NewPropertyName = value; }
    private string newPropertyValue { get => activeTab.NewPropertyValue; set => activeTab.NewPropertyValue = value; }
    private string newComponentLabel { get => activeTab.NewComponentLabel; set => activeTab.NewComponentLabel = value; }
    private string newConnectionDirection { get => activeTab.NewConnectionDirection; set => activeTab.NewConnectionDirection = value; }
    private string newConnectionLabel { get => activeTab.NewConnectionLabel; set => activeTab.NewConnectionLabel = value; }
    private string newConnectionOtherId { get => activeTab.NewConnectionOtherId; set => activeTab.NewConnectionOtherId = value; }
    //null = off, "fill" = single-click fills "Other vertex ID" input, "twoClick" = two-click direct edge creation
    private string connectionPickMode { get => activeTab.ConnectionPickMode; set => activeTab.ConnectionPickMode = value; }
    private string pickEdgeLabel { get => activeTab.PickEdgeLabel; set => activeTab.PickEdgeLabel = value; }
    private string pickFirstNodeId { get => activeTab.PickFirstNodeId; set => activeTab.PickFirstNodeId = value; }
    private string pickFirstNodeName { get => activeTab.PickFirstNodeName; set => activeTab.PickFirstNodeName = value; }
    private string pickSecondNodeId { get => activeTab.PickSecondNodeId; set => activeTab.PickSecondNodeId = value; }
    private string pickSecondNodeName { get => activeTab.PickSecondNodeName; set => activeTab.PickSecondNodeName = value; }
    private int pickActiveSlot { get => activeTab.PickActiveSlot; set => activeTab.PickActiveSlot = value; }
    private HashSet<string> removedProperties { get => activeTab.RemovedProperties; set => activeTab.RemovedProperties = value; }
    private HashSet<string> sessionEditedKeys { get => activeTab.SessionEditedKeys; set => activeTab.SessionEditedKeys = value; }//property keys touched since the current element was selected

    #endregion


    #region Derived state

    //The on-canvas "uncommitted changes" banner shows only while the canvas is previewing staged edits.
    private bool ShowUncommittedBanner => !reflectDbState && !string.IsNullOrWhiteSpace(generatedQueryText);

    //True when there is graph data to show (from a query or from pasted JSON).
    private bool HasGraphData => lastResultData.ValueKind != JsonValueKind.Undefined || graphResultData.ValueKind != JsonValueKind.Undefined;

    //True exactly when Home.razor has the #cyGraph / #graph3d canvas div in the DOM. Everything that
    //draws into those containers — RenderGraphAsync and the restore path in OnAfterRenderAsync — MUST
    //gate on this, or the interop targets a container that was never rendered ("Cytoscape container not
    //found" / "3D graph container not found"). It mirrors the markup exactly: the working area renders
    //only when connected, in offline mode, or when there is restored / imported graph data (Home.razor's
    //outer @if); the debug overlay and a tabular (SPARQL) result each pre-empt the canvas; and the canvas
    //branch itself needs a non-empty result that parsed to a graph. This is the property that keeps the
    //markup and the renderer from drifting apart — the drift is exactly what caused the container errors
    //(e.g. on reload while disconnected: HasGraphData was true so the draw fired, but isConnected /
    //offlineMode were false so the pane — and the container — never rendered).
    private bool ShowGraphCanvas =>
        ShowWorkspace
        && !showDebug
        && tableResult == null
        && (visualizationMode == 2 || visualizationMode == 3)
        && !string.IsNullOrEmpty(queryResults)
        && CurrentGraphData().ValueKind != JsonValueKind.Undefined;

    //The result the tab holds, as it came back: the live graph result when present, else the last one.
    private JsonElement HeldResult()
    {
        if (graphResultData.ValueKind != JsonValueKind.Undefined)
            return graphResultData;

        return lastResultData;
    }

    //The graph data backing the current view: the result the tab holds, when the viewer can read it as a graph. One it
    //cannot is no graph data at all, so nothing that draws, tabulates or counts it walks into it, and ResultProblem
    //says why. Until 2026-10-31 such a result went to every reader as it was, and the first to read it threw out of a
    //render. Kept with its tab, it threw again on every reload, and the page could not be got back without deleting the
    //stored tabs from the browser's storage by hand.
    private JsonElement CurrentGraphData()
    {
        if (ResultProblem() != null)
            return default;

        return HeldResult();
    }

    //Memo for ResultProblem: the result last read, and what reading it found.
    private JsonElement problemResult;
    private bool problemRead;
    private string problemValue;

    //Why the viewer cannot read the result the tab holds as a graph, or null when it can or there is none. Read once per
    //result, since it walks the whole of it and the page asks on every render: a JsonElement that is the same document at
    //the same index is the same result, as GraphStats keeps it.
    private string ResultProblem()
    {
        var held = HeldResult();

        if (held.ValueKind == JsonValueKind.Undefined)
            return null;

        if (!problemRead || !held.Equals(problemResult))
        {
            problemValue = GraphDataConverter.WhyUnreadable(held);
            problemResult = held;
            problemRead = true;
        }

        return problemValue;
    }

    //Memo for ResultsHaveNoRenderableGraph so the result set isn't walked on every render.
    private object noVisualToken;
    private bool noVisualIsGraph;
    private bool noVisualValue;

    //True when the current results parsed but contain no vertices or edges to draw (e.g. a scalar or
    //string list such as g.V().label().dedup()), so the 2D / 3D canvas or Table would otherwise sit
    //blank. Backs the "nothing to visualize — go to JSON" banner.
    private bool ResultsHaveNoRenderableGraph()
    {
        if (!HasGraphData)
            return false;

        //A canvas deliberately emptied via "Clear results" (to build a new graph) is not a failed
        //visualization, so it shows no banner — the user is drawing / staging changes to commit.
        if (resultsCleared)
            return false;

        //Offline mode is a drawing surface, not a database query, so "the query returned no graph to
        //display" never applies — the canvas holds what the user is building, not a failed visualization.
        if (offlineMode)
            return false;

        //Only while actually previewing staged edits (reflect-db-state off AND a staged buffer exists) does
        //the canvas render the effective graph — base plus edits — rather than the base results this method
        //inspects, which would make the banner misleading. With no staged edits the canvas shows the base
        //results as-is, so an empty result must still surface the banner (matches ShowUncommittedBanner).
        if (!reflectDbState && !string.IsNullOrWhiteSpace(generatedQueryText))
            return false;

        var data = CurrentGraphData();
        if (data.ValueKind == JsonValueKind.Undefined)
            return false;

        //queryResults is a stable string instance until the results change, so it's a cheap identity
        //token for the scalar path. The live-graph path always has nodes, so a stale token is harmless.
        bool isGraph = graphResultData.ValueKind != JsonValueKind.Undefined;
        if (!ReferenceEquals(noVisualToken, queryResults) || noVisualIsGraph != isGraph)
        {
            var table = GraphDataConverter.ToTable(data);
            noVisualValue = table.Nodes.Count == 0 && table.Edges.Count == 0;
            noVisualToken = queryResults;
            noVisualIsGraph = isGraph;
        }

        return noVisualValue;
    }

    //A successful query with no renderable graph may still have returned a scalar (a count, a boolean, a
    //single string). Returns that value for the status line, or null when the result is empty or complex.
    private string ResultScalarText()
    {
        var data = CurrentGraphData();

        var el = data;
        if (data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() != 1)
                return null;

            el = data[0];
        }

        var u = GraphDataConverter.UnwrapElement(el);

        if (u.ValueKind == JsonValueKind.Number
            || u.ValueKind == JsonValueKind.String
            || u.ValueKind == JsonValueKind.True
            || u.ValueKind == JsonValueKind.False)
            return u.ToString();

        return null;
    }

    //Whole milliseconds, switching to seconds past 1000 ms — matches the GraphStats formatting.
    private static string FormatQueryMs(double ms)
    {
        if (ms >= 1000)
            return $"{ms / 1000:0.##} s";
        else
            return $"{ms:0} ms";
    }

    #endregion
}
