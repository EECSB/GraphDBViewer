namespace GraphDBViewerWeb.Code;

///<summary>
///Reserved property keys recognized only by the GraphDB viewer (vertices and edges): node image /
///3D-model URLs, a pinned layout position (X/Y/Z), and a per-node style stored on the vertex itself
///(the database-scoped counterpart of the per-label browser styles) — color, size, shape, and the
///property whose value is shown as the node's label.
///</summary>
public static class GdbvKeys
{
    //Every viewer-reserved key starts with this prefix.
    public const string Prefix = "gdbv";

    public const string Image = "gdbvImage";
    public const string Model = "gdbvModel";
    //Pinned 2D-canvas position. The 3D viewer keeps its own separate position (X3d/Y3d/Z3d) because the
    //two coordinate systems are unrelated, so a 2D arrangement never disturbs a 3D one (or vice versa).
    public const string X = "gdbvX";
    public const string Y = "gdbvY";
    //Legacy shared Z — no longer written (superseded by Z3d); kept so cleanup can still strip old data.
    public const string Z = "gdbvZ";
    //Pinned 3D-viewer position, independent of the 2D X/Y above.
    public const string X3d = "gdbvX3d";
    public const string Y3d = "gdbvY3d";
    public const string Z3d = "gdbvZ3d";
    public const string Color = "gdbvColor";
    public const string Size = "gdbvSize";
    //2D canvas shape (rectangle / square / circle / oval / triangle / hexagon).
    public const string Shape = "gdbvShape";
    //3D solid when no model is set (sphere / oval / cube / box / pyramid / prism).
    public const string Shape3d = "gdbvShape3d";
    public const string Display = "gdbvDisplay";
    //Comma-separated list of property keys shown beneath the node on the 2D / 3D canvas.
    public const string Show = "gdbvShow";

    ///<summary>Every viewer-reserved key, for bulk operations such as the database cleanup.</summary>
    public static readonly string[] All = { Image, Model, X, Y, Z, X3d, Y3d, Z3d, Color, Size, Shape, Shape3d, Display, Show };
}
