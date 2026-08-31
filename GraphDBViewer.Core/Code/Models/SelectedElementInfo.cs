namespace GraphDBViewerWeb.Code;

///<summary>
///The vertex or edge currently selected in the graph/table view, with its editable properties and
///(for a vertex) its incident edges, as shown in the sidebar's Component Properties panel.
///</summary>
public class SelectedElementInfo
{
    public string Type { get; set; }
    public string Id { get; set; }
    //GraphSON type of the id (e.g. "g:Int64"), so edits/deletes emit a correctly-typed id literal.
    public string IdType { get; set; }
    public string Label { get; set; }
    public string GLabel { get; set; }
    public string Source { get; set; }
    public string Target { get; set; }
    public string SourceLabel { get; set; }
    public string TargetLabel { get; set; }
    public Dictionary<string, string> EditableProperties { get; set; } = new();
    public List<EdgeInfo> InEdges { get; set; } = new();
    public List<EdgeInfo> OutEdges { get; set; } = new();
}

///<summary>One edge incident to the selected vertex: the edge and the node on its other end.</summary>
public class EdgeInfo
{
    public string EdgeId { get; set; }
    //GraphSON type of the edge id (e.g. "g:Int64"), so removing the edge emits a correctly-typed id.
    public string EdgeIdType { get; set; }
    public string Label { get; set; }
    public string OtherNodeId { get; set; }
    public string OtherNodeLabel { get; set; }
}
