namespace GraphDBViewerWeb.Code;

///<summary>
///A fixed categorical color palette, mapping a label (vertex or edge) to a stable color. The mapping is a
///deterministic hash of the label text, so the same label always gets the same color across renders and
///across the 2D / 3D views (and a legend can reproduce it) — independent of the order results arrive in.
///The selection orange (#fd7e14) and the neutral gray defaults are deliberately excluded.
///</summary>
public static class GraphPalette
{
    public static readonly string[] Colors =
    {
        "#4e79a7",//blue
        "#e15759",//red
        "#59a14f",//green
        "#b07aa1",//purple
        "#edc948",//yellow
        "#9c755f",//brown
        "#ff9da7",//pink
        "#76b7b2",//cyan
        "#6610f2",//indigo
        "#d63384",//magenta
        "#20c997",//teal
        "#b6992d",//olive
    };

    ///<summary>The palette color for a label — a stable FNV-1a hash of the text folded onto the palette.</summary>
    public static string ColorForLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
            return Colors[0];

        uint hash = 2166136261;

        foreach (char c in label)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return Colors[hash % (uint)Colors.Length];
    }
}
