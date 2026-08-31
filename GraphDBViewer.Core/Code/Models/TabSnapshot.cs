namespace GraphDBViewerWeb.Code;

///<summary>
///Serializable snapshot of a tab for localStorage (JsonElement results are stored as raw JSON text).
///</summary>
public class TabSnapshot
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string QueryText { get; set; }
    public string EditorLanguage { get; set; }
    public string GeneratedQueryText { get; set; }
    public int QueryEditorTab { get; set; }
    public string QueryResults { get; set; }
    public int VisualizationMode { get; set; }
    public string SearchTerm { get; set; }
    public List<string> HiddenLabels { get; set; }
    public string LastResultJson { get; set; }
    public string GraphResultJson { get; set; }
    public bool ResultsCleared { get; set; }//canvas emptied via "Clear results" to build a new graph
    public int LoadDbLimit { get; set; }
    public bool AutoReloadAfterCommit { get; set; }
    public bool ReflectDbState { get; set; } = true;//default on so older snapshots keep today's behavior
    public string Cy2dLayout { get; set; }
    public string G3dLayout { get; set; }
    public string Saved2dPositionsJson { get; set; }
    public string Saved3dPositionsJson { get; set; }
    public bool Show3dVertexLabels { get; set; }
    public bool Show3dEdgeLabels { get; set; }
    //Nullable so a tab saved before this field existed restores with the default (on) rather than off.
    public bool? Show3dProps { get; set; }
    public string G3dExportFormat { get; set; }
    public string ImageExportFormat { get; set; }
    public string TableExportFormat { get; set; }
    public Dictionary<string, LabelStyle> LabelStyles { get; set; }
    public int EdgeColorMode { get; set; }
    public Dictionary<string, string> EdgeColors { get; set; }
}

///<summary>
///Tiny per-tab editor-text snapshot, persisted on every keystroke (no heavy result data).
///</summary>
public class TabText
{
    public string Id { get; set; }
    public string Query { get; set; }
    public string Generated { get; set; }
}
