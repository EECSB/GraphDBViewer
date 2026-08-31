namespace GraphDBViewerWeb.Code;

///<summary>A node id + its position captured from the graph view (Z is used only in 3D).</summary>
public class NodePosition
{
    public string Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
