using System.Text;

namespace GraphDBViewerWeb.Code;

///<summary>
///How a value written into a staged single-quoted literal reads back out. Every builder's <c>Escape</c>
///writes the same four escapes — <c>\\</c>, <c>\'</c>, <c>\r</c> and <c>\n</c> — and the parsers that
///preview the Generated buffer on the canvas need their one inverse, so that a description written on two
///lines is previewed on two lines and commits as the two lines it is.
///
///Any other escaped character stands for itself, which is what a hand-edited buffer most likely meant and
///what the Gremlin parser always did; a lone trailing backslash is kept rather than lost.
///</summary>
public static class StagedLiteral
{
    public static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        if (!value.Contains('\\'))
            return value;

        var sb = new StringBuilder(value.Length);
        var escape = false;

        foreach (var c in value)
        {
            if (!escape)
            {
                if (c == '\\')
                    escape = true;
                else
                    sb.Append(c);

                continue;
            }

            escape = false;

            if (c == 'n')
                sb.Append('\n');
            else if (c == 'r')
                sb.Append('\r');
            else if (c == 't')
                sb.Append('\t');
            else
                sb.Append(c);
        }

        if (escape)
            sb.Append('\\');

        return sb.ToString();
    }
}
