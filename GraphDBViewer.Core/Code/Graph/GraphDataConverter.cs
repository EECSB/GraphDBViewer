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

    ///<summary>Converts a Gremlin/GraphSON result array into Cytoscape.js elements JSON (nodes + edges) for the 2D view.</summary>
    public static string ToCytoscapeJson(JsonElement results, IReadOnlyDictionary<string, LabelStyle> styles = null, int edgeColorMode = 0, IReadOnlyDictionary<string, string> edgeColors = null)
    {
        var elements = new List<object>();
        var seenNodes = new HashSet<string>();

        if (results.ValueKind == JsonValueKind.Array)
        {
            //Pass 1: add real vertices first so an edge listed before its vertices doesn't
            //leave an id-only placeholder node shadowing the real (named) vertex.
            foreach (var item in results.EnumerateArray())
            {
                var unwrapped = UnwrapGraphSON(item);

                if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
                {
                    AddCytoscapeNode(elements, seenNodes, v, styles);
                    AddCytoscapeNode(elements, seenNodes, o, styles);
                }
                else if (IsVertex(unwrapped))
                {
                    AddCytoscapeNode(elements, seenNodes, unwrapped, styles);
                }
            }

            //Pass 2: add edges (endpoints become placeholders only when truly absent above).
            foreach (var item in results.EnumerateArray())
            {
                var unwrapped = UnwrapGraphSON(item);

                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                    AddCytoscapeEdge(elements, v, e, o, edgeColorMode, edgeColors);
                else if (IsEdge(unwrapped))
                    AddCytoscapeEdgeStandalone(elements, seenNodes, unwrapped, edgeColorMode, edgeColors);
            }
        }

        return JsonSerializer.Serialize(elements);
    }

    private static void AddCytoscapeNode(List<object> elements, HashSet<string> seen, JsonElement vertex, IReadOnlyDictionary<string, LabelStyle> styles)
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
        {
            elements.Add(new { data });
        }
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

    private static void AddCytoscapeEdge(List<object> elements, JsonElement from, JsonElement edge, JsonElement to, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        string sourceId = GetId(from);
        string targetId = GetId(to);
        string edgeLabel = GetStringProp(edge, "label") ?? "edge";
        string edgeId = GetId(edge) ?? Guid.NewGuid().ToString();

        var properties = ExtractProperties(edge);
        elements.Add(new { data = new { id = edgeId, source = sourceId, target = targetId, label = edgeLabel, edgeColor = ResolveEdgeColor(edgeLabel, properties, edgeColorMode, edgeColors), properties } });
    }

    private static void AddCytoscapeEdgeStandalone(List<object> elements, HashSet<string> seenNodes, JsonElement edge, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        string sourceId = GetEdgeVertexId(edge, "outV") ?? GetEdgeVertexId(edge, "inV");
        string targetId = GetEdgeVertexId(edge, "inV") ?? GetEdgeVertexId(edge, "outV");

        if (sourceId == null || targetId == null)
            return;

        if (seenNodes.Add(sourceId))
            elements.Add(new { data = new { id = sourceId, label = sourceId, shape = "rectangle", properties = new Dictionary<string, string>() } });

        if (seenNodes.Add(targetId))
            elements.Add(new { data = new { id = targetId, label = targetId, shape = "rectangle", properties = new Dictionary<string, string>() } });

        string edgeLabel = GetStringProp(edge, "label") ?? "edge";
        string edgeId = GetId(edge) ?? Guid.NewGuid().ToString();
        var properties = ExtractProperties(edge);

        elements.Add(new { data = new { id = edgeId, source = sourceId, target = targetId, label = edgeLabel, edgeColor = ResolveEdgeColor(edgeLabel, properties, edgeColorMode, edgeColors), properties } });
    }

    #endregion

    #region 3D Force Graph

    ///<summary>Converts a Gremlin/GraphSON result array into 3d-force-graph JSON ({ nodes, links }) for the 3D view, grouping node color by vertex label.</summary>
    public static string ToForceGraphJson(JsonElement results, IReadOnlyDictionary<string, LabelStyle> styles = null, int edgeColorMode = 0, IReadOnlyDictionary<string, string> edgeColors = null)
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
            foreach (var item in results.EnumerateArray())
            {
                var unwrapped = UnwrapGraphSON(item);

                if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
                {
                    AddForceNode(nodes, seenNodes, v, labelGroups, ref groupCounter, styles);
                    AddForceNode(nodes, seenNodes, o, labelGroups, ref groupCounter, styles);
                }
                else if (IsVertex(unwrapped))
                {
                    AddForceNode(nodes, seenNodes, unwrapped, labelGroups, ref groupCounter, styles);
                }
            }

            //Pass 2: add edges (endpoints become placeholders only when truly absent above).
            foreach (var item in results.EnumerateArray())
            {
                var unwrapped = UnwrapGraphSON(item);

                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                    AddForceLink(links, v, e, o, edgeColorMode, edgeColors);
                else if (IsEdge(unwrapped))
                    AddForceLinkStandalone(nodes, links, seenNodes, unwrapped, labelGroups, ref groupCounter, edgeColorMode, edgeColors);
            }
        }

        return JsonSerializer.Serialize(new { nodes, links });
    }

    private static void AddForceNode(List<object> nodes, HashSet<string> seen, JsonElement vertex, Dictionary<string, int> labelGroups, ref int groupCounter, IReadOnlyDictionary<string, LabelStyle> styles)
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

    private static void AddForceLink(List<object> links, JsonElement from, JsonElement edge, JsonElement to, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        string source = GetId(from);
        string target = GetId(to);
        string label = GetStringProp(edge, "label") ?? "edge";

        var properties = ExtractProperties(edge);
        links.Add(new { source, target, label, color = ResolveEdgeColor(label, properties, edgeColorMode, edgeColors), properties });
    }

    private static void AddForceLinkStandalone(List<object> nodes, List<object> links, HashSet<string> seenNodes, JsonElement edge, Dictionary<string, int> labelGroups, ref int groupCounter, int edgeColorMode, IReadOnlyDictionary<string, string> edgeColors)
    {
        string sourceId = GetEdgeVertexId(edge, "outV") ?? GetEdgeVertexId(edge, "inV");
        string targetId = GetEdgeVertexId(edge, "inV") ?? GetEdgeVertexId(edge, "outV");

        if (sourceId == null || targetId == null) 
            return;

        if (seenNodes.Add(sourceId))
        {
            if (!labelGroups.TryGetValue("vertex", out int g))
            {
                g = ++groupCounter;
                labelGroups["vertex"] = g;
            }

            nodes.Add(new { id = sourceId, label = sourceId, group = g, properties = new Dictionary<string, string>() });
        }

        if (seenNodes.Add(targetId))
        {
            if (!labelGroups.TryGetValue("vertex", out int g))
            {
                g = ++groupCounter;
                labelGroups["vertex"] = g;
            }

            nodes.Add(new { id = targetId, label = targetId, group = g, properties = new Dictionary<string, string>() });
        }

        string label = GetStringProp(edge, "label") ?? "edge";
        var properties = ExtractProperties(edge);
        links.Add(new { source = sourceId, target = targetId, label, color = ResolveEdgeColor(label, properties, edgeColorMode, edgeColors), properties });
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

    private static JsonElement UnwrapGraphSON(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("@type", out var typeEl) && element.TryGetProperty("@value", out var inner))
        {
            string? type = typeEl.GetString();

            //g:Map stores key-value pairs as flat array: ["k1", v1, "k2", v2, ...]
            //Convert to a proper JSON object for downstream processing.
            if (type == "g:Map" && inner.ValueKind == JsonValueKind.Array)
                return ConvertGraphSONMapToObject(inner);

            return UnwrapGraphSON(inner);
        }

        return element;
    }

    private static JsonElement ConvertGraphSONMapToObject(JsonElement kvArray)
    {
        var dict = new Dictionary<string, JsonElement>();
        for (int i = 0; i + 1 < kvArray.GetArrayLength(); i += 2)
        {
            string? key = kvArray[i].GetString();
            if (key != null)
                dict[key] = kvArray[i + 1];
        }

        var json = JsonSerializer.Serialize(dict);

        return JsonDocument.Parse(json).RootElement;
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

    private static bool IsVertex(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        if (el.TryGetProperty("type", out var t) && t.GetString() == "vertex")
            return true;

        if (el.TryGetProperty("id", out _) && el.TryGetProperty("label", out _) && !el.TryGetProperty("outV", out _))
            return true;

        return false;
    }

    private static bool IsEdge(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        if (el.TryGetProperty("type", out var t) && t.GetString() == "edge")
            return true;

        if (el.TryGetProperty("outV", out _) || el.TryGetProperty("inV", out _))
            return true;

        return false;
    }

    #endregion

    #region Property Helpers

    private static string GetId(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (!el.TryGetProperty("id", out var idProp))
            return null;

        if (idProp.ValueKind == JsonValueKind.Object && idProp.TryGetProperty("@value", out var inner))
            return inner.ToString();

        return idProp.ToString();
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

    private static string GetEdgeVertexId(JsonElement edge, string direction)
    {
        if (!edge.TryGetProperty(direction, out var val))
            return null;

        if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("@value", out var inner))
            return inner.ToString();

        return val.ToString();
    }

    private static string ExtractLabel(JsonElement vertex, IReadOnlyDictionary<string, LabelStyle> styles, IReadOnlyDictionary<string, string> properties)
    {
        string id = GetId(vertex) ?? "?";
        string typeLabel = GetStringProp(vertex, "label");
        string perNodeDisplay = properties?.GetValueOrDefault(GdbvKeys.Display);

        if (vertex.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in DisplayPropertyNames(typeLabel, styles, perNodeDisplay))
            {
                if (!props.TryGetProperty(propName, out var propVal))
                    continue;

                if (propVal.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in propVal.EnumerateArray())
                    {
                        var unwrapped = UnwrapGraphSON(entry);
                        if (unwrapped.TryGetProperty("value", out var val))
                            return val.ToString();
                        if (unwrapped.ValueKind == JsonValueKind.String)
                            return unwrapped.GetString();
                    }
                }
                else if (propVal.ValueKind == JsonValueKind.String)
                {
                    return propVal.GetString();
                }
                else if (propVal.ValueKind == JsonValueKind.Object)
                {
                    var unwrapped = UnwrapGraphSON(propVal);
                    if (unwrapped.TryGetProperty("value", out var val))
                        return val.ToString();
                }
            }
        }

        if (typeLabel != null)
            return $"{typeLabel} ({id})";

        return id;
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

    ///<summary>Extracts an element's properties into a flat name→value dictionary, unwrapping GraphSON property shapes and skipping id/label/type metadata.</summary>
    public static Dictionary<string, string> ExtractProperties(JsonElement element)
    {
        var result = new Dictionary<string, string>();
        var skip = new HashSet<string> { "id", "label", "type", "outV", "inV", "outVLabel", "inVLabel" };

        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (skip.Contains(prop.Name) || prop.Name.StartsWith("_"))
                    continue;

                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in prop.Value.EnumerateArray())
                    {
                        var unwrapped = UnwrapGraphSON(entry);
                        if (unwrapped.TryGetProperty("value", out var val))
                        {
                            result[prop.Name] = val.ToString();
                            break;
                        }
                        result[prop.Name] = unwrapped.ToString();
                        break;
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var unwrapped = UnwrapGraphSON(prop.Value);
                    if (unwrapped.TryGetProperty("value", out var val))
                        result[prop.Name] = val.ToString();
                    else
                        result[prop.Name] = unwrapped.ToString();
                }
                else
                {
                    result[prop.Name] = prop.Value.ToString();
                }
            }
        }
        else
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (skip.Contains(prop.Name) || prop.Name.StartsWith("_"))
                    continue;
                 
                if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    result[prop.Name] = prop.Value.ToString();
            }
        }

        return result;
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
        {
            foreach (var item in results.EnumerateArray())
            {
                var unwrapped = UnwrapGraphSON(item);

                if (TryExtractTraversalTriple(unwrapped, out var v, out var e, out var o))
                {
                    AddTableNode(table, seenNodes, v);
                    AddTableNode(table, seenNodes, o);
                    AddTableEdge(table, seenEdges, e);
                }
                else if (IsVertex(unwrapped))
                {
                    AddTableNode(table, seenNodes, unwrapped);
                }
                else if (IsEdge(unwrapped))
                {
                    AddTableEdge(table, seenEdges, unwrapped);
                }
            }
        }

        return table;
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

        foreach (var item in results.EnumerateArray())
        {
            var unwrapped = UnwrapGraphSON(item);

            if (TryExtractTraversalTriple(unwrapped, out var v, out _, out var o))
            {
                AddSuggestion(suggestions, seenIds, seenText, v, styles);
                AddSuggestion(suggestions, seenIds, seenText, o, styles);
            }
            else if (IsVertex(unwrapped))
            {
                AddSuggestion(suggestions, seenIds, seenText, unwrapped, styles);
            }
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

        var row = new GraphRow
        {
            Id = id,
            IdType = GetIdType(edge),
            Label = GetStringProp(edge, "label") ?? "edge",
            Source = GetEdgeVertexId(edge, "outV") ?? GetEdgeVertexId(edge, "inV"),
            Target = GetEdgeVertexId(edge, "inV") ?? GetEdgeVertexId(edge, "outV"),
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
        {
            edgesById.Remove(edit.Id);
        }
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
        else
        {
            if (edgesById.TryGetValue(edit.Id, out var e))
                return e;
        }

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

    private static string CsvEscape(string value)
    {
        value ??= "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
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
