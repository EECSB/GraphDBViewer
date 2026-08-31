using System.Collections.Generic;

namespace GraphDBViewerWeb.Code;

///<summary>
///Reads GUN's staged statements back: whether a line writes, and what the Generated buffer amounts to on
///the canvas. The GUN counterpart of <see cref="GremlinEditParser"/> and <see cref="AqlStatementParser"/>.
///
///GUN's <c>put</c> is an upsert — it creates a node and updates one with the same call — so a statement
///yields both an add and the property sets. That is not hedging: the add is ignored when the node is
///already drawn, and the sets are ignored when it is not, so the pair describes whichever case is real.
///</summary>
public static class GunStatementParser
{
    ///<summary>Property GUN data carries a node's type in, which is the closest thing it has to a label.</summary>
    public const string TypeProperty = GunConverter.TypeProperty;

    ///<summary>True when any line writes. A GUN read is a key path, so a statement is the only way to write.</summary>
    public static bool IsMutating(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var line in text.Split('\n'))
            if (GunWrite.IsWrite(line))
                return true;

        return false;
    }

    ///<summary>
    ///Every recognized write in the buffer, in order, as the edits the optimistic view applies.
    ///Unrecognized lines are skipped — they stay staged and fail at commit rather than being guessed at.
    ///</summary>
    public static List<GraphEdit> Parse(string buffer)
    {
        var edits = new List<GraphEdit>();

        if (string.IsNullOrWhiteSpace(buffer))
            return edits;

        foreach (var line in buffer.Split('\n'))
        {
            var write = GunWrite.Parse(line);

            if (write != null)
                Add(edits, write);
        }

        return edits;
    }

    private static void Add(List<GraphEdit> edits, GunWrite write)
    {
        if (write.Kind == GunWriteKind.Link)
        {
            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.AddEdge,
                Type = "edge",
                Id = GunConverter.EdgeId(write.Soul, write.Edge, write.Target),
                Label = write.Edge,
                Source = write.Soul,
                Target = write.Target
            });

            return;
        }

        if (write.Kind == GunWriteKind.Clear)
        {
            //Nulling a key that holds a link removes that edge; nulling the node removes the node.
            if (!string.IsNullOrEmpty(write.Edge))
            {
                //Without knowing what the link pointed at there is no edge to name, so the canvas shows the
                //change only after the commit re-reads it.
                if (string.IsNullOrEmpty(write.Target))
                    return;

                edits.Add(new GraphEdit
                {
                    Kind = GraphEditKind.RemoveEdge,
                    Type = "edge",
                    Id = GunConverter.EdgeId(write.Soul, write.Edge, write.Target)
                });

                return;
            }

            edits.Add(new GraphEdit
            {
                Kind = GraphEditKind.RemoveNode,
                Type = "node",
                Id = write.Soul
            });

            return;
        }

        var properties = new Dictionary<string, string>();

        foreach (var kv in write.Values)
            if (kv.Value != null)
                properties[kv.Key] = kv.Value;

        //Ignored when the node is already on the canvas, which is exactly when it should be.
        edits.Add(new GraphEdit
        {
            Kind = GraphEditKind.AddNode,
            Type = "node",
            Id = write.Soul,
            Label = Label(properties),
            Properties = new Dictionary<string, string>(properties)
        });

        foreach (var kv in write.Values)
        {
            GraphEditKind kind;
            if (kv.Value == null)
                kind = GraphEditKind.DropProperty;
            else
                kind = GraphEditKind.SetProperty;

            edits.Add(new GraphEdit
            {
                Kind = kind,
                Type = "node",
                Id = write.Soul,
                Key = kv.Key,
                Value = kv.Value
            });
        }
    }

    //GUN has no types, so a node's label is the "type" property when the data carries one — the same
    //convention GunConverter reads.
    private static string Label(Dictionary<string, string> properties)
    {
        if (properties.TryGetValue(TypeProperty, out var type) && !string.IsNullOrWhiteSpace(type))
            return type;

        return GunConverter.DefaultLabel;
    }
}
