using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///One independent query workspace: its own query text, results, view mode and selection.
///The active tab's members are surfaced through Home's delegating properties (Home.State.cs).
///</summary>
public class QueryTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; }
    public string QueryText { get; set; }
    public string EditorLanguage { get; set; } = "gremlin";
    public string GeneratedQueryText { get; set; }
    public int QueryEditorTab { get; set; } = 1;
    public string QueryResults { get; set; }
    public string QueryError { get; set; }//error message from the last run, shown as a red bar over the stats
    public bool JsonCopied { get; set; }
    public int VisualizationMode { get; set; } = 2;
    public string SearchTerm { get; set; }
    public bool ShowFilter { get; set; }
    public HashSet<string> HiddenLabels { get; set; } = new();
    public JsonElement LastResultData { get; set; }
    public JsonElement GraphResultData { get; set; }
    //True when the canvas was emptied via "Clear results" to build a new graph from scratch, so the empty
    //state isn't treated as a failed visualization (no "nothing to visualize" banner). Reset once a
    //query runs or JSON is pasted/visualized.
    public bool ResultsCleared { get; set; }
    public double? LastQueryMs { get; set; }//runtime only (not persisted): elapsed ms of the last executed query
    public SelectedElementInfo SelectedElement { get; set; }
    public Dictionary<string, string> OriginalProperties { get; set; }
    public bool HasPropertyChanges { get; set; }
    public string CommitStatus { get; set; }
    public string CommitStatusClass { get; set; }
    public string NewPropertyName { get; set; }
    public string NewPropertyValue { get; set; }
    public string NewComponentLabel { get; set; }
    public string NewConnectionDirection { get; set; } = "out";
    public string NewConnectionLabel { get; set; }
    public string NewConnectionOtherId { get; set; }
    public string ConnectionPickMode { get; set; }
    public string PickEdgeLabel { get; set; }
    public string PickFirstNodeId { get; set; }
    public string PickFirstNodeName { get; set; }
    public string PickSecondNodeId { get; set; }
    public string PickSecondNodeName { get; set; }
    //Which node slot the next graph click fills: 1 = source, 2 = target.
    public int PickActiveSlot { get; set; } = 1;
    public HashSet<string> RemovedProperties { get; set; } = new();
    //Property keys the current selection's edit session has touched, so re-generating the staged queries
    //replaces only those lines and leaves other elements' (and untouched keys') staged edits intact.
    public HashSet<string> SessionEditedKeys { get; set; } = new();

    //Per-tab settings & view preferences.
    public int LoadDbLimit { get; set; } = 100;
    public bool AutoReloadAfterCommit { get; set; }
    //When false, the canvas previews the staged (uncommitted) edits instead of the plain database state.
    public bool ReflectDbState { get; set; } = true;
    public string Cy2dLayout { get; set; } = "cose";
    public string G3dLayout { get; set; } = "force";
    //Hand-pinned node arrangements captured by "Save positions", stored in the browser (JSON
    //[{id,x,y(,z)}]). Kept separately per viewer so a 2D arrangement never disturbs a 3D one; drives the
    //"user saved positions" layout even before the changes are committed.
    public string Saved2dPositionsJson { get; set; }
    public string Saved3dPositionsJson { get; set; }
    public bool Show3dVertexLabels { get; set; } = true;
    public bool Show3dEdgeLabels { get; set; } = true;
    public bool Show3dProps { get; set; } = true;
    //3D label draw distance as a 0–100 percentage: 100 (the default) shows every label, lower fades out
    //vertex and edge labels farther from the camera so a dense graph's text stays legible.
    public int Draw3dLabelDistance { get; set; } = 100;
    //3D node draw distance as a 0–100 percentage: 100 (the default) shows every node, lower hides nodes
    //(and the edges touching them) farther from the camera so a dense graph declutters as you zoom out.
    public int Draw3dNodeDistance { get; set; } = 100;
    public string G3dExportFormat { get; set; } = "obj";
    public string ImageExportFormat { get; set; } = "png";
    public string TableExportFormat { get; set; } = "csv";
    public Dictionary<string, LabelStyle> LabelStyles { get; set; } = new();
    //Edge coloring mode: 0 = auto (a distinct hashed color per edge label), 1 = off (the default gray),
    //2 = custom (per-edge-label colors from EdgeColors, falling back to the auto color where unset).
    public int EdgeColorMode { get; set; }
    //Per-edge-label custom colors (edge label → hex), used when EdgeColorMode is custom.
    public Dictionary<string, string> EdgeColors { get; set; } = new();
}
