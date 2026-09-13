using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

///<summary>
///The datasets on the canvas, and which of them are drawn (ADR 0016, question 5).
///
///<strong>A dataset is part of the structure the viewer exists to show</strong>, so the viewer draws
///it rather than linking out to the perspective that made it. What it draws is generic — a chart for
///an ordered series, a table otherwise — and comes from the vertex's own declaration, because the
///viewer knows nothing about meaning and must not learn.
///
///<strong>Two ways to switch it off, and the first is the real one.</strong> The query decides what
///reaches the canvas at all: a traversal that leaves <c>Dataset</c> out draws no dataset, and the
///label filter hides a whole label with one checkbox. These are the second — one per dataset, so a
///graph that does include them is not a wall of charts.
///</summary>
public partial class Home
{
    ///<summary>
    ///How many are drawn before the rest wait to be ticked.
    ///
    ///<strong>Four because each one is a round trip.</strong> A canvas showing a line's forty sensors
    ///would otherwise open forty reads the moment a query returned, and the answer to wanting forty
    ///charts is a query that asks for fewer — which is the first of question 5's two ways.
    ///</summary>
    private const int DatasetsDrawnByDefault = 4;

    ///<summary>Unticked by the user, by vertex id. Absent means the default decides.</summary>
    private readonly HashSet<string> datasetsHidden = new(StringComparer.Ordinal);

    ///<summary>Ticked by the user past the default, by vertex id.</summary>
    private readonly HashSet<string> datasetsShown = new(StringComparer.Ordinal);

    ///<summary>
    ///The <c>Dataset</c> vertices in what the canvas is showing, in the order they came back.
    ///
    ///Recomputed only when the result changes, the way <see cref="GraphStats"/>'s table is and for the
    ///same reason: this runs on every render, and Home renders on every selection, tab switch and
    ///staged edit rather than only when the graph changed.
    ///</summary>
    private List<GraphDataConverter.GraphRow> DatasetsOnCanvas
    {
        get
        {
            var data = CurrentGraphData();

            //Same document, same index, same graph. JsonElement's equality is structural on the
            //underlying document, so this is a reference check rather than a walk.
            if (datasetsRead && data.Equals(datasetsFrom))
                return datasetsOnCanvas;

            datasetsFrom = data;
            datasetsRead = true;
            datasetsOnCanvas = new List<GraphDataConverter.GraphRow>();

            if (data.ValueKind == JsonValueKind.Undefined)
                return datasetsOnCanvas;

            foreach (var row in GraphDataConverter.ToTable(data).Nodes)
            {
                if (row.Label == DatasetVertex.Label)
                    datasetsOnCanvas.Add(row);
            }

            return datasetsOnCanvas;
        }
    }

    private List<GraphDataConverter.GraphRow> datasetsOnCanvas = new();
    private JsonElement datasetsFrom;
    private bool datasetsRead;

    ///<summary>Whether this one is drawn: the first few by default, and whatever the user has said since.</summary>
    private bool Drawing(string id)
    {
        if (datasetsHidden.Contains(id))
            return false;

        if (datasetsShown.Contains(id))
            return true;

        return DatasetsOnCanvas.Take(DatasetsDrawnByDefault).Any(one => one.Id == id);
    }

    ///<summary>
    ///Ticks or unticks one. <strong>The vertex stays on the canvas either way</strong> — question 5 is
    ///explicit that this hides the drawing and not the dataset.
    ///</summary>
    private void ToggleDataset(string id)
    {
        if (Drawing(id))
        {
            datasetsShown.Remove(id);
            datasetsHidden.Add(id);

            return;
        }

        datasetsHidden.Remove(id);
        datasetsShown.Add(id);
    }

    ///<summary>
    ///The key the read surface is addressed by — the identity property, not the engine's id.
    ///
    ///An engine hands out its own ids (TinkerGraph a Long, Cosmos a string) and they are not portable;
    ///the platform's key is, which is the whole reason it exists.
    ///</summary>
    private static string DatasetKey(GraphDataConverter.GraphRow dataset)
    {
        if (dataset.Properties.TryGetValue(GraphIdentity.Property, out var key))
            return key;

        return "";
    }

    ///<summary>What to call it in the checkbox row. Its name, or its key when it has none.</summary>
    private static string DatasetName(GraphDataConverter.GraphRow dataset)
    {
        if (dataset.Properties.TryGetValue(DatasetVertex.Name, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        var key = DatasetKey(dataset);

        if (key.Length > 8)
            return key.Substring(0, 8);

        return "Dataset";
    }
}
