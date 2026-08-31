namespace GraphDBViewerWeb.Code;

///<summary>
///Per-vertex-label visual style: node color, pixel size, the 2D canvas shape, the 3D solid, the
///property whose value is shown as the node's display label, an optional icon image URL, and an
///optional 3D model (.obj) URL. Every field is optional — a null/blank string or a zero size means
///"unset", so the default look (blue, auto-sized, rectangle in 2D, sphere in 3D, Name/name/title) is
///kept. Mirrors the per-node gdbv* style stored in the database.
///</summary>
public class LabelStyle
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
}
