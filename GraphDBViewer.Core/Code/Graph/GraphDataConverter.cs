using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Converts raw Gremlin JSON results into formats consumed by Cytoscape.js (2D) and 3d-force-graph (3D).
///Works with System.Text.Json — no Gremlin.Net dependency.
///</summary>
public static class GraphDataConverter
{
    #region Cytoscape (2D)

    ///<summary>
    ///Converts a Gremlin/GraphSON result array into Cytoscape.js elements JSON (nodes + edges) for the 2D view. An
    ///algorithm's overlay, when given, is drawn over every other style (AlgorithmOverlay).
    ///</summary>
    public static string ToCytoscapeJson(
        JsonElement results,
        IReadOnlyDictionary<string, LabelStyle> styles = null,
        int edgeColorMode = 0,
        IReadOnlyDictionary<string, string> edgeColors = null,
        AlgorithmOverlay overlay = null,
        IReadOnlyCollection<string> edgeDetails = null)
    {
        var elements = new List<object>();
        var seenNodes = new HashSet<string>();

        if (results.ValueKind == JsonValueKind.Array)
        {
            //Pass 1: add real vertices first so an edge listed before its vertices doesn't
            //leave an id-only placeholder node shadowing the real (named) vertex.
            foreach (var unwrapped in ResultElements(results))
            {
                if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
                {
                    AddCytoscapeNode(elements, seenNodes, v, styles, overlay);
                    AddCytoscapeNode(elements, seenNodes, o, styles, overlay);
                }
                else if (IsVertex(unwrapped))
                    AddCytoscapeNode(elements, seenNodes, unwrapped, styles, overlay);
            }

            //Pass 2: add edges (endpoints become placeholders only when truly absent above).
            foreach (var unwrapped in ResultElements(results))
            {
                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                    AddCytoscapeEdge(elements, v, e, o, edgeColorMode, edgeColors, overlay, edgeDetails);
                else if (IsEdge(unwrapped))
                    AddCytoscapeEdgeStandalone(elements, seenNodes, unwrapped, edgeColorMode, edgeColors, overlay, edgeDetails);
            }
        }

        return JsonSerializer.Serialize(elements);
    }

    private static void AddCytoscapeNode(List<object> elements, HashSet<string> seen, JsonElement vertex, IReadOnlyDictionary<string, LabelStyle> styles, AlgorithmOverlay overlay)
    {
        string id = GetId(vertex);

        if (id == null || !seen.Add(id))
            return;

        string typeLabel = GetStringProp(vertex, "label") ?? "vertex";
        var properties = ExtractProperties(vertex);
        string label = ExtractLabel(vertex, styles, properties);

        var data = new Dictionary<string, object> { ["id"] = id, ["label"] = label, ["glabel"] = typeLabel, ["properties"] = properties };

        //A per-node image renders only when the node is set to show it (its gdbvShow list). The 2D
        //stylesheet draws data["image"], so leaving it unset when "show" is off hides the image.
        if (properties.TryGetValue(GdbvKeys.Image, out var image)
            && !string.IsNullOrWhiteSpace(image)
            && IsShown(properties, GdbvKeys.Image))
            data["image"] = image;

        ApplyNodeStyle(data, typeLabel, styles);
        ApplyPerNodeStyle(data, properties);
        ApplyLowConfidence(data, properties);
        ApplyShownProperties(data, properties);
        ApplyGeoPosition(data, properties, MappingOf(typeLabel, styles));
        overlay?.ApplyToNode(data, id);

        //Rectangle is the default 2D canvas shape when nothing else set one.
        if (!data.ContainsKey("shape"))
            data["shape"] = "rectangle";

        if (TryGetPinnedPosition(properties, GdbvKeys.X, GdbvKeys.Y, null, out double px, out double py, out _))
        {
            data["px"] = px;
            data["py"] = py;
            elements.Add(new { data, position = new { x = px, y = py } });
        }
        else
            elements.Add(new { data });
    }

    //Where the vertex is on the earth, when its properties say so — read by the Map and Globe layouts,
    //which place the node by these, and ignored by every other layout. Under names of their own so a
    //user property that happens to be called "lat" never collides with what the view reads.
    private static void ApplyGeoPosition(Dictionary<string, object> data, IReadOnlyDictionary<string, string> properties, GeoMapping mapping)
    {
        if (!GeoCoordinates.TryRead(properties, mapping, out double latitude, out double longitude))
            return;

        data["geoLat"] = latitude;
        data["geoLng"] = longitude;
    }

    //The coordinate properties this label's style names, or null where it names none — which is every
    //label until somebody has data convention cannot read.
    private static GeoMapping MappingOf(string typeLabel, IReadOnlyDictionary<string, LabelStyle> styles)
    {
        if (styles == null || typeLabel == null || !styles.TryGetValue(typeLabel, out var style))
            return null;

        return GeoMapping.Of(style.LatitudeProperty, style.LongitudeProperty, style.PointProperty);
    }

    ///<summary>
    ///How many of the result's vertices carry a place on the earth, and how many there are — what the
    ///Map and Globe say about the graph they were asked to draw ("12 of 40 located"), and what tells
    ///them to say so when the answer is none.
    ///</summary>
    public static (int Located, int Total) GeoCoverage(JsonElement results, IReadOnlyDictionary<string, LabelStyle> styles = null)
    {
        int located = 0;
        int total = 0;
        var seen = new HashSet<string>();

        if (results.ValueKind != JsonValueKind.Array)
            return (0, 0);

        foreach (var unwrapped in ResultElements(results))
        {
            if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
            {
                CountGeo(v, styles, seen, ref located, ref total);
                CountGeo(o, styles, seen, ref located, ref total);
            }
            else if (IsVertex(unwrapped))
                CountGeo(unwrapped, styles, seen, ref located, ref total);
        }

        return (located, total);
    }

    ///<summary>
    ///The vertices the Map cannot place, each with the words a geocoder would be asked about it — its
    ///address as its properties spell it, else the name the canvas draws. The same walk and the same
    ///reading as <see cref="GeoCoverage"/>, so what this lists is exactly what that counts as missing.
    ///</summary>
    public static List<GeoCandidate> UnlocatedVertices(JsonElement results, IReadOnlyDictionary<string, LabelStyle> styles = null)
    {
        var candidates = new List<GeoCandidate>();
        var seen = new HashSet<string>();

        if (results.ValueKind != JsonValueKind.Array)
            return candidates;

        foreach (var unwrapped in ResultElements(results))
        {
            if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
            {
                AddCandidate(candidates, v, styles, seen);
                AddCandidate(candidates, o, styles, seen);
            }
            else if (IsVertex(unwrapped))
                AddCandidate(candidates, unwrapped, styles, seen);
        }

        return candidates;
    }

    private static void AddCandidate(
        List<GeoCandidate> candidates,
        JsonElement vertex,
        IReadOnlyDictionary<string, LabelStyle> styles,
        HashSet<string> seen)
    {
        string id = GetId(vertex);

        if (id == null || !seen.Add(id))
            return;

        string typeLabel = GetStringProp(vertex, "label") ?? "vertex";
        var properties = ExtractProperties(vertex);

        if (GeoCoordinates.TryRead(properties, MappingOf(typeLabel, styles), out _, out _))
            return;

        //The name is looked for the way the canvas looks for a caption — the node's own gdbvDisplay, the
        //label's display property, then Name/name/title — but read from the properties rather than taken
        //from ExtractLabel, which always answers something. Its fallbacks are "Label (id)" and the bare
        //id, which are the canvas saying there is no name, and no geocoder has heard of either.
        string name = null;

        foreach (var key in DisplayPropertyNames(typeLabel, styles, properties.GetValueOrDefault(GdbvKeys.Display)))
            if (properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                name = value;

                break;
            }

        candidates.Add(new GeoCandidate(id, GetIdType(vertex), typeLabel, name, Geocoding.QueryFor(properties, name)));
    }

    private static void CountGeo(
        JsonElement vertex,
        IReadOnlyDictionary<string, LabelStyle> styles,
        HashSet<string> seen,
        ref int located,
        ref int total)
    {
        string id = GetId(vertex);

        if (id == null || !seen.Add(id))
            return;

        total++;

        var mapping = MappingOf(GetStringProp(vertex, "label") ?? "vertex", styles);

        if (GeoCoordinates.TryRead(ExtractProperties(vertex), mapping, out _, out _))
            located++;
    }

    //Folds a label's configured color/size/icon/3D model into the node data so the JS view styles it.
    //Icon only fills in when the node has no per-node image of its own; a shown per-node gdbvModel is
    //mapped to data["model"] by the callers (overriding the label model set here), which the 3D view reads.
    private static void ApplyNodeStyle(Dictionary<string, object> data, string typeLabel, IReadOnlyDictionary<string, LabelStyle> styles)
    {
        if (styles == null || !styles.TryGetValue(typeLabel, out var style))
            return;

        if (!string.IsNullOrWhiteSpace(style.Color))
            data["bgColor"] = style.Color;

        if (style.Size > 0)
            data["nodeSize"] = style.Size;

        if (!string.IsNullOrWhiteSpace(style.Shape))
            data["shape"] = style.Shape;

        if (!string.IsNullOrWhiteSpace(style.Shape3d))
            data["shape3d"] = style.Shape3d;

        if (!string.IsNullOrWhiteSpace(style.Icon) && !data.ContainsKey("image"))
            data["image"] = style.Icon;

        if (!string.IsNullOrWhiteSpace(style.Model))
            data["model"] = style.Model;

        if (style.Details)
            data["details"] = true;
    }

    ///<summary>
    ///Draws an entity the extraction was unsure of in the same muted color its relationships get. It comes
    ///last, after the label style and the node's own: a guess should look like a guess whatever palette is
    ///in play, and a color the user chose deliberately is the one thing that outranks it.
    ///</summary>
    private static void ApplyLowConfidence(Dictionary<string, object> data, IReadOnlyDictionary<string, string> properties)
    {
        if (!IsLowConfidence(properties))
            return;

        //Except where the user set one themselves — that is a decision, not a default.
        if (properties.TryGetValue(GdbvKeys.Color, out var own) && !string.IsNullOrWhiteSpace(own))
            return;

        data["bgColor"] = LowConfidenceColor;
    }

    //Folds a node's own gdbvColor / gdbvSize (stored on the vertex in the database) into its render
    //data, overriding the per-label browser style — most-specific wins, just as a per-node gdbvImage
    //already overrides the label icon. gdbvImage is mapped to data["image"] by the callers.
    private static void ApplyPerNodeStyle(Dictionary<string, object> data, IReadOnlyDictionary<string, string> props)
    {
        if (props.TryGetValue(GdbvKeys.Color, out var color) && !string.IsNullOrWhiteSpace(color))
            data["bgColor"] = color;

        if (props.TryGetValue(GdbvKeys.Size, out var sizeStr)
            && double.TryParse(sizeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var size)
            && size > 0)
            data["nodeSize"] = size;

        if (props.TryGetValue(GdbvKeys.Shape, out var shape) && !string.IsNullOrWhiteSpace(shape))
            data["shape"] = shape;

        if (props.TryGetValue(GdbvKeys.Shape3d, out var shape3d) && !string.IsNullOrWhiteSpace(shape3d))
            data["shape3d"] = shape3d;

        if (props.TryGetValue(GdbvKeys.Details, out var details) && bool.TryParse(details, out var shown))
            data["details"] = shown;
    }

    //True when the given key is present in the node's gdbvShow list (a comma-separated set of property
    //keys the node is configured to display). Image and model use this to gate whether their linked file
    //renders on the node.
    private static bool IsShown(IReadOnlyDictionary<string, string> props, string key)
    {
        if (!props.TryGetValue(GdbvKeys.Show, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        foreach (var k in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (k == key)
                return true;

        return false;
    }

    //Collects the property values the node is configured to display beneath itself (its gdbvShow list —
    //a comma-separated set of property keys) into data["showProps"] as ordered "key: value" strings,
    //which the 2D and 3D views render as an extension / floating text under the node. gdbvImage and
    //gdbvModel share the same list but render as the image / 3D model itself (see above), not as text.
    private static void ApplyShownProperties(Dictionary<string, object> data, IReadOnlyDictionary<string, string> props)
    {
        if (!props.TryGetValue(GdbvKeys.Show, out var raw) || string.IsNullOrWhiteSpace(raw))
            return;

        var lines = new List<string>();
        foreach (var key in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (key == GdbvKeys.Image || key == GdbvKeys.Model)
                continue;

            if (props.TryGetValue(key, out var val))
                lines.Add($"{key}: {val}");
            else
                lines.Add(key);
        }

        if (lines.Count > 0)
            data["showProps"] = lines;
    }

    private static void AddCytoscapeEdge(
        List<object> elements,
        JsonElement from,
        JsonElement edge,
        JsonElement to,
        int edgeColorMode,
        IReadOnlyDictionary<string, string> edgeColors,
        AlgorithmOverlay overlay,
        IReadOnlyCollection<string> edgeDetails)
    {
        string sourceId = GetId(from);
        string targetId = GetId(to);
        string edgeLabel = GetStringProp(edge, "label") ?? "edge";
        string edgeId = GetId(edge) ?? Guid.NewGuid().ToString();

        var properties = ExtractProperties(edge);
        var edgeColor = overlay?.EdgeColor(sourceId, targetId) ?? ResolveEdgeColor(edgeLabel, properties, edgeColorMode, edgeColors);
        var details = EdgeDetailsOf(edgeLabel, properties, edgeDetails);

        elements.Add(new { data = new { id = edgeId, source = sourceId, target = targetId, label = edgeLabel, edgeColor, properties, details } });
    }

    private static void AddCytoscapeEdgeStandalone(
        List<object> elements,
        HashSet<string> seenNodes,
        JsonElement edge,
        int edgeColorMode,
        IReadOnlyDictionary<string, string> edgeColors,
        AlgorithmOverlay overlay,
        IReadOnlyCollection<string> edgeDetails)
    {
        //An edge naming one end only is not drawn: there is nothing to draw it to, and ToDot leaves it out too. Until
        //2026-11-07 the missing end was filled in from the other, and the edge was drawn as a loop on the end it named.
        string sourceId = GetEdgeVertexId(edge, "outV");
        string targetId = GetEdgeVertexId(edge, "inV");

        if (sourceId == null || targetId == null)
            return;

        if (seenNodes.Add(sourceId))
            elements.Add(new { data = Placeholder(sourceId, overlay) });

        if (seenNodes.Add(targetId))
            elements.Add(new { data = Placeholder(targetId, overlay) });

        string edgeLabel = GetStringProp(edge, "label") ?? "edge";
        string edgeId = GetId(edge) ?? Guid.NewGuid().ToString();
        var properties = ExtractProperties(edge);
        var edgeColor = overlay?.EdgeColor(sourceId, targetId) ?? ResolveEdgeColor(edgeLabel, properties, edgeColorMode, edgeColors);
        var details = EdgeDetailsOf(edgeLabel, properties, edgeDetails);

        elements.Add(new { data = new { id = edgeId, source = sourceId, target = targetId, label = edgeLabel, edgeColor, properties, details } });
    }

    ///<summary>
    ///Whether an edge is drawn as a card of its details (Detailed view on edges, 2026-11-07, the user's "do the loose
    ///ends"): its own gdbvDetails when it says true or false, as a vertex's does, else whether its label is among those
    ///Styling turned the view on for.
    ///</summary>
    public static bool EdgeDetailsOf(string label, IReadOnlyDictionary<string, string> properties, IReadOnlyCollection<string> edgeDetails)
    {
        if (properties.TryGetValue(GdbvKeys.Details, out var own) && bool.TryParse(own, out var shown))
            return shown;

        return edgeDetails != null && edgeDetails.Contains(label);
    }

    ///<summary>The node an edge's end is drawn as when the result does not hold that vertex: its id, and an overlay's look, since an algorithm counts it.</summary>
    private static Dictionary<string, object> Placeholder(string id, AlgorithmOverlay overlay)
    {
        var data = new Dictionary<string, object> { ["id"] = id, ["label"] = id, ["shape"] = "rectangle", ["properties"] = new Dictionary<string, string>() };

        overlay?.ApplyToNode(data, id);

        return data;
    }

    #endregion

    #region 3D Force Graph

    ///<summary>
    ///Converts a Gremlin/GraphSON result array into 3d-force-graph JSON ({ nodes, links }) for the 3D view, grouping node
    ///color by vertex label. An algorithm's overlay, when given, is drawn over every other style (AlgorithmOverlay).
    ///</summary>
    public static string ToForceGraphJson(
        JsonElement results,
        IReadOnlyDictionary<string, LabelStyle> styles = null,
        int edgeColorMode = 0,
        IReadOnlyDictionary<string, string> edgeColors = null,
        AlgorithmOverlay overlay = null,
        IReadOnlyCollection<string> edgeDetails = null)
    {
        var nodes = new List<object>();
        var links = new List<object>();
        var seenNodes = new HashSet<string>();
        int groupCounter = 0;
        var labelGroups = new Dictionary<string, int>();

        if (results.ValueKind == JsonValueKind.Array)
        {
            //Pass 1: add real vertices first so an edge listed before its vertices doesn't
            //leave an id-only placeholder node shadowing the real (named) vertex.
            foreach (var unwrapped in ResultElements(results))
            {
                if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
                {
                    AddForceNode(nodes, seenNodes, v, labelGroups, ref groupCounter, styles, overlay);
                    AddForceNode(nodes, seenNodes, o, labelGroups, ref groupCounter, styles, overlay);
                }
                else if (IsVertex(unwrapped))
                    AddForceNode(nodes, seenNodes, unwrapped, labelGroups, ref groupCounter, styles, overlay);
            }

            //Pass 2: add edges (endpoints become placeholders only when truly absent above).
            foreach (var unwrapped in ResultElements(results))
            {
                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                    AddForceLink(links, v, e, o, edgeColorMode, edgeColors, overlay, edgeDetails);
                else if (IsEdge(unwrapped))
                    AddForceLinkStandalone(nodes, links, seenNodes, unwrapped, labelGroups, ref groupCounter, edgeColorMode, edgeColors, overlay, edgeDetails);
            }
        }

        return JsonSerializer.Serialize(new { nodes, links });
    }

    private static void AddForceNode(
        List<object> nodes,
        HashSet<string> seen,
        JsonElement vertex,
        Dictionary<string, int> labelGroups,
        ref int groupCounter,
        IReadOnlyDictionary<string, LabelStyle> styles,
        AlgorithmOverlay overlay)
    {
        string id = GetId(vertex);

        if (id == null || !seen.Add(id))
            return;

        string typeLabel = GetStringProp(vertex, "label") ?? "vertex";

        if (!labelGroups.TryGetValue(typeLabel, out int group))
        {
            group = ++groupCounter;
            labelGroups[typeLabel] = group;
        }

        var properties = ExtractProperties(vertex);
        string label = ExtractLabel(vertex, styles, properties);

        var data = new Dictionary<string, object> { ["id"] = id, ["label"] = label, ["group"] = group, ["glabel"] = typeLabel, ["properties"] = properties };

        //A per-node image renders only when the node is set to show it (its gdbvShow list); the 3D view
        //reads data["image"], so leaving it unset when "show" is off hides the image.
        if (properties.TryGetValue(GdbvKeys.Image, out var image)
            && !string.IsNullOrWhiteSpace(image)
            && IsShown(properties, GdbvKeys.Image))
            data["image"] = image;

        ApplyNodeStyle(data, typeLabel, styles);
        ApplyPerNodeStyle(data, properties);
        ApplyLowConfidence(data, properties);

        //A per-node linked model (gdbvModel) likewise renders only when shown, overriding any label
        //model; the 3D view reads data["model"], not the raw property, so gating happens here.
        if (properties.TryGetValue(GdbvKeys.Model, out var model)
            && !string.IsNullOrWhiteSpace(model)
            && IsShown(properties, GdbvKeys.Model))
            data["model"] = model;

        ApplyShownProperties(data, properties);
        ApplyGeoPosition(data, properties, MappingOf(typeLabel, styles));
        overlay?.ApplyToNode(data, id);

        //Sphere is the default 3D solid when nothing else set one.
        if (!data.ContainsKey("shape3d"))
            data["shape3d"] = "sphere";

        //3d-force-graph reads fx/fy/fz as fixed positions, so saved 3D coordinates pin the node.
        if (TryGetPinnedPosition(properties, GdbvKeys.X3d, GdbvKeys.Y3d, GdbvKeys.Z3d, out double fx, out double fy, out double? fz))
        {
            data["fx"] = fx;
            data["fy"] = fy;
            if (fz.HasValue)
                data["fz"] = fz.Value;
        }

        nodes.Add(data);
    }

    private static void AddForceLink(
        List<object> links,
        JsonElement from,
        JsonElement edge,
        JsonElement to,
        int edgeColorMode,
        IReadOnlyDictionary<string, string> edgeColors,
        AlgorithmOverlay overlay,
        IReadOnlyCollection<string> edgeDetails)
    {
        string source = GetId(from);
        string target = GetId(to);
        string label = GetStringProp(edge, "label") ?? "edge";

        var properties = ExtractProperties(edge);
        var color = overlay?.EdgeColor(source, target) ?? ResolveEdgeColor(label, properties, edgeColorMode, edgeColors);
        var details = EdgeDetailsOf(label, properties, edgeDetails);

        links.Add(new { source, target, label, color, properties, details });
    }

    private static void AddForceLinkStandalone(
        List<object> nodes,
        List<object> links,
        HashSet<string> seenNodes,
        JsonElement edge,
        Dictionary<string, int> labelGroups,
        ref int groupCounter,
        int edgeColorMode,
        IReadOnlyDictionary<string, string> edgeColors,
        AlgorithmOverlay overlay,
        IReadOnlyCollection<string> edgeDetails)
    {
        //As on the 2D canvas: an edge naming one end only is not drawn.
        string sourceId = GetEdgeVertexId(edge, "outV");
        string targetId = GetEdgeVertexId(edge, "inV");

        if (sourceId == null || targetId == null)
            return;

        if (seenNodes.Add(sourceId))
        {
            if (!labelGroups.TryGetValue("vertex", out int g))
            {
                g = ++groupCounter;
                labelGroups["vertex"] = g;
            }

            nodes.Add(ForcePlaceholder(sourceId, g, overlay));
        }

        if (seenNodes.Add(targetId))
        {
            if (!labelGroups.TryGetValue("vertex", out int g))
            {
                g = ++groupCounter;
                labelGroups["vertex"] = g;
            }

            nodes.Add(ForcePlaceholder(targetId, g, overlay));
        }

        string label = GetStringProp(edge, "label") ?? "edge";
        var properties = ExtractProperties(edge);
        var color = overlay?.EdgeColor(sourceId, targetId) ?? ResolveEdgeColor(label, properties, edgeColorMode, edgeColors);
        var details = EdgeDetailsOf(label, properties, edgeDetails);

        links.Add(new { source = sourceId, target = targetId, label, color, properties, details });
    }

    ///<summary>The 3D node an edge's end is drawn as when the result does not hold that vertex, with an overlay's look, since an algorithm counts it.</summary>
    private static Dictionary<string, object> ForcePlaceholder(string id, int group, AlgorithmOverlay overlay)
    {
        var data = new Dictionary<string, object> { ["id"] = id, ["label"] = id, ["group"] = group, ["properties"] = new Dictionary<string, string>() };

        overlay?.ApplyToNode(data, id);

        return data;
    }

    //The default edge line color (matches the 2D base edge selector), used when coloring is off.
    private const string DefaultEdgeColor = "#6c757d";

    ///<summary>
    ///Below this, a relationship carrying a <see cref="KgGraphParser.ConfidenceProperty"/> is drawn faint.
    ///Knowledge-graph extraction states everything with the same confident line whether the text said it
    ///outright or the model inferred it; when the model reports how sure it was, showing that makes a
    ///guess look like a guess.
    ///</summary>
    public const double LowConfidenceThreshold = 0.5;

    ///<summary>The muted color a low-confidence edge is drawn in.</summary>
    public const string LowConfidenceColor = "#adb5bd";

    ///<summary>
    ///An edge's render color: the faint one when its own confidence says it is a guess, otherwise whatever
    ///the label's coloring mode resolves to.
    ///</summary>
    public static string ResolveEdgeColor(string label, IReadOnlyDictionary<string, string> properties, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        if (IsLowConfidence(properties))
            return LowConfidenceColor;

        return ResolveEdgeColor(label, edgeColorMode, edgeColors);
    }

    ///<summary>True when the element reports a confidence below <see cref="LowConfidenceThreshold"/>.</summary>
    public static bool IsLowConfidence(IReadOnlyDictionary<string, string> properties)
    {
        if (properties == null || !properties.TryGetValue(KgGraphParser.ConfidenceProperty, out var raw))
            return false;

        //An unparseable confidence is not a low one — saying nothing is different from saying "unsure".
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var confidence))
            return false;

        return confidence < LowConfidenceThreshold;
    }

    //Resolves an edge's render color for the current mode: off (1) = the picked color for that label when
    //one is set, else the default gray — so coloring a single label leaves the rest gray; custom (2) = the
    //picked color, falling back to the auto color when unset; auto (0) and the fallback = a stable hashed
    //palette color per label.
    public static string ResolveEdgeColor(string label, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        var custom = PickedEdgeColor(label, edgeColors);

        if (edgeColorMode == 1)
        {
            if (custom != null)
                return custom;

            return DefaultEdgeColor;
        }

        if (edgeColorMode == 2 && custom != null)
            return custom;

        return GraphPalette.ColorForLabel(label);
    }

    //The color explicitly picked for an edge label, or null when the label has none.
    private static string PickedEdgeColor(string label, IReadOnlyDictionary<string, string> edgeColors)
    {
        if (edgeColors != null && edgeColors.TryGetValue(label ?? "", out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;

        return null;
    }

    #endregion

    #region GraphSON Unwrapping

    ///<summary>Unwraps a single GraphSON-typed element (e.g. g:Vertex, g:Edge, g:Map, g:Int64) to its plain inner JSON value.</summary>
    public static JsonElement UnwrapElement(JsonElement element)
    {
        return UnwrapGraphSON(element);
    }

    ///<summary>
    ///The result's elements, each unwrapped: an item as it is, or, for a path() result, what the path went through. A
    ///g:Path holds its labels and its objects, and the objects are the vertices and edges, which drew nothing while the
    ///path was read whole (2026-11-07).
    ///</summary>
    private static IEnumerable<JsonElement> ResultElements(JsonElement results)
    {
        foreach (var item in results.EnumerateArray())
        {
            var unwrapped = UnwrapGraphSON(item);

            if (IsPath(item, unwrapped))
                foreach (var step in UnwrapGraphSON(unwrapped.GetProperty("objects")).EnumerateArray())
                    yield return UnwrapGraphSON(step);
            else
                yield return unwrapped;
        }
    }

    ///<summary>A g:Path, or one written without its type: an object holding its labels and its objects, with no id of its own.</summary>
    private static bool IsPath(JsonElement item, JsonElement unwrapped)
    {
        if (unwrapped.ValueKind != JsonValueKind.Object || !unwrapped.TryGetProperty("objects", out var objects))
            return false;

        if (UnwrapGraphSON(objects).ValueKind != JsonValueKind.Array)
            return false;

        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("@type", out var type) && type.GetString() == "g:Path")
            return true;

        return unwrapped.TryGetProperty("labels", out _) && !unwrapped.TryGetProperty("id", out _);
    }

    private static JsonElement UnwrapGraphSON(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("@type", out var typeEl) && element.TryGetProperty("@value", out var inner))
        {
            string? type = typeEl.GetString();

            //g:Map stores key-value pairs as flat array: ["k1", v1, "k2", v2, ...]
            //Convert to a proper JSON object for downstream processing.
            if (type == "g:Map" && inner.ValueKind == JsonValueKind.Array)
                return ConvertGraphSONMapToObject(inner);

            //A date is epoch milliseconds on the wire, which nothing downstream can read as a date.
            //Read back as the ISO 8601 UTC instant it stands for, which every date reader can.
            if ((type == "g:Date" || type == "g:Timestamp") && inner.ValueKind == JsonValueKind.Number && inner.TryGetInt64(out var epochMilliseconds))
                return JsonSerializer.SerializeToElement(IsoInstant(epochMilliseconds));

            return UnwrapGraphSON(inner);
        }

        return element;
    }

    ///<summary>
    ///A property's value as text: a string as it is, a whole number as its digits, a double in its
    ///shortest invariant form — 10 for 10.0, 12.5 for 12.5 — so a value the seam wrote typed reads
    ///back as exactly the text it was written from (ADR 0014's "reading is already correct", kept true).
    ///</summary>
    private static string ValueText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var whole))
                return whole.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (value.TryGetDouble(out var number))
                return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (value.ValueKind == JsonValueKind.True)
            //A JSON boolean's ToString is C#'s "True"; every reader compares against the word as Gremlin says it.
            return "true";
        else if (value.ValueKind == JsonValueKind.False)
            return "false";

        return value.ToString();
    }

    ///<summary>
    ///A plain value, one no GraphSON wraps, as text: a string as it is and a number as its own text, as the wire has
    ///them, and a null as empty text, as they have always read; but a boolean as the word Gremlin says, through
    ///<see cref="GraphWireText.Stringify"/> as every engine's converter reads one, where a JsonElement's ToString
    ///says C#'s "True". Until 2026-09-26 the flat shapes read it through ToString (<see cref="ExtractProperties"/>).
    ///</summary>
    private static string PlainText(JsonElement value)
    {
        return GraphWireText.Stringify(value) ?? "";
    }

    ///<summary>A string, a number or a boolean, as the wire has it: a value that is text already, or has one text.</summary>
    private static bool IsScalar(JsonElement value)
    {
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }

    ///<summary>
    ///A scalar GraphSON 3 wraps with its type: {"@type":"g:Int32","@value":250}, and every other number, a g:Date's
    ///epoch milliseconds and a g:UUID's string the same way. A wrapper around a list, a map or an element is not
    ///one: what it holds has no single text.
    ///</summary>
    private static bool IsTypedScalar(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("@type", out _))
            return false;

        return value.TryGetProperty("@value", out var inner) && IsScalar(inner);
    }

    ///<summary>Epoch milliseconds as the ISO 8601 UTC instant they stand for — 2026-09-05T00:00:00Z, milliseconds only when there are any.</summary>
    private static string IsoInstant(long epochMilliseconds)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);

        if (epochMilliseconds % 1000 == 0)
            return at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        return at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    ///<summary>
    ///A g:Map as the JSON object it stands for, each key read by <see cref="MapKeyText"/>. The values are kept as the
    ///wire has them, typed, for their readers to unwrap. A token key is the element's own id, label or end, so a
    ///property stored under the same name, which JanusGraph allows for id, never displaces it, whichever comes first.
    ///</summary>
    private static JsonElement ConvertGraphSONMapToObject(JsonElement kvArray)
    {
        var dict = new Dictionary<string, JsonElement>();
        var tokens = new HashSet<string>();

        for (int i = 0; i + 1 < kvArray.GetArrayLength(); i += 2)
        {
            string? key = MapKeyText(kvArray[i], out bool token);

            if (key == null)
                continue;

            if (!token && tokens.Contains(key))
                continue;

            if (token)
                tokens.Add(key);

            dict[key] = kvArray[i + 1];
        }

        var json = JsonSerializer.Serialize(dict);

        return JsonDocument.Parse(json).RootElement;
    }

    ///<summary>
    ///A g:Map key as the name it has in the object: a string as it is; a key the wire types as the text of what it
    ///wraps, so a g:T token reads "id" or "label", a g:Direction one "IN" or "OUT" and a number its digits, which is
    ///what the same map is keyed by untyped; and anything else, a vertex groupCount() keys by, as its JSON. A null key
    ///names nothing and reads as null. Until 2026-09-27 every key was asked for its string, and a typed one threw,
    ///so every elementMap() and valueMap(true) answer did, from every reader of it.
    ///</summary>
    private static string? MapKeyText(JsonElement key, out bool token)
    {
        token = false;

        if (key.ValueKind == JsonValueKind.String)
            return key.GetString();

        if (key.ValueKind == JsonValueKind.Object && key.TryGetProperty("@type", out var type) && type.ValueKind == JsonValueKind.String)
            token = type.GetString() is "g:T" or "g:Direction";

        return GraphWireText.Stringify(UnwrapGraphSON(key));
    }

    private static bool TryExtractTraversalTriple(JsonElement item, out JsonElement v, out JsonElement e, out JsonElement o)
    {
        v = default; e = default; o = default;

        if (item.ValueKind != JsonValueKind.Object)
            return false;

        bool hasV = item.TryGetProperty("v", out var vRaw);
        bool hasE = item.TryGetProperty("e", out var eRaw);
        bool hasO = item.TryGetProperty("o", out var oRaw);

        if (hasV && hasE && hasO)
        {
            v = UnwrapGraphSON(vRaw);
            e = UnwrapGraphSON(eRaw);
            o = UnwrapGraphSON(oRaw);
            return true;
        }

        return false;
    }

    //An element map's keys are the element's own property names, so a "type" there may be a stored number: only a
    //string names the element's type.
    private static bool IsVertex(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        if (el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "vertex")
            return true;

        //Either end rules it out, as either one makes it an edge (IsEdge); until 2026-11-07 only outV did here.
        if (el.TryGetProperty("id", out _)
            && el.TryGetProperty("label", out _)
            && !el.TryGetProperty("outV", out _)
            && !el.TryGetProperty("inV", out _)
            && !HasElementMapEnds(el)
            && !IsEdgeWithoutEnds(el))
            return true;

        return false;
    }

    private static bool IsEdge(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        if (el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "edge")
            return true;

        if (el.TryGetProperty("outV", out _) || el.TryGetProperty("inV", out _))
            return true;

        if (HasElementMapEnds(el))
            return true;

        if (IsEdgeWithoutEnds(el))
            return true;

        return false;
    }

    ///<summary>
    ///Whether the element is an edge that names neither of its ends, which is what valueMap(true) answers for an edge on
    ///JanusGraph: an element that is its own bag, whose id JanusGraph types as the edge's RelationIdentifier. It has an
    ///id and a label and no outV, as a vertex does, so until 2026-09-29 it read as a vertex, drawn as a node, and an edit
    ///or a drop of it went to g.V() with the edge's id. No vertex has such an id. A vertex property does, and reads as
    ///its id, its label and its value, so an element with a value is left to read as it did. Elsewhere nothing on the
    ///wire tells such an edge from a vertex: an edge's values are single where a vertex's are lists, but so are a
    ///vertex's under valueMap(true).by(unfold()).
    ///</summary>
    private static bool IsEdgeWithoutEnds(JsonElement el)
    {
        return el.TryGetProperty("id", out var id)
            && IsRelationIdentifier(id)
            && el.TryGetProperty("label", out _)
            && !el.TryGetProperty("value", out _)
            && !el.TryGetProperty("outV", out _)
            && !el.TryGetProperty("inV", out _)
            && !HasElementMapEnds(el);
    }

    ///<summary>
    ///Whether the element names its ends as an edge's elementMap() does: IN and OUT, keyed by g:Direction tokens on
    ///the wire and by the words untyped, each a map of that vertex's id and label. It has an id and a label and no
    ///outV, as a vertex does, so without this an edge's element map read as a vertex (until 2026-09-27).
    ///</summary>
    private static bool HasElementMapEnds(JsonElement el)
    {
        return el.TryGetProperty("IN", out var inEnd)
            && inEnd.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("OUT", out var outEnd)
            && outEnd.ValueKind == JsonValueKind.Object;
    }

    #endregion

    #region Property Helpers

    private static string GetId(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("id", out var idProp))
            return null;

        return IdText(idProp);
    }

    ///<summary>The GraphSON type JanusGraph gives an edge's id, and a vertex property's.</summary>
    private const string RelationIdentifierType = "janusgraph:RelationIdentifier";

    ///<summary>
    ///An element's id as the text the viewer shows it by and quotes into a query for it: a typed id as the text of its
    ///value, a number's digits and a string as it is, and an untyped one as it is. But JanusGraph's edge id, typed
    ///{"@type":"janusgraph:RelationIdentifier","@value":{"relationId":"4r-6c-2dx-3bk"}}, as its relationId, which is
    ///how JanusGraph writes an edge's id and resolves one: g.E('4r-6c-2dx-3bk'). Until 2026-09-27 it read as the JSON
    ///text {"relationId":"4r-6c-2dx-3bk"}, and every edit and drop of the edge was sent as g.E('{"relationId":…}'),
    ///which JanusGraph cannot parse into an edge id. The connections panel's reading of an edge's id
    ///(<see cref="GremlinQueryBuilderAdapter"/>) and the commit's of an added element's (<see cref="StagedIds"/>) go
    ///through this too, so one edge has one id wherever it was read.
    ///</summary>
    public static string IdText(JsonElement id)
    {
        if (id.ValueKind != JsonValueKind.Object || !id.TryGetProperty("@value", out var value))
            return id.ToString();

        if (IsRelationIdentifier(id)
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("relationId", out var relation)
            && relation.ValueKind == JsonValueKind.String)
            return relation.GetString();

        return value.ToString();
    }

    ///<summary>Whether an id is typed as JanusGraph's RelationIdentifier, which <see cref="IdText"/> reads as its relationId.</summary>
    public static bool IsRelationIdentifier(JsonElement id)
    {
        return id.ValueKind == JsonValueKind.Object
            && id.TryGetProperty("@type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == RelationIdentifierType;
    }

    ///<summary>The GraphSON id type (e.g. "g:Int64") of a vertex / edge, or null when the id carries no
    ///type. Kept alongside the id so a mutation query can emit a correctly-typed id literal — Gremlin is
    ///type-strict on edge ids, so a Long id must be written g.E(123L), not g.E(123).</summary>
    private static string GetIdType(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("id", out var idProp))
            return null;

        if (idProp.ValueKind == JsonValueKind.Object && idProp.TryGetProperty("@type", out var typeEl))
            return typeEl.GetString();

        return null;
    }

    private static string GetStringProp(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty(name, out var prop))
            return null;

        return prop.ToString();
    }

    //The id of an edge's end, "outV" or "inV": where the edge names it, or where its elementMap() does, the vertex's
    //id in the map under OUT or IN. An end written null is no end. The staged-edit preview writes the ends it does not
    //know that way, an edge's that came without them or a staged one's left unnamed, and read as text a null is "": both
    //canvases drew a node with no id and an edge to it (until 2026-10-03).
    private static string GetEdgeVertexId(JsonElement edge, string direction)
    {
        if (edge.TryGetProperty(direction, out var val))
        {
            if (val.ValueKind == JsonValueKind.Null)
                return null;

            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("@value", out var inner))
                return inner.ToString();

            return val.ToString();
        }

        string end;

        if (direction == "outV")
            end = "OUT";
        else
            end = "IN";

        if (HasElementMapEnds(edge))
            return GetId(UnwrapGraphSON(edge.GetProperty(end)));

        return null;
    }

    ///<summary>
    ///A node's caption: the first of its display properties it has (<see cref="DisplayPropertyNames"/>), read from the
    ///properties already extracted for it, so that it is the text its properties panel shows, a typed number as its
    ///digits and a boolean as true or false; else its label and its id. An element that is its own bag, an elementMap()
    ///or a projection, is captioned from what it holds as a vertex is. Until 2026-09-27 the caption read the element's
    ///properties object again on its own, as the wire has it: on GraphSON 3 a typed value captioned as its wrapper's
    ///JSON and a boolean as C#'s "True", and an element with no properties object read as its label and id whatever
    ///it was named.
    ///</summary>
    private static string ExtractLabel(JsonElement vertex, IReadOnlyDictionary<string, LabelStyle> styles, IReadOnlyDictionary<string, string> properties)
    {
        string id = GetId(vertex) ?? "?";
        string typeLabel = GetStringProp(vertex, "label");

        foreach (var propName in DisplayPropertyNames(typeLabel, styles, properties?.GetValueOrDefault(GdbvKeys.Display)))
            if (properties != null && properties.TryGetValue(propName, out var caption))
                return caption;

        if (typeLabel != null)
            return $"{typeLabel} ({id})";

        return id;
    }

    ///<summary>A table row's caption, as the canvas draws its vertex: the property the styles say to display, else a name, else its label and id.</summary>
    public static string CaptionOf(GraphRow row, IReadOnlyDictionary<string, LabelStyle> styles)
    {
        foreach (var propName in DisplayPropertyNames(row.Label, styles, row.Properties?.GetValueOrDefault(GdbvKeys.Display)))
            if (row.Properties != null && row.Properties.TryGetValue(propName, out var caption))
                return caption;

        if (row.Label != null)
            return $"{row.Label} ({row.Id})";

        return row.Id;
    }

    //Property names tried (in order) for a node's display label: the node's own gdbvDisplay
    //(stored in the database) first, then the label's configured display-property, then the
    //built-in Name/name/title fallbacks.
    private static IEnumerable<string> DisplayPropertyNames(string typeLabel, IReadOnlyDictionary<string, LabelStyle> styles, string perNodeDisplay)
    {
        if (!string.IsNullOrWhiteSpace(perNodeDisplay))
            yield return perNodeDisplay;

        if (styles != null
            && typeLabel != null
            && styles.TryGetValue(typeLabel, out var style)
            && !string.IsNullOrWhiteSpace(style.DisplayProperty))
            yield return style.DisplayProperty;

        yield return "Name";
        yield return "name";
        yield return "title";
        yield return "Title";
        yield return "label";
    }

    //Reads a pinned-layout position from the given x / y (/ z) property keys, if present. The 2D and 3D
    //viewers pass different keys (gdbvX/gdbvY vs gdbvX3d/gdbvY3d/gdbvZ3d) so their arrangements are
    //independent. Returns true when both x and y are valid numbers; zKey is optional (pass null for 2D).
    private static bool TryGetPinnedPosition(Dictionary<string, string> props, string xKey, string yKey, string zKey, out double x, out double y, out double? z)
    {
        x = 0;
        y = 0;
        z = null;

        if (!props.TryGetValue(xKey, out var xs) || !double.TryParse(xs, NumberStyles.Any, CultureInfo.InvariantCulture, out x))
            return false;

        if (!props.TryGetValue(yKey, out var ys) || !double.TryParse(ys, NumberStyles.Any, CultureInfo.InvariantCulture, out y))
            return false;

        if (zKey != null && props.TryGetValue(zKey, out var zs) && double.TryParse(zs, NumberStyles.Any, CultureInfo.InvariantCulture, out var zv))
            z = zv;

        return true;
    }

    //An element's own metadata, skipped when the element itself is the property bag. These are names the
    //wire gives the element, not names a database gave a value. Inside a "properties" object they are not
    //metadata at all, so this is not applied there — see ExtractProperties.
    private static readonly HashSet<string> ElementMetadataNames = new() { "id", "label", "type", "outV", "inV", "outVLabel", "inVLabel" };

    ///<summary>Extracts an element's properties into a flat name→value dictionary, unwrapping GraphSON property shapes and skipping id/label/type metadata when the element itself is the bag.</summary>
    public static Dictionary<string, string> ExtractProperties(JsonElement element)
    {
        var result = new Dictionary<string, string>();

        //A projection's properties map is typed as a g:Map on GraphSON 3, and read as the object it stands for.
        JsonElement props = default;

        if (element.TryGetProperty("properties", out var rawProps))
            props = UnwrapGraphSON(rawProps);

        if (props.ValueKind == JsonValueKind.Object)
            //Everything in an element's own properties object is the element's data, and nothing is
            //skipped. An element's metadata sits beside that object and never inside it — every converter
            //strips its own before building one — so a property genuinely named label, type or id is a
            //value somebody stored. rdfs:label above all: it captions the node on the canvas, and until
            //2026-09-16 it was missing from the table, the properties panel and everything built on them.
            foreach (var prop in props.EnumerateObject())
                //The value itself is typed too in GraphSON 3 — {"@type":"g:Double","@value":48.2} — so it is
                //unwrapped like the property that carries it, or a number reads as a JSON blob and no
                //parser downstream (coordinates, pinned positions, sizes) can make anything of it. Its text
                //is invariant whatever the browser's culture: ValueText's for a value read out of a list or a
                //wrapper, the wire's own for a bare one.
                if (prop.Value.ValueKind == JsonValueKind.Array || prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (HeldValueText(prop.Value, out var text))
                        result[prop.Name] = text;
                }
                else
                    //The value itself, as untyped GraphSON gives an edge's: GraphSON 1's, and Cosmos DB's.
                    result[prop.Name] = PlainText(prop.Value);
        else
            //Here the element is the bag: a projected map or a document, where the wire's own names sit
            //among the stored ones and there is nothing else to tell them apart by.
            foreach (var prop in element.EnumerateObject())
            {
                if (ElementMetadataNames.Contains(prop.Name) || prop.Name.StartsWith("_"))
                    continue;

                //A scalar GraphSON 3 types, every number, a date and a UUID, reads as the value it wraps, through
                //the reading a vertex property's typed value gets above. Until 2026-09-27 only a bare scalar was
                //read here, and a typed one was dropped: a projection's numbers had no column in the table.
                if (IsScalar(prop.Value))
                    result[prop.Name] = PlainText(prop.Value);
                else if (IsTypedScalar(prop.Value))
                    result[prop.Name] = ValueText(UnwrapGraphSON(prop.Value));
                else if (FirstOfList(prop.Value, out var first))
                    result[prop.Name] = first;
            }

        return result;
    }

    ///<summary>
    ///What an element's properties object holds under a name, as text: a vertex's list of vertex properties as the first
    ///one's value, an edge's property as its value, and a value held as it is, typed by GraphSON 3 or not, as itself. A
    ///list reads as its first entry whatever the entry is, so a projection's valueMap() lists of plain values read as a
    ///vertex's own properties do, and an empty list is no value. Until 2026-10-31 only a vertex property or an edge's
    ///property was read out of a list or a wrapper, and a plain value there, the list's or a typed number standing
    ///alone, threw: project('id','label','properties').by(id).by(label).by(valueMap()) could not be read at all.
    ///</summary>
    private static bool HeldValueText(JsonElement held, out string text)
    {
        text = null;

        var value = UnwrapGraphSON(held);

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0)
                return false;

            value = UnwrapGraphSON(value[0]);
        }

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner))
            value = UnwrapGraphSON(inner);

        text = ValueText(value);

        return true;
    }

    ///<summary>
    ///A list as the text of its first value, where the element is its own bag: what valueMap(true) answers for each of a
    ///vertex's properties, a list because a vertex property can hold more than one value. It reads as the first, which is
    ///how a vertex's own properties object has always been read above, so the same vertex reads the same whichever of the
    ///two brought it (the user's choice, 2026-09-29, over the values joined or the list's JSON). A list GraphSON 3 types
    ///(g:List, g:Set) or a plain one, a typed value in it read as a vertex property's typed value is, and a vertex property
    ///in it, as propertyMap() answers, read as its value. An empty list, and one whose first value has no one text, are no
    ///value. Until 2026-09-29 every list was dropped here, and a valueMap(true) answer had no column but its id and label,
    ///its nodes captioned by those two, although the viewer's own "Vertices + props" example asks for exactly that.
    ///</summary>
    private static bool FirstOfList(JsonElement value, out string text)
    {
        text = "";

        var list = UnwrapGraphSON(value);

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return false;

        var first = UnwrapGraphSON(list[0]);

        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var held))
            first = UnwrapGraphSON(held);

        if (!IsScalar(first))
            return false;

        text = ValueText(first);

        return true;
    }

    #endregion

    #region Table

    ///<summary>A single vertex or edge row in the table view.</summary>
    public class GraphRow
    {
        public string Id { get; set; }
        //GraphSON type of the id (e.g. "g:Int64"), so mutation queries can emit a correctly-typed id.
        public string IdType { get; set; }
        public string Label { get; set; }
        public string Source { get; set; }//edges only
        public string Target { get; set; }//edges only
        public Dictionary<string, string> Properties { get; set; } = new();
    }

    ///<summary>Vertices and edges flattened into rows, with the union of property keys as columns.</summary>
    public class GraphTable
    {
        public List<GraphRow> Nodes { get; } = new();
        public List<GraphRow> Edges { get; } = new();
        public List<string> NodePropertyColumns { get; } = new();
        public List<string> EdgePropertyColumns { get; } = new();
    }

    ///<summary>
    ///Flattens a graph result into row/column tables of vertices and edges,
    ///reusing the same element-detection logic as the 2D/3D converters.
    ///</summary>
    public static GraphTable ToTable(JsonElement results)
    {
        var table = new GraphTable();
        var seenNodes = new HashSet<string>();
        var seenEdges = new HashSet<string>();

        if (results.ValueKind == JsonValueKind.Array)
            foreach (var unwrapped in ResultElements(results))
            {
                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                {
                    AddTableNode(table, seenNodes, v);
                    AddTableNode(table, seenNodes, o);
                    AddTableEdge(table, seenEdges, e);
                }
                else if (IsVertex(unwrapped))
                    AddTableNode(table, seenNodes, unwrapped);
                else if (IsEdge(unwrapped))
                    AddTableEdge(table, seenEdges, unwrapped);
            }

        return table;
    }

    ///<summary>
    ///Why a result cannot be read as a graph, or null when it can: what <see cref="ToTable"/> threw on it. Every reader of
    ///a result walks it as ToTable does, element by element through the same helpers, so a result ToTable reads is one
    ///the others read too, and one it throws on is a shape none of them expected.
    ///</summary>
    public static string WhyUnreadable(JsonElement results)
    {
        try
        {
            ToTable(results);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    ///<summary>
    ///The GraphSON id type (e.g. "g:Int64") of the vertex or edge with the given id in this graph data,
    ///or null when it isn't found or the id is untyped. Lets a mutation query emit a correctly-typed id
    ///literal so it matches the stored element (an edge with a Long id needs g.E(123L), not g.E(123)).
    ///</summary>
    public static string FindIdType(JsonElement results, string id, bool isEdge)
    {
        if (id == null || results.ValueKind == JsonValueKind.Undefined)
            return null;

        var table = ToTable(results);

        List<GraphRow> rows;
        if (isEdge)
            rows = table.Edges;
        else
            rows = table.Nodes;

        foreach (var row in rows)
            if (row.Id == id)
                return row.IdType;

        return null;
    }

    ///<summary>Distinct vertex type-labels present in a result (for the label filter).</summary>
    public static List<string> VertexLabels(JsonElement results)
    {
        var table = ToTable(results);
        var labels = new List<string>();

        foreach (var n in table.Nodes)
            if (!labels.Contains(n.Label))
                labels.Add(n.Label);

        return labels;
    }

    ///<summary>Distinct edge labels present in a result (for the edge-color styling list).</summary>
    public static List<string> EdgeLabels(JsonElement results)
    {
        var table = ToTable(results);
        var labels = new List<string>();

        foreach (var e in table.Edges)
            if (!string.IsNullOrEmpty(e.Label) && !labels.Contains(e.Label))
                labels.Add(e.Label);

        return labels;
    }

    ///<summary>
    ///Distinct node display labels in the result set — the same text shown on the nodes and matched by
    ///the graph search — for the search box's type-ahead suggestions. Sorted (case-insensitive) and
    ///capped so the datalist stays light on a large graph.
    ///</summary>
    public static List<string> SearchSuggestions(JsonElement results, IReadOnlyDictionary<string, LabelStyle> styles = null, int max = 200)
    {
        var suggestions = new List<string>();

        if (results.ValueKind != JsonValueKind.Array)
            return suggestions;

        var seenIds = new HashSet<string>();
        var seenText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var unwrapped in ResultElements(results))
        {
            if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
            {
                AddSuggestion(suggestions, seenIds, seenText, v, styles);
                AddSuggestion(suggestions, seenIds, seenText, o, styles);
            }
            else if (IsVertex(unwrapped))
                AddSuggestion(suggestions, seenIds, seenText, unwrapped, styles);
        }

        suggestions.Sort(StringComparer.OrdinalIgnoreCase);

        if (suggestions.Count > max)
            return suggestions.GetRange(0, max);

        return suggestions;
    }

    //Collects a vertex's display label into the suggestion list, deduped by vertex id then by text.
    private static void AddSuggestion(
        List<string> suggestions,
        HashSet<string> seenIds,
        HashSet<string> seenText,
        JsonElement vertex,
        IReadOnlyDictionary<string, LabelStyle> styles)
    {
        string id = GetId(vertex);

        if (id == null || !seenIds.Add(id))
            return;

        var properties = ExtractProperties(vertex);
        var label = ExtractLabel(vertex, styles, properties);

        if (!string.IsNullOrWhiteSpace(label) && seenText.Add(label))
            suggestions.Add(label);
    }

    private static void AddTableNode(GraphTable table, HashSet<string> seen, JsonElement vertex)
    {
        string id = GetId(vertex);

        if (id == null || !seen.Add(id))
            return;

        var row = new GraphRow
        {
            Id = id,
            IdType = GetIdType(vertex),
            Label = GetStringProp(vertex, "label") ?? "vertex",
            Properties = ExtractProperties(vertex)
        };
        table.Nodes.Add(row);

        foreach (var key in row.Properties.Keys)
            if (!table.NodePropertyColumns.Contains(key))
                table.NodePropertyColumns.Add(key);
    }

    private static void AddTableEdge(GraphTable table, HashSet<string> seen, JsonElement edge)
    {
        string id = GetId(edge) ?? Guid.NewGuid().ToString();

        if (!seen.Add(id))
            return;

        //The table shows the data as it came: an end the edge does not name stays empty, and the row stays, where the
        //canvases draw nothing. Until 2026-11-07 the missing end was filled in from the other, and the row said the edge
        //was a loop on the end it named.
        var row = new GraphRow
        {
            Id = id,
            IdType = GetIdType(edge),
            Label = GetStringProp(edge, "label") ?? "edge",
            Source = GetEdgeVertexId(edge, "outV"),
            Target = GetEdgeVertexId(edge, "inV"),
            Properties = ExtractProperties(edge)
        };

        table.Edges.Add(row);

        foreach (var key in row.Properties.Keys)
            if (!table.EdgePropertyColumns.Contains(key))
                table.EdgePropertyColumns.Add(key);
    }

    #endregion

    #region Merge

    ///<summary>
    ///Merges two GraphSON result arrays into one, de-duplicating vertices/edges by id (for incremental
    ///expansion). Set <paramref name="incomingIsNewer"/> when the second side is not another view of the
    ///same moment but a later one — a live update, where a property the peer removed has to disappear
    ///rather than being kept for being the fuller answer.
    ///</summary>
    public static JsonElement MergeGraphResults(JsonElement existing, JsonElement incoming, bool incomingIsNewer = false)
    {
        var items = new List<JsonElement>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        AppendUnique(items, seen, existing, false);
        AppendUnique(items, seen, incoming, incomingIsNewer);

        var json = JsonSerializer.Serialize(items);

        return JsonDocument.Parse(json).RootElement;
    }

    private static void AppendUnique(List<JsonElement> items, Dictionary<string, int> seen, JsonElement arr, bool isNewer)
    {
        if (arr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in arr.EnumerateArray())
        {
            string key = ElementKey(item);

            if (key == null)
            {
                items.Add(item);
                continue;
            }

            if (!seen.TryGetValue(key, out var at))
            {
                seen[key] = items.Count;
                items.Add(item);

                continue;
            }

            //A later answer replaces outright: it is the element as it stands now, so a property the peer
            //removed has to go with it.
            if (isNewer)
            {
                items[at] = item;
                continue;
            }

            //Otherwise the same element twice: keep whichever carries more. Which side that is depends on
            //the engine — a Gremlin expand returns the clicked node as a bare edge endpoint, so the loaded
            //one wins; GUN stands in an empty node for a link target it never walked, so the walk that
            //finally reads it wins.
            if (ExtractProperties(item).Count > ExtractProperties(items[at]).Count)
                items[at] = item;
        }
    }

    ///<summary>
    ///The graph with every edge leaving one of the given nodes dropped.
    ///
    ///For a live update, an answer about a node is the whole truth about that node's links: GUN keeps them
    ///as keys, and a key holds one link, so re-pointing <c>knows</c> at someone else is not a second edge
    ///but a replacement. Without this the old one lingers on the canvas as an edge the database no longer
    ///has. The pushed edges are merged straight back in, so only the ones that really went stay gone.
    ///</summary>
    public static JsonElement WithoutEdgesFrom(JsonElement graph, IReadOnlyCollection<string> sourceIds)
    {
        if (graph.ValueKind != JsonValueKind.Array || sourceIds == null || sourceIds.Count == 0)
            return graph;

        var kept = new List<JsonElement>();

        foreach (var item in graph.EnumerateArray())
        {
            var element = UnwrapGraphSON(item);

            if (IsEdge(element))
            {
                var source = GetEdgeVertexId(element, "outV");

                if (source != null && sourceIds.Contains(source))
                    continue;
            }

            kept.Add(item);
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(kept)).RootElement;
    }

    //Dedup key for a flat vertex/edge element ("v:id"/"e:id"), or null when it has no id.
    private static string ElementKey(JsonElement item)
    {
        var u = UnwrapGraphSON(item);
        string id = GetId(u);

        if (id == null)
            return null;

        if (IsEdge(u))
            return "e:" + id;

        if (IsVertex(u))
            return "v:" + id;

        return null;
    }

    #endregion

    #region Effective (optimistic) graph

    ///<summary>
    ///Builds an "effective" graph — the loaded baseline with the given staged edits applied — as a
    ///simple GraphSON array ({id,label,properties} vertices, {id,label,outV,inV,properties} edges) that
    ///<see cref="ToCytoscapeJson"/> / <see cref="ToForceGraphJson"/> accept unchanged. Used to render
    ///the optimistic (uncommitted) view when "reflect database state" is switched off.
    ///</summary>
    public static JsonElement BuildEffectiveGraphSON(JsonElement baseline, IReadOnlyList<GraphEdit> edits)
    {
        var table = ToTable(baseline);

        var nodesById = new Dictionary<string, GraphRow>();
        foreach (var n in table.Nodes)
            nodesById[n.Id] = n;

        var edgesById = new Dictionary<string, GraphRow>();
        foreach (var e in table.Edges)
            if (e.Id != null)
                edgesById[e.Id] = e;

        if (edits != null)
            foreach (var edit in edits)
                ApplyEdit(edit, nodesById, edgesById);

        var elements = new List<object>();

        foreach (var n in nodesById.Values)
            elements.Add(new { id = n.Id, label = n.Label, properties = n.Properties });

        foreach (var e in edgesById.Values)
            elements.Add(new { id = e.Id, label = e.Label, outV = e.Source, inV = e.Target, properties = e.Properties });

        var json = JsonSerializer.Serialize(elements);

        return JsonDocument.Parse(json).RootElement;
    }

    private static void ApplyEdit(GraphEdit edit, Dictionary<string, GraphRow> nodesById, Dictionary<string, GraphRow> edgesById)
    {
        if (edit.Kind == GraphEditKind.AddNode)
        {
            if (!nodesById.ContainsKey(edit.Id))
                nodesById[edit.Id] = new GraphRow { Id = edit.Id, Label = edit.Label, Properties = new Dictionary<string, string>(edit.Properties) };
        }
        else if (edit.Kind == GraphEditKind.AddEdge)
        {
            if (edit.Id != null && !edgesById.ContainsKey(edit.Id))
                edgesById[edit.Id] = new GraphRow { Id = edit.Id, Label = edit.Label, Source = edit.Source, Target = edit.Target, Properties = new Dictionary<string, string>(edit.Properties) };
        }
        else if (edit.Kind == GraphEditKind.RemoveNode)
        {
            nodesById.Remove(edit.Id);

            //Dropping a vertex drops its incident edges too — mirror Gremlin's drop semantics.
            var incident = edgesById
                .Where(kv => kv.Value.Source == edit.Id || kv.Value.Target == edit.Id)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in incident)
                edgesById.Remove(key);
        }
        else if (edit.Kind == GraphEditKind.RemoveEdge)
            edgesById.Remove(edit.Id);
        else if (edit.Kind == GraphEditKind.SetProperty)
        {
            var row = FindRow(edit, nodesById, edgesById);

            if (row != null)
                row.Properties[edit.Key] = edit.Value;
        }
        else if (edit.Kind == GraphEditKind.DropProperty)
        {
            var row = FindRow(edit, nodesById, edgesById);

            if (row != null)
                row.Properties.Remove(edit.Key);
        }
    }

    private static GraphRow FindRow(GraphEdit edit, Dictionary<string, GraphRow> nodesById, Dictionary<string, GraphRow> edgesById)
    {
        if (edit.Type == "node")
        {
            if (nodesById.TryGetValue(edit.Id, out var n))
                return n;
        }
        else if (edgesById.TryGetValue(edit.Id, out var e))
            return e;

        return null;
    }

    #endregion

    #region CSV export

    ///<summary>Flattens a GraphTable to a single CSV (vertices then edges) with a kind column and the union of property columns.</summary>
    public static string ToCsv(GraphTable table)
    {
        var propColumns = table.NodePropertyColumns.Concat(table.EdgePropertyColumns).Distinct().ToList();

        var header = new List<string> { "kind", "id", "label", "source", "target" };
        header.AddRange(propColumns);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(CsvEscape)));

        foreach (var node in table.Nodes)
        {
            var cells = new List<string> { "vertex", node.Id, node.Label, "", "" };
            foreach (var col in propColumns)
                cells.Add(PropOrEmpty(node.Properties, col));

            sb.AppendLine(string.Join(",", cells.Select(CsvEscape)));
        }

        foreach (var edge in table.Edges)
        {
            var cells = new List<string> { "edge", edge.Id, edge.Label, edge.Source ?? "", edge.Target ?? "" };
            foreach (var col in propColumns)
                cells.Add(PropOrEmpty(edge.Properties, col));

            sb.AppendLine(string.Join(",", cells.Select(CsvEscape)));
        }

        return sb.ToString();
    }

    private static string PropOrEmpty(Dictionary<string, string> properties, string column)
    {
        if (properties.TryGetValue(column, out var value))
            return value;

        return "";
    }

    //A cell a spreadsheet would run is written behind an apostrophe, which Excel and LibreOffice read as "text
    //follows", before the quoting. The apostrophe shows when Excel imports the CSV; that is the price of a CSV that
    //cannot run anything. Until 2026-11-07 the value went out as it stood, and a stored =HYPERLINK(...) was a link.
    private static string CsvEscape(string value)
    {
        value ??= "";

        if (RunsAsAFormula(value))
            value = "'" + value;

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    //Whether Excel or LibreOffice would run the cell as a formula when it opens the CSV: it begins with =, +, -, @, a
    //tab or a carriage return (OWASP's CSV injection: =cmd|' /C calc'!A0 runs a program). A cell that reads as a plain
    //number, -5 or +3.5, is one, and is left alone.
    private static bool RunsAsAFormula(string value)
    {
        if (value.Length == 0 || value[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r'))
            return false;

        return !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    #endregion

    #region DOT export

    private static readonly string[] DotLabelProps = { "Name", "name", "title", "Title" };

    ///<summary>
    ///Renders a GraphTable as Graphviz DOT — a directed graph whose node labels are each vertex's
    ///display name (falling back to its type label) and whose edges carry the edge label. This is the
    ///output of the "DOT" export option and mirrors the DOT that GraphImport can read back in.
    ///</summary>
    public static string ToDot(GraphTable table)
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph G {");

        foreach (var node in table.Nodes)
            sb.AppendLine($"    {DotQuote(node.Id)} [label={DotQuote(DotNodeLabel(node))}];");

        if (table.Nodes.Count > 0 && table.Edges.Count > 0)
            sb.AppendLine();

        foreach (var edge in table.Edges)
        {
            if (string.IsNullOrEmpty(edge.Source) || string.IsNullOrEmpty(edge.Target))
                continue;

            sb.AppendLine($"    {DotQuote(edge.Source)} -> {DotQuote(edge.Target)} [label={DotQuote(edge.Label)}];");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    //A node's display label: its Name/name/title property when present, otherwise its type label.
    private static string DotNodeLabel(GraphRow node)
    {
        foreach (var name in DotLabelProps)
            if (node.Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

        return node.Label;
    }

    //Wraps a value as a DOT double-quoted string, escaping backslashes, quotes and newlines.
    private static string DotQuote(string value)
    {
        value ??= "";
        value = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

        return "\"" + value + "\"";
    }

    #endregion

    #region Schema (data model)

    ///<summary>
    ///Derives a data-model / schema meta-graph from a loaded result: one node per vertex label
    ///(with an instance count and the union of its property keys) and one edge per distinct
    ///(sourceLabel, edgeLabel, targetLabel) relationship (with a count). The output matches
    ///<see cref="SchemaBuilder.BuildSchemaGraphJson"/> so it renders through the normal 2D/3D/table
    ///pipeline. Engine-agnostic: works on any loaded graph (Gremlin, SPARQL, Cosmos, or pasted /
    ///imported data) with no live schema query.
    ///</summary>
    public static string BuildSchemaFromData(JsonElement results)
    {
        var table = ToTable(results);

        var idToLabel = new Dictionary<string, string>();
        foreach (var n in table.Nodes)
            idToLabel[n.Id] = n.Label;

        //Per vertex label: instance count + the union of property keys seen on it.
        var labelCounts = new Dictionary<string, int>();
        var labelKeys = new Dictionary<string, SortedSet<string>>();
        foreach (var n in table.Nodes)
        {
            labelCounts[n.Label] = labelCounts.GetValueOrDefault(n.Label) + 1;

            if (!labelKeys.TryGetValue(n.Label, out var keys))
            {
                keys = new SortedSet<string>(StringComparer.Ordinal);
                labelKeys[n.Label] = keys;
            }

            foreach (var k in n.Properties.Keys)
                if (!k.StartsWith(GdbvKeys.Prefix))
                    keys.Add(k);
        }

        //Per (sourceLabel, edgeLabel, targetLabel): relationship count. Endpoints are resolved to
        //their labels via the loaded vertices; an edge whose endpoints aren't loaded is skipped.
        var triples = new Dictionary<(string Out, string Edge, string In), int>();
        foreach (var e in table.Edges)
        {
            if (e.Source == null || e.Target == null)
                continue;

            if (!idToLabel.TryGetValue(e.Source, out var outLabel) || !idToLabel.TryGetValue(e.Target, out var inLabel))
                continue;

            var key = (outLabel, e.Label, inLabel);
            triples[key] = triples.GetValueOrDefault(key) + 1;
        }

        var elements = new List<object>();

        foreach (var label in labelCounts.Keys)
        {
            var props = new Dictionary<string, string> { ["name"] = label, ["count"] = labelCounts[label].ToString() };

            if (labelKeys.TryGetValue(label, out var keys) && keys.Count > 0)
                props["keys"] = string.Join(", ", keys);

            elements.Add(new { id = label, label, properties = props });
        }

        foreach (var t in triples)
        {
            var props = new Dictionary<string, string> { ["count"] = t.Value.ToString() };
            elements.Add(new { id = $"{t.Key.Out}-{t.Key.Edge}->{t.Key.In}", label = t.Key.Edge, outV = t.Key.Out, inV = t.Key.In, properties = props });
        }

        return JsonSerializer.Serialize(elements);
    }

    #endregion
}
