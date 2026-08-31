using System.Text.Json;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Query execution against the connected database: Load DB / Schema, the editor's Run action
//(Gremlin and SPARQL), and the schema vocabulary that feeds Monaco's autocomplete.
public partial class Home
{

    //Global UI state (not part of a tab): a query is running.
    private bool isQuerying;
    private CancellationTokenSource queryCts;

    private GraphDbTable tableResult;//last row/boolean result (a graph answer flows through lastResultData instead)
    private SchemaVocabulary schemaVocab;//latest schema, fed to the NL-query prompt; null until one is read

    //Tool runner given to the NL-query modal so a tool-using model can run read-only queries against the
    //live graph. Needs a live connection to a database that can host the tool (the runner executes Gremlin).
    private ILlmToolRunner NlToolRunner
    {
        get
        {
            if (db != null && isConnected && Caps.AiTools)
                //The database's own language, not the editor dropdown's — the tool runs against the database.
                return new GremlinToolRunner(db, Qb, Provider.EditorLanguage);

            return null;
        }
    }

    //The vertex query Load DB runs (with the current limit), shown in the button's tooltip.
    private string LoadDbQueryPreview
    {
        get
        {
            int limit;
            if (loadDbLimit > 0)
                limit = loadDbLimit;
            else
                limit = 100;

            return Qb.LimitedVertices(limit);
        }
    }

    //Starts a fresh cancellation scope for a query operation and returns its token.
    private CancellationToken BeginQuery()
    {
        queryCts?.Dispose();
        queryCts = new CancellationTokenSource();
        isQuerying = true;
        queryError = null;
        resultsCleared = false;//a query is producing results, so the canvas is no longer a blank build surface

        return queryCts.Token;
    }

    private void EndQuery()
    {
        isQuerying = false;
        queryCts?.Dispose();
        queryCts = null;
    }

    //Cancels the in-flight query/load (aborts the WebSocket receive or HTTP request).
    private void CancelQuery()
    {
        queryCts?.Cancel();
    }

    //Set while the top-bar connection button pulses to point the user at where to connect.
    private bool pulseConnect;

    //A user action (Run / Load DB / Schema) needs a live connection but there isn't one: warn in the
    //stats bar and pulse the top-bar connection button.
    private void WarnNoConnection()
    {
        queryError = "No active connection: connect to a database first.";
        _ = PulseConnectAsync();
    }

    //Pulses the connection button for a few seconds, then clears the flag so it can retrigger.
    private async Task PulseConnectAsync()
    {
        pulseConnect = true;
        StateHasChanged();

        await Task.Delay(3000);

        pulseConnect = false;
        StateHasChanged();
    }

    //The Run button doubles as a Stop button while a query is running (it turns red) so the toolbar
    //layout never shifts — clicking it then cancels the in-flight query instead of starting a new one.
    private async Task RunOrCancelAsync()
    {
        if (isQuerying)
            CancelQuery();
        else
            await RunQueryAsync();
    }

    //Transient "copied" state for the editor's floating Copy button.
    private bool queryCopied;

    //Copies the editor's query text to the clipboard, with brief feedback on the floating Copy button.
    private async Task CopyQueryEditorAsync()
    {
        if (string.IsNullOrEmpty(queryText))
            return;

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", queryText);
            queryCopied = true;
            StateHasChanged();

            await Task.Delay(1500);
            queryCopied = false;
        }
        catch
        {
            //Clipboard API unavailable (e.g. non-secure context) — ignore.
        }
    }

    //Clears the editor after confirming, since it can't be undone.
    private async Task ClearQueryEditorAsync()
    {
        bool confirmed = await JS.InvokeAsync<bool>("confirm", "Clear the query editor? This can't be undone.");
        if (!confirmed)
            return;

        queryText = "";
        await SaveQueryAsync();
        StateHasChanged();
    }

    //Transient "copied" state for the Generated editor's floating Copy button.
    private bool generatedCopied;

    //Copies the Generated tab's staged queries to the clipboard, with brief feedback on the Copy button.
    private async Task CopyGeneratedEditorAsync()
    {
        if (string.IsNullOrEmpty(generatedQueryText))
            return;

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", generatedQueryText);
            generatedCopied = true;
            StateHasChanged();

            await Task.Delay(1500);
            generatedCopied = false;
        }
        catch
        {
            //Clipboard API unavailable (e.g. non-secure context) — ignore.
        }
    }

    private async Task LoadDbAsync()
    {
        //Ignore re-entrant clicks while a query/load is already running — spam-clicking Load DB would
        //otherwise stack up graph redraws that race the Cytoscape layout teardown.
        if (isQuerying)
            return;

        //The button is hidden without this, but the guard is explicit so the Gremlin these methods build
        //can never reach a database that doesn't speak it.
        if (!Caps.BrowseGraph)
            return;

        if (db == null)
        {
            WarnNoConnection();
            return;
        }

        var ct = BeginQuery();
        queryResults = null;
        lastResultData = default;
        graphResultData = default;

        try
        {
            int limit;
            if (loadDbLimit > 0)
                limit = loadDbLimit;
            else
                limit = 100;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await db.ExecuteAsync(Qb.LimitedVertices(limit), ct);
            queryResults = result.ToString();
            if (!result.IsError)
                lastResultData = result.Data;
            else
                queryError = result.Error;

            var graphResult = await db.ExecuteAsync(Qb.FullGraph(limit), ct);
            if (!graphResult.IsError)
                graphResultData = graphResult.Data;
            else if (string.IsNullOrEmpty(queryError))
                queryError = graphResult.Error;
            sw.Stop();
            lastQueryMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            queryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            queryResults = $"Error: {ex.Message}";
            queryError = ex.Message;
        }
        finally
        {
            EndQuery();
        }

        if (visualizationMode == 2 || visualizationMode == 3)
            await RenderGraphAsync();
        else
            await SaveTabsAsync();
    }

    //Loads the schema vocabulary (vertex/edge labels + property keys) and pushes it to the Monaco
    //editor for schema-aware autocomplete. Runs inside the connect/commit flow so it never overlaps a
    //user query on the shared WebSocket. Failures are non-fatal (autocomplete just stays step-only).
    private async Task RefreshSchemaVocabularyAsync()
    {
        if (!Caps.BrowseGraph || db == null || !isConnected)
            return;

        try
        {
            var provider = Provider;
            var schema = await provider.SchemaSource.ReadVocabularyAsync(db);

            //An engine that cannot write its own browse queries without the schema gets its builder rebuilt
            //here — this is the moment the collection names become known.
            if (provider.QueryBuilderForSchema != null && !schema.HasErrors)
                schemaQueryBuilder = provider.QueryBuilderForSchema(schema.Vocabulary);

            //A failed schema query reads back as an empty vocabulary, so a timeout or an RU throttle looks
            //exactly like a graph with nothing in it. Autocomplete can live with that — it just goes
            //step-only either way. The NL prompt can't: it would state an empty schema as fact. Publish
            //null instead, which the prompt reads as "unknown" rather than "empty".
            if (schema.HasErrors)
                schemaVocab = null;
            else
                schemaVocab = schema.Vocabulary;

            await JS.InvokeVoidAsync("monacoInterop.setSchema", schema.Vocabulary);
        }
        catch { }
    }

    //Loads the graph schema as a label-graph: vertex labels (with counts + property keys)
    //connected by edge labels. Built from groupCount()/group()/project() queries.
    private async Task LoadSchemaAsync()
    {
        if (isQuerying)
            return;

        if (!Caps.BrowseGraph)
            return;

        if (db == null)
        {
            WarnNoConnection();
            return;
        }

        var ct = BeginQuery();
        queryResults = null;
        lastResultData = default;
        graphResultData = default;

        try
        {
            var json = await Provider.SchemaSource.BuildSchemaGraphJsonAsync(db, ct);

            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            lastResultData = parsed;
            graphResultData = parsed;
            queryResults = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (OperationCanceledException)
        {
            queryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            queryResults = $"Error: {ex.Message}";
            queryError = ex.Message;
        }
        finally
        {
            EndQuery();
        }

        await RenderGraphAsync();
    }

    private async Task RunQueryAsync()
    {
        if (db == null)
        {
            WarnNoConnection();
            return;
        }

        //Read before the run, because the editor's text can change under a long one.
        bool mutating = Qb.IsMutating(queryText ?? "");

        var ct = BeginQuery();
        queryResults = null;
        tableResult = null;
        lastResultData = default;
        graphResultData = default;
        try
        {
            var query = queryText ?? "";
            await AddToHistoryAsync(query);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await db.ExecuteAsync(query, ct);
            sw.Stop();
            lastQueryMs = sw.Elapsed.TotalMilliseconds;

            queryResults = result.ToString();

            //Visualize the query result itself. graphResultData stays unset, so the
            //graph/stats/table/exports fall back to this result. Use "Load DB" to load
            //the whole graph (vertices + edges) instead.
            if (result.IsError)
                queryError = result.Error;
            else if (result.Table != null)
                tableResult = result.Table;
            else
                lastResultData = result.Data;
        }
        catch (OperationCanceledException)
        {
            queryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            queryResults = $"Error: {ex.Message}";
            queryError = ex.Message;
        }
        finally
        {
            EndQuery();
        }

        //A query that changed the graph changed its schema with it. See RunEachLineAsync for what that
        //costs when nothing re-reads it.
        if (mutating && string.IsNullOrEmpty(queryError))
            await RefreshSchemaVocabularyAsync();

        //A rows/boolean answer has no graph to draw, so there's nothing to render.
        if (tableResult == null && (visualizationMode == 2 || visualizationMode == 3))
            await RenderGraphAsync();
        else
            await SaveTabsAsync();

        //Live follows what is on screen, so a new read re-points the subscription at it.
        if (liveUpdates)
            await StartLiveAsync();
    }

    //Runs the editor's statements one per line (like committing the Generated buffer), so multi-statement
    //addV/addE mutations all execute. Run submits the whole text as a single Gremlin script, where only
    //the last (iterated) statement actually runs — the earlier addV lines silently do nothing. The last
    //statement's result is shown / rendered; any per-line errors are reported.
    private async Task RunEachLineAsync()
    {
        if (db == null)
        {
            WarnNoConnection();
            return;
        }

        //Read before the run, because the editor's text can change under a long one.
        bool mutating = Qb.IsMutating(queryText ?? "");

        var lines = (queryText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
            return;

        var ct = BeginQuery();
        queryResults = null;
        tableResult = null;
        lastResultData = default;
        graphResultData = default;

        try
        {
            await AddToHistoryAsync(queryText ?? "");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var errors = new List<string>();

            //Each statement is its own round-trip, so a long run reports live progress; throttled to ~50
            //repaints so re-rendering the page doesn't itself slow the run. (See CommitGeneratedQueriesAsync.)
            int step = Math.Max(1, lines.Length / 50);

            for (int i = 0; i < lines.Length; i++)
            {
                var r = await db.ExecuteAsync(lines[i], ct);
                if (r.IsError)
                    errors.Add($"{lines[i]}: {r.Error}");

                if ((i + 1) % step == 0 || i == lines.Length - 1)
                {
                    queryResults = $"Running… {i + 1} / {lines.Length}";
                    StateHasChanged();
                }
            }

            sw.Stop();
            lastQueryMs = sw.Elapsed.TotalMilliseconds;

            if (errors.Count > 0)
            {
                queryError = string.Join("\n", errors);
                queryResults = $"Ran {lines.Length} statement(s), {errors.Count} failed:\n{queryError}";
            }
            else
            {
                //A batch runner — report the count rather than render the last statement's result (which,
                //for a mutation, is just the single element it created, not the whole batch). The defined-
                //empty result makes the "query ran successfully" status line show, and Load DB shows the graph.
                queryResults = $"Ran {lines.Length} statement(s) successfully.";
                lastResultData = JsonDocument.Parse("[]").RootElement;
            }
        }
        catch (OperationCanceledException)
        {
            queryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            queryResults = $"Error: {ex.Message}";
            queryError = ex.Message;
        }
        finally
        {
            EndQuery();
        }

        //A query that changed the graph changed its schema with it, and only connecting and committing
        //re-read it before now. So loading a sample graph left the vocabulary as it was read at connect
        //time — empty, for a graph that was empty then — and the Ask AI panel went on stating that as
        //fact long after it stopped being true. A model told the graph is empty does not go looking.
        if (mutating && string.IsNullOrEmpty(queryError))
            await RefreshSchemaVocabularyAsync();

        if (visualizationMode == 2 || visualizationMode == 3)
            await RenderGraphAsync();
        else
            await SaveTabsAsync();
    }


    ///<summary>
    ///Why Commit is unavailable, or null when it is. Sits on a wrapper rather than the button: a disabled
    ///control receives no mouse events and so never shows a title of its own, which is precisely when
    ///there is something worth explaining.
    ///</summary>
    private string CommitTitle
    {
        get
        {
            if (isQuerying)
                return "A query is running. Commit becomes available when it finishes.";

            if (string.IsNullOrWhiteSpace(generatedQueryText))
                return "Nothing is staged yet. Edit a property, add a component or link two nodes, and the queries appear here to commit.";

            return "Run every staged query against the database. This is the point of no return.";
        }
    }

    ///<summary>Why Create Component is unavailable, or what it does when it is not.</summary>
    private string CreateComponentTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(newComponentLabel))
                return "Type a label first — it is what the new vertex will be called, e.g. person.";

            return "Create a new component (vertex). It is staged, not written, until you commit.";
        }
    }

    //Loads an example query into the Query editor and switches to the Query tab.
    private async Task UseExample(string query)
    {
        queryText = query;
        queryEditorTab = 1;
        await SaveQueryAsync();
    }

    //Clears the current tab's results down to an empty graph so a new one can be built from scratch
    //(add components / link nodes). The base is an empty array — not "no data" — so the graph view keeps
    //rendering an empty canvas and staged edits still preview on it. Staged Generated queries are kept.
    private async Task ClearResultsAsync()
    {
        //Offline, the drawing IS the staged edits — there are no database results to clear, and emptying the
        //base leaves the staged drawing on the canvas. So "Clear results" acts like "Discard changes":
        //dropping the staged queries is what actually empties the canvas.
        //That only holds while something is actually staged. reflectDbState is persisted and any edit turns
        //it off, so reopening the app disconnected (restored graph, nothing staged) landed here and silently
        //discarded nothing — the button looked dead until you connected. With no staged text there is a real
        //graph to clear, so fall through to the normal clear instead.
        if (!isConnected && !reflectDbState && !string.IsNullOrWhiteSpace(generatedQueryText))
        {
            await DiscardGeneratedQueriesAsync();
            return;
        }

        bool confirmed = await JS.InvokeAsync<bool>("confirm", "Clear the canvas? All displayed results will be removed so you can start a new graph. Staged changes in the Generated tab are kept.");
        if (!confirmed)
            return;

        var empty = JsonDocument.Parse("[]").RootElement;
        lastResultData = empty;
        graphResultData = empty;
        resultsCleared = true;//deliberately blank, for building — not a failed visualization
        queryResults = "[]";
        queryError = null;
        tableResult = null;
        lastQueryMs = null;
        searchTerm = null;
        saved2dPositionsJson = null;
        saved3dPositionsJson = null;
        hiddenLabels.Clear();

        if (visualizationMode == 2 || visualizationMode == 3)
            await RenderGraphAsync();
        else
            await SaveTabsAsync();
    }
}
