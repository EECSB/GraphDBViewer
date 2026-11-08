namespace GraphDBViewerWeb.Code;

///<summary>
///Per-vertex-label visual style: node color, pixel size, the 2D canvas shape, the 3D solid, the
///property whose value is shown as the node's display label, an optional icon image URL, an optional
///3D model (.obj) URL, and which properties hold the coordinates for the Map and the Globe. Every
///field is optional — a null/blank string or a zero size means "unset", so the default look (blue,
///auto-sized, rectangle in 2D, sphere in 3D, Name/name/title) and the coordinate names convention
///already knows are kept. Mirrors the per-node gdbv* style stored in the database.
///</summary>
///<remarks>
///A record rather than a class for one reason: <c>with { }</c> copies it whole. The stylesheet clone
///used to list every field by hand, which works until the next field is added and quietly is not.
///</remarks>
public record LabelStyle
{
    public string Color { get; set; }
    public int Size { get; set; }
    //2D canvas shape: rectangle (default) / square / circle / oval / triangle / hexagon. Blank = rectangle.
    public string Shape { get; set; }
    //3D solid (when no model is set): sphere (default) / oval / cube / box / pyramid / prism. Blank = sphere.
    public string Shape3d { get; set; }
    public string DisplayProperty { get; set; }
    public string Icon { get; set; }
    public string Model { get; set; }
    //Where this label's vertices keep their place on the earth, when it is not under a name GeoCoordinates
    //already knows: a property each, or one holding both. Blank leaves the convention alone.
    public string LatitudeProperty { get; set; }
    public string LongitudeProperty { get; set; }
    public string PointProperty { get; set; }
    //Show details on canvas: a small card of each vertex's label, id and properties under it on the 2D canvas.
    public bool Details { get; set; }
}
