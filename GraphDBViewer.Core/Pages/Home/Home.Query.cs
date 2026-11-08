using System.Text.Json;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Query execution against the connected database: Load DB / Schema, the editor's Run action
//(every engine, through its provider), and the schema vocabulary that feeds Monaco's autocomplete.
public partial class Home
{

    //A run is under way in the tab in front (the multiple-connections spec, story 4: each tab its own run, which writes
    //its answer into that tab wherever the person goes meanwhile).
    private bool isQuerying => activeTab.QueryCts != null;

    //Another tab of the connection in front is running, so this one waits: the connection's tabs share one live link.
    private bool connectionBusy => activeConnection.RunningTab != null && !ReferenceEquals(activeConnection.RunningTab, activeTab);

    //What the run buttons are held by: this tab's run, or another of the connection's.
    private bool runHeld => isQuerying || connectionBusy;

    //Set while a run that still writes into the tab in front is under way (the debugger's), which holds the switches.
    private bool frontRunHeld;

    private SchemaVocabulary schemaVocab;//latest schema, fed to the NL-query prompt; null until one is read

    //Tool runner given to the NL-query modal so a tool-using model can run queries against the live graph. Needs a
    //live connection to a database that can host the tools; the modal hands them over while the editor is set to
    //that database's language.
    private ILlmToolRunner NlToolRunner
    {
        get
        {
            if (db != null && isConnected && Caps.AiTools)
                //The database's own language, not the editor dropdown's — the tool runs against the database.
                return new QueryToolRunner(db, Qb, Provider.QueryLanguage);

            return null;
        }
    }

    //Ask AI's picture tool, while a graph is drawn in the 2D or 3D view: null otherwise, so a model is not offered a look
    //at nothing. The view can change while a question is answered, so the look asks again (LookAtGraphAsync).
    private Func<CancellationToken, Task<LlmToolOutput>> NlLook
    {
        get
        {
            if (HasGraphData && (visualizationMode == 2 || visualizationMode == 3))
                return LookAtGraphAsync;

            return null;
        }
    }

    //The graph as the view draws it now, for a model that sees pictures (GraphPictureRunner, 2026-11-02, GDS Viewer's look).
    //Only the 2D and 3D views draw it, so the JSON and the table say why there is no picture.
    private async Task<LlmToolOutput> LookAtGraphAsync(CancellationToken ct)
    {
        const string Png = "data:image/png;base64,";

        if (!HasGraphData)
            return LlmToolOutput.Failure("No graph is drawn: nothing has been loaded into the view.");

        if (visualizationMode != 2 && visualizationMode != 3)
            return LlmToolOutput.Failure("The graph is not drawn now: the view shows it as JSON or a table. Ask the user to switch to the 2D or 3D view to look at it.");

        var url = await GraphView.PictureAsync(GraphPictureRunner.MostPictureSide);

        if (string.IsNullOrEmpty(url) || !url.StartsWith(Png, StringComparison.Ordinal))
            return LlmToolOutput.Failure("The view could not be drawn as a picture.");

        var view = "2D";

        if (visualizationMode == 3)
            view = "3D";

        return new LlmToolOutput { Text = $"The graph as the {view} view draws it now.", Png = url.Substring(Png.Length) };
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

    //Starts a run in a tab of a connection, the cancellation scope its Stop uses, and returns its token. The run writes
    //into this tab and this connection from here on, not into whichever is in front when it answers.
    private CancellationToken BeginQuery(QueryTab tab, ViewerConnection owner)
    {
        tab.QueryCts?.Dispose();
        tab.QueryCts = new CancellationTokenSource();
        owner.RunningTab = tab;
        tab.QueryError = null;
        tab.ResultsCleared = false;//a query is producing results, so the canvas is no longer a blank build surface

        return tab.QueryCts.Token;
    }

    private void EndQuery(QueryTab tab, ViewerConnection owner)
    {
        tab.QueryCts?.Dispose();
        tab.QueryCts = null;

        if (ReferenceEquals(owner.RunningTab, tab))
            owner.RunningTab = null;
    }

    //A run in the tab in front that the switches wait for (the debugger's, whose state is the page's, not a tab's).
    private CancellationToken BeginQuery()
    {
        frontRunHeld = true;

        return BeginQuery(activeTab, activeConnection);
    }

    private void EndQuery()
    {
        EndQuery(activeTab, activeConnection);
        frontRunHeld = false;
    }

    //Cancels the in-flight query/load of the tab in front (aborts the WebSocket receive or HTTP request).
    private void CancelQuery()
    {
        activeTab.QueryCts?.Cancel();
    }

    //Whether a tab is the one on the page: its connection in front and it the tab in front of that.
    private bool IsInFront(QueryTab tab)
    {
        return ReferenceEquals(tab, activeTab);
    }

    //A run's answer, shown: drawn when its tab is in front, as before; kept with its tab when the person went elsewhere,
    //to be drawn when they come back to it (SwitchTabAsync redraws), and the page told so its row says the run ended.
    private async Task ShowRunAsync(QueryTab tab, bool draw)
    {
        if (!IsInFront(tab))
        {
            await SaveTabsAsync();
            StateHasChanged();
        }
        else if (draw)
            await RenderGraphAsync();
        else
            await SaveTabsAsync();
    }

    //Set while the top-bar connection button pulses to point the user at where to connect.
    private bool pulseConnect { get => activeConnection.PulseConnect; set => activeConnection.PulseConnect = value; }

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

    //Why the run buttons wait while another tab of this connection runs: its tabs share one live link.
    private const string BusyConnectionTitle = "Another query tab of this connection is running. Run becomes available when it finishes, or press Stop in that tab.";

    //What the Run button says: Stop while this tab runs, why it waits while another tab of the connection does.
    private string RunTitle
    {
        get
        {
            if (isQuerying)
                return "Stop: cancel the running query";

            if (connectionBusy)
                return BusyConnectionTitle;

            return "Run Query: run the query from the editor against the connected database";
        }
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
        await LoadDbAsync(activeTab, activeConnection);
    }

    //Load DB in a tab of a connection, its answer written into that tab wherever the person goes meanwhile.
    private async Task LoadDbAsync(QueryTab tab, ViewerConnection owner)
    {
        //Ignore re-entrant clicks while a query/load is already running — spam-clicking Load DB would
        //otherwise stack up graph redraws that race the Cytoscape layout teardown.
        if (tab.QueryCts != null || owner.RunningTab != null)
            return;

        //The button is hidden without this, but the guard is explicit so the Gremlin these methods build
        //can never reach a database that doesn't speak it.
        if (!Caps.BrowseGraph)
            return;

        var database = owner.Db;

        if (database == null)
        {
            WarnNoConnection();
            return;
        }

        var qb = Qb;
        var ct = BeginQuery(tab, owner);
        tab.QueryResults = null;
        tab.LastResultData = default;
        tab.GraphResultData = default;

        try
        {
            int limit;
            if (tab.LoadDbLimit > 0)
                limit = tab.LoadDbLimit;
            else
                limit = 100;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await database.ExecuteAsync(qb.LimitedVertices(limit), ct);
            tab.QueryResults = result.ToString();
            if (!result.IsError)
                tab.LastResultData = result.Data;
            else
                tab.QueryError = result.Error;

            var graphResult = await database.ExecuteAsync(qb.FullGraph(limit), ct);
            if (!graphResult.IsError)
                tab.GraphResultData = graphResult.Data;
            else if (string.IsNullOrEmpty(tab.QueryError))
                tab.QueryError = graphResult.Error;
            sw.Stop();
            tab.LastQueryMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException)
        {
            tab.QueryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            tab.QueryResults = $"Error: {ex.Message}";
            tab.QueryError = ex.Message;
        }
        finally
        {
            EndQuery(tab, owner);
        }

        await ShowRunAsync(tab, tab.VisualizationMode == 2 || tab.VisualizationMode == 3);
    }

    //Loads the schema vocabulary (vertex/edge labels + property keys) and pushes it to the Monaco
    //editor for schema-aware autocomplete. Runs inside the connect/commit flow so it never overlaps a
    //user query on the shared WebSocket. Failures are non-fatal (autocomplete just stays step-only).
    private async Task RefreshSchemaVocabularyAsync()
    {
        await RefreshSchemaVocabularyAsync(activeConnection);
    }

    //The same for a given connection, which may have gone behind by the time its schema is read: its own builder is
    //rebuilt, and the editor and the AI panel, which follow the connection in front, are told only while it is in front.
    private async Task RefreshSchemaVocabularyAsync(ViewerConnection owner)
    {
        var provider = GraphDbProviders.For(owner.Connection.DatabaseType);

        if (!provider.Capabilities.BrowseGraph || owner.Db == null || !owner.IsConnected)
            return;

        try
        {
            var schema = await provider.SchemaSource.ReadVocabularyAsync(owner.Db);

            //An engine that cannot write its own browse queries without the schema gets its builder rebuilt
            //here — this is the moment the collection names become known.
            if (provider.QueryBuilderForSchema != null && !schema.HasErrors)
                owner.SchemaQueryBuilder = provider.QueryBuilderForSchema(schema.Vocabulary);

            if (!ReferenceEquals(owner, activeConnection))
                return;

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
        var tab = activeTab;
        var owner = activeConnection;

        if (tab.QueryCts != null || owner.RunningTab != null)
            return;

        if (!Caps.BrowseGraph)
            return;

        var database = owner.Db;

        if (database == null)
        {
            WarnNoConnection();
            return;
        }

        var provider = Provider;
        var ct = BeginQuery(tab, owner);
        tab.QueryResults = null;
        tab.LastResultData = default;
        tab.GraphResultData = default;

        try
        {
            var json = await provider.SchemaSource.BuildSchemaGraphJsonAsync(database, ct);

            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            tab.LastResultData = parsed;
            tab.GraphResultData = parsed;
            tab.QueryResults = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (OperationCanceledException)
        {
            tab.QueryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            tab.QueryResults = $"Error: {ex.Message}";
            tab.QueryError = ex.Message;
        }
        finally
        {
            EndQuery(tab, owner);
        }

        await ShowRunAsync(tab, true);
    }

    private async Task RunQueryAsync()
    {
        var tab = activeTab;
        var owner = activeConnection;

        //One run at a time on a connection, whose tabs share one live link; Stop is where to go meanwhile.
        if (tab.QueryCts != null || owner.RunningTab != null)
            return;

        var database = owner.Db;

        if (database == null)
        {
            WarnNoConnection();
            return;
        }

        //Read before the run, because the editor's text can change under a long one. Like EffectiveData, and
        //unlike the builder's other uses, Run has no capability gate in front of it, so it has to allow for an
        //engine with no builder: SPARQL composes no queries, and every SPARQL query threw here until the live
        //database check ran one.
        bool mutating = Qb != null && Qb.IsMutating(queryText ?? "");

        var ct = BeginQuery(tab, owner);
        tab.QueryResults = null;
        tab.TableResult = null;
        tab.LastResultData = default;
        tab.GraphResultData = default;

        try
        {
            var query = tab.QueryText ?? "";
            await AddToHistoryAsync(query);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await database.ExecuteAsync(query, ct);
            sw.Stop();
            tab.LastQueryMs = sw.Elapsed.TotalMilliseconds;

            tab.QueryResults = result.ToString();

            //Visualize the query result itself. graphResultData stays unset, so the
            //graph/stats/table/exports fall back to this result. Use "Load DB" to load
            //the whole graph (vertices + edges) instead.
            if (result.IsError)
                tab.QueryError = result.Error;
            else if (result.Table != null)
                tab.TableResult = result.Table;
            else
                tab.LastResultData = result.Data;
        }
        catch (OperationCanceledException)
        {
            tab.QueryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            tab.QueryResults = $"Error: {ex.Message}";
            tab.QueryError = ex.Message;
        }
        finally
        {
            EndQuery(tab, owner);
        }

        //A query that changed the graph changed its schema with it. See RunEachLineAsync for what that
        //costs when nothing re-reads it.
        if (mutating && string.IsNullOrEmpty(tab.QueryError))
            await RefreshSchemaVocabularyAsync(owner);

        //A rows/boolean answer has no graph to draw, so there's nothing to render.
        await ShowRunAsync(tab, tab.TableResult == null && (tab.VisualizationMode == 2 || tab.VisualizationMode == 3));

        //Live follows what is on screen, so a new read re-points the subscription at it; a read that answered behind
        //is not on screen.
        if (liveUpdates && IsInFront(tab))
            await StartLiveAsync();
    }

    //Runs the editor's statements one per line (like committing the Generated buffer), so multi-statement
    //addV/addE mutations all execute. Run submits the whole text as a single Gremlin script, where only
    //the last (iterated) statement actually runs — the earlier addV lines silently do nothing. The last
    //statement's result is shown / rendered; any per-line errors are reported.
    private async Task RunEachLineAsync()
    {
        var tab = activeTab;
        var owner = activeConnection;

        if (tab.QueryCts != null || owner.RunningTab != null)
            return;

        var database = owner.Db;

        if (database == null)
        {
            WarnNoConnection();
            return;
        }

        //Read before the run, because the editor's text can change under a long one.
        bool mutating = Qb.IsMutating(queryText ?? "");

        var lines = (queryText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
            return;

        var ct = BeginQuery(tab, owner);
        tab.QueryResults = null;
        tab.TableResult = null;
        tab.LastResultData = default;
        tab.GraphResultData = default;

        try
        {
            await AddToHistoryAsync(tab.QueryText ?? "");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var errors = new List<string>();

            //Each statement is its own round-trip, so a long run reports live progress; throttled to ~50
            //repaints so re-rendering the page doesn't itself slow the run. (See CommitGeneratedQueriesAsync.)
            int step = Math.Max(1, lines.Length / 50);

            for (int i = 0; i < lines.Length; i++)
            {
                var r = await database.ExecuteAsync(lines[i], ct);
                if (r.IsError)
                    errors.Add($"{lines[i]}: {r.Error}");

                if ((i + 1) % step == 0 || i == lines.Length - 1)
                {
                    tab.QueryResults = $"Running… {i + 1} / {lines.Length}";
                    StateHasChanged();
                }
            }

            sw.Stop();
            tab.LastQueryMs = sw.Elapsed.TotalMilliseconds;

            if (errors.Count > 0)
            {
                tab.QueryError = string.Join("\n", errors);
                tab.QueryResults = $"Ran {lines.Length} statement(s), {errors.Count} failed:\n{tab.QueryError}";
            }
            else
            {
                //A batch runner — report the count rather than render the last statement's result (which,
                //for a mutation, is just the single element it created, not the whole batch). The defined-
                //empty result makes the "query ran successfully" status line show, and Load DB shows the graph.
                tab.QueryResults = $"Ran {lines.Length} statement(s) successfully.";
                tab.LastResultData = JsonDocument.Parse("[]").RootElement;
            }
        }
        catch (OperationCanceledException)
        {
            tab.QueryResults = "Query canceled.";
        }
        catch (Exception ex)
        {
            tab.QueryResults = $"Error: {ex.Message}";
            tab.QueryError = ex.Message;
        }
        finally
        {
            EndQuery(tab, owner);
        }

        //A query that changed the graph changed its schema with it, and only connecting and committing
        //re-read it before now. So loading a sample graph left the vocabulary as it was read at connect
        //time — empty, for a graph that was empty then — and the Ask AI panel went on stating that as
        //fact long after it stopped being true. A model told the graph is empty does not go looking.
        if (mutating && string.IsNullOrEmpty(tab.QueryError))
            await RefreshSchemaVocabularyAsync(owner);

        await ShowRunAsync(tab, tab.VisualizationMode == 2 || tab.VisualizationMode == 3);
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

            if (connectionBusy)
                return BusyConnectionTitle;

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
