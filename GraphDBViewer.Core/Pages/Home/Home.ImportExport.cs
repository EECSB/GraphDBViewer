using System.Text.Json;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Import / export: clipboard copies, the image / table / 3D-model downloads, and turning pasted
//JSON (or DOT/Mermaid) into a rendered graph or staged addV/addE queries.
public partial class Home
{

    //Copies the raw JSON results to the clipboard, with brief "Copied" feedback on the button.
    private async Task CopyJsonAsync()
    {
        if (string.IsNullOrEmpty(queryResults))
            return;

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", queryResults);
            jsonCopied = true;

            StateHasChanged();

            await Task.Delay(1500);
            jsonCopied = false;
        }
        catch
        {
            //Clipboard API unavailable (e.g. non-secure context) — ignore.
        }
    }

    //The Export "Text" control: a format <select> (JSON / Gremlin queries) in front of the download
    //button, matching the Table / Image / 3D exports. The button downloads the current graph as a text
    //file — the JSON it's drawn from (round-trips via "Visualize pasted JSON"), or the equivalent
    //add-node / add-edge queries in the connected database's language.
    private string exportChoice = "json";

    //Names the query flavor in the export dropdown and its filename, so exporting while connected to Neo4j
    //offers "Cypher queries" and not a file of Gremlin it would refuse.
    private string QueryLanguageName
    {
        get
        {
            if (Provider.EditorLanguage == "cypher")
                return "Cypher";

            if (Provider.EditorLanguage == "aql")
                return "AQL";

            if (Provider.EditorLanguage == "dql")
                return "DQL";

            return "Gremlin";
        }
    }

    private async Task RunExportChoiceAsync()
    {
        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return;

        string filename;
        string content;

        if (exportChoice == "gremlin")
        {
            filename = $"graph-{QueryLanguageName.ToLowerInvariant()}.txt";
            content = ComposedOrExplained(() => GraphImport.QueriesFromJson(data, Qb));
        }
        else
        {
            filename = "graph-json.txt";
            content = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        }

        if (string.IsNullOrWhiteSpace(content))
            return;

        await JS.InvokeVoidAsync("exportInterop.downloadText", filename, content, "text/plain");
    }

    //Tooltip shown when an export control is disabled because no graph is loaded.
    private const string LoadGraphHint = "Load a graph first (Load DB, run a query, or import) to enable this.";

    //Tooltip for the image-export control, explaining the current disabled reason.
    private string ImageExportHint
    {
        get
        {
            if (!HasGraphData)
                return LoadGraphHint;
            else if (imageExportFormat == "svg" && visualizationMode != 2)
                return "SVG export is available in the 2D view only.";
            else if (imageExportFormat == "dot")
                return "Download the graph as Graphviz DOT (.dot).";
            else if (imageExportFormat == "svglayout")
                return "Auto-layout the graph and download it as an SVG image.";
            else
                return "Download the current view as an image.";
        }
    }

    //Tooltip for the 3D-model-export control, explaining the current disabled reason.
    private string ModelExportHint
    {
        get
        {
            if (!HasGraphData)
                return LoadGraphHint;
            else if (visualizationMode != 3)
                return "Switch to the 3D view to export a 3D model.";
            else
                return "Download the 3D model in the selected format.";
        }
    }

    //True when the selected image format can be produced for the current view.
    //SVG is 2D-only; PNG/JPEG work in 2D or 3D.
    private bool CanExportImage
    {
        get
        {
            if (!HasGraphData)
                return false;

            if (imageExportFormat == "svg")
                return visualizationMode == 2;
            else if (imageExportFormat == "dot" || imageExportFormat == "svglayout")
                return true;
            else
                return visualizationMode == 2 || visualizationMode == 3;
        }
    }

    //Exports the current view as an image in the selected format (png/jpeg/svg/dot/graphviz).
    private async Task ExportImageAsync()
    {
        try
        {
            if (imageExportFormat == "svg")
            {
                if (visualizationMode != 2)
                    return;

                var svg = await JS.InvokeAsync<string>("cytoscapeToSvg");

                if (!string.IsNullOrEmpty(svg))
                    await JS.InvokeVoidAsync("exportInterop.downloadText", "graph.svg", svg, "image/svg+xml");

                return;
            }

            if (imageExportFormat == "dot")
            {
                var data = CurrentGraphData();
                if (data.ValueKind == JsonValueKind.Undefined)
                    return;

                var dot = GraphDataConverter.ToDot(GraphDataConverter.ToTable(data));
                await JS.InvokeVoidAsync("exportInterop.downloadText", "graph.dot", dot, "text/vnd.graphviz");

                return;
            }

            if (imageExportFormat == "svglayout")
            {
                var data = CurrentGraphData();
                if (data.ValueKind == JsonValueKind.Undefined)
                    return;

                //Render the graph through an off-screen Cytoscape layout and download the laid-out SVG image.
                var elements = GraphDataConverter.ToCytoscapeJson(data, labelStyles, edgeColorMode, edgeColors);
                var svg = await JS.InvokeAsync<string>("cytoscapeLayoutToSvg", elements, cy2dLayout);

                if (!string.IsNullOrEmpty(svg))
                    await JS.InvokeVoidAsync("exportInterop.downloadText", "graph.svg", svg, "image/svg+xml");

                return;
            }

            var dataUrl = await GraphView.ImageAsync(imageExportFormat);

            if (string.IsNullOrEmpty(dataUrl))
                return;

            string ext;
            if (imageExportFormat == "jpeg")
                ext = "jpg";
            else
                ext = "png";

            await JS.InvokeVoidAsync("exportInterop.download", "graph." + ext, dataUrl);
        }
        catch { }
    }

    //Exports the current 3D scene as a 3D-model download in the selected format.
    private async Task Export3dAsync()
    {
        try
        {
            var content = await JS.InvokeAsync<string>("graph3DInterop.export3d", g3dExportFormat);

            if (string.IsNullOrEmpty(content))
                return;

            string mime;
            if (g3dExportFormat == "stl")
                mime = "model/stl";
            else if (g3dExportFormat == "ply")
                mime = "text/plain";
            else if (g3dExportFormat == "gltf")
                mime = "model/gltf+json";
            else
                mime = "model/obj";

            await JS.InvokeVoidAsync("exportInterop.downloadText", "graph." + g3dExportFormat, content, mime);
        }
        catch { }
    }

    //Exports the current graph table (vertices + edges) as CSV or a color-styled .xlsx.
    private async Task ExportTableAsync()
    {
        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return;

        var table = GraphDataConverter.ToTable(data);

        if (tableExportFormat == "xlsx")
        {
            var bytes = ExcelExport.BuildXlsx(table, labelStyles);
            var dataUrl = "data:application/vnd.openxmlformats-officedocument.spreadsheetml.sheet;base64," + Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync("exportInterop.download", "graph.xlsx", dataUrl);
        }
        else
        {
            var csv = GraphDataConverter.ToCsv(table);
            await JS.InvokeVoidAsync("exportInterop.downloadText", "graph.csv", csv, "text/csv");
        }
    }

    //Asks before an Import button overwrites the staged Generated queries — the way every other
    //destructive action here does. True when nothing is staged (nothing to lose). Callers invoke it only
    //once the input is known to produce an import, so a malformed paste never prompts for a no-op.
    //Deliberately not ShowUncommittedBanner: its !reflectDbState clause only decides whether the banner
    //is on screen, and the staged queries are just as gone when it isn't.
    private async Task<bool> ConfirmReplaceStagedAsync()
    {
        if (string.IsNullOrWhiteSpace(generatedQueryText))
            return true;

        return await JS.InvokeAsync<bool>("confirm", "Replace the staged changes? The queries in the Generated tab will be overwritten by this import.");
    }

    ///<summary>
    ///Makes sure the connected database can take an import at all, asking first when that means changing
    ///its schema. Only Dgraph needs anything — it cannot find an imported node by the id it came with
    ///unless that id is indexed — and an index is a change beyond the data the user asked to import, so it
    ///is a question rather than something done quietly. Returns false when the user declines or it failed.
    ///</summary>
    private async Task<bool> PrepareForImportAsync()
    {
        var preparation = Provider.ImportPreparation;

        //Nothing to prepare, or nothing to prepare against — an offline import writes to no database.
        if (preparation == null || db == null || !isConnected)
            return true;

        if (await preparation.IsReadyAsync(db))
            return true;

        bool confirmed = await JS.InvokeAsync<bool>("confirm",
            $"{Provider.DisplayName} needs a schema change before it can take an import:\n\n    {preparation.Requirement}\n\n{preparation.Reason}\n\nApply it now?");

        if (!confirmed)
        {
            queryError = $"Import canceled: {Provider.DisplayName} needs \"{preparation.Requirement}\" first.";

            return false;
        }

        var error = await preparation.PrepareAsync(db);

        if (error != null)
        {
            queryError = $"Could not prepare {Provider.DisplayName} for import: {error}";

            return false;
        }

        return true;
    }

    //Renders pasted JSON (GraphSON result array) without a live connection.
    private async Task VisualizePastedJsonAsync(string input)
    {
        try
        {
            JsonElement parsed;

            if (GraphImport.LooksLikeGraphText(input))
            {
                var graph = GraphImport.Parse(input);
                if (graph == null)
                    return;

                if (!await ConfirmReplaceStagedAsync())
                    return;

                parsed = JsonSerializer.Deserialize<JsonElement>(GraphImport.ToRenderJson(graph));

                //Assign the staged buffer directly (not via the property) so it doesn't kick off the
                //fire-and-forget optimistic sync — that would race the full RenderGraphAsync redraw below
                //and leave Cytoscape mid-teardown ("Cannot read properties of null (reading 'notify')").
                if (!await PrepareForImportAsync())
                    return;

                var staged = ComposedOrExplained(() => GraphImport.ToQueries(graph, Qb));

                if (staged == null)
                    return;

                activeTab.GeneratedQueryText = staged;

                if (!string.IsNullOrWhiteSpace(generatedQueryText))
                    queryEditorTab = 2;
            }
            else if (GraphImport.LooksLikeQueryScript(input))
            {
                if (!await ConfirmReplaceStagedAsync())
                    return;

                //Re-import exported addV/addE queries as a drawing: load them into the staged buffer and
                //preview them as edits over an empty baseline (offline — nothing executes), exactly the
                //state they'd be in if drawn by hand. Direct field assignment avoids racing the redraw below.
                activeTab.GeneratedQueryText = input.Trim();
                reflectDbState = false;
                parsed = JsonSerializer.Deserialize<JsonElement>("[]");

                //A drawing is a graph — show it in 2D if the user imported from JSON/Table.
                if (visualizationMode != 2 && visualizationMode != 3)
                    visualizationMode = 2;

                if (!string.IsNullOrWhiteSpace(generatedQueryText))
                    queryEditorTab = 2;
            }
            else
            {
                parsed = JsonSerializer.Deserialize<JsonElement>(input);
            }

            lastResultData = parsed;
            graphResultData = parsed;
            offlineMode = true;//an imported/pasted graph is offline work — reveal the workspace
            resultsCleared = false;//pasted data is a real visualization, not a blank build surface
            queryResults = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
            showConnectionCard = false;
            showImportExport = false;

            if (visualizationMode == 2 || visualizationMode == 3)
                await RenderGraphAsync();
        }
        catch
        {
            //Invalid input is reported inline by the input panel; ignore here.
        }
    }

    //Accepting a generated knowledge graph: Replace routes through the shared import path — inheriting
    //its ConfirmReplaceStagedAsync guard — while Merge appends the modal's precomputed delta.
    private async Task HandleKgUseGraphAsync((string Gremlin, bool Merge) args)
    {
        if (args.Merge)
            await MergeGeneratedGraphAsync(args.Gremlin);
        else
            await VisualizePastedJsonAsync(args.Gremlin);
    }

    //Merge mode (AC 9): concatenates the delta the modal already folded and previewed. Deliberately
    //thin — no fold, no parse, no confirm (nothing is lost; the app's rule is confirm iff work is
    //lost). Appending rather than re-emitting is what keeps hand-edited staged statements alive: the
    //edit parser skips what it can't parse, so a rebuild would silently drop them.
    private async Task MergeGeneratedGraphAsync(string delta)
    {
        if (string.IsNullOrWhiteSpace(delta))
            return;

        //The field, not the property — the setter's fire-and-forget optimistic sync would race the
        //full redraw below, exactly like the import branches above.
        if (string.IsNullOrWhiteSpace(activeTab.GeneratedQueryText))
            activeTab.GeneratedQueryText = delta;
        else
            activeTab.GeneratedQueryText = activeTab.GeneratedQueryText + "\n" + delta;

        //Preview the staged edits so the merged entities appear on the canvas right away.
        reflectDbState = false;
        queryEditorTab = 2;

        //Reveal the workspace when nothing is connected; with a live connection the workspace is
        //already visible and offline mode would fight the connection's own state.
        if (!isConnected)
            offlineMode = true;

        if (visualizationMode != 2 && visualizationMode != 3)
            visualizationMode = 2;

        await RenderGraphAsync();
    }

    //Converts pasted JSON (or DOT/Mermaid) into addV/addE in the Generated query tab,
    //ready to commit to the database. Does not render — use Visualize for that.
    private async Task GenerateImportQueries(string input)
    {
        string gremlin;

        if (GraphImport.LooksLikeGraphText(input))
        {
            var graph = GraphImport.Parse(input);
            if (graph == null)
                return;

            if (!await PrepareForImportAsync())
                return;

            gremlin = ComposedOrExplained(() => GraphImport.ToQueries(graph, Qb));
        }
        else if (GraphImport.LooksLikeQueryScript(input))
        {
            gremlin = input.Trim();
        }
        else
        {
            JsonElement data;
            try
            {
                data = JsonSerializer.Deserialize<JsonElement>(input);
            }
            catch
            {
                return;
            }

            if (!await PrepareForImportAsync())
                return;

            gremlin = ComposedOrExplained(() => GraphImport.QueriesFromJson(data, Qb));
        }

        if (string.IsNullOrWhiteSpace(gremlin))
            return;

        if (!await ConfirmReplaceStagedAsync())
            return;

        offlineMode = true;
        //The property write is deliberate here, unlike Visualize's field writes: no redraw follows, so the
        //setter's fire-and-forget optimistic sync is what previews the staged queries — there is nothing
        //for it to race.
        generatedQueryText = gremlin;
        queryEditorTab = 2;
    }

    //The Offline-mode button toggles offline drawing on and off, staying mutually exclusive with a live
    //connection: entering offline mode disconnects first, and connecting exits offline mode.
    private async Task ToggleOfflineModeAsync()
    {
        if (offlineMode)
        {
            ExitOfflineMode();
            return;
        }

        await StartBlankCanvasAsync();
    }

    //Leaves offline drawing mode without connecting: hides the workspace again and reopens the connection
    //card so the user can connect. The drawn graph stays in the tab until a connection replaces it.
    private void ExitOfflineMode()
    {
        offlineMode = false;
        showConnectionCard = true;
    }

    //Starts a blank offline drawing surface: seeds an empty canvas (so the editing toolbar appears) in 2D
    //with edit-preview on, closes the import/connection panels, and drops into add-component mode so the
    //user can place components right away — all without a database.
    private async Task StartBlankCanvasAsync()
    {
        //Offline mode and a live connection are mutually exclusive — drop the connection first.
        if (isConnected)
            Disconnect("Disconnected to start offline mode.");

        offlineMode = true;
        var empty = JsonSerializer.Deserialize<JsonElement>("[]");
        lastResultData = empty;
        graphResultData = empty;
        queryResults = "[]";
        reflectDbState = false;
        visualizationMode = 2;
        showConnectionCard = false;
        showImportExport = false;
        deleteMode = false;
        connectionPickMode = null;
        ResetPickState();
        addNodeMode = true;

        await RenderGraphAsync();
    }
}
