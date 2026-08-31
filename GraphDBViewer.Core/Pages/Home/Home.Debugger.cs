using System.Text.Json;
using GraphDBViewerWeb.Code;
using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Pages;

//The query debugger: a transient session on the active tab's query that runs it truncated at each
//step (traverser counts + samples), plus the Profile and Explain tabs.
public partial class Home
{

    private bool showDebug;
    private string debugError;//mutation/validity message when stepping can't run
    private List<DebugStepResult> debugSteps = new();
    private int debugStepIndex = -1;//the focused step (highlighted in the editor)
    private int debugTab = 1;//1 = Steps, 2 = Profile, 3 = Explain
    private double debugProfileTotalMs;
    private List<MetricsRow> debugProfileRows = new();
    private string debugProfileError;
    private bool debugProfileLoaded;
    private string debugExplain;
    private bool debugExplainLoaded;

    //Traversal simulation: for the focused step, which of its elements reach the query's final result.
    private List<SimulationRow> debugSimRows = new();
    private int debugSimStepIndex = -1;//the step the loaded simulation is for (matched against debugStepIndex to display)
    private bool debugSimLoaded;
    private string debugSimError;
    private bool debugSimHideFailed;
    private const int DebugSimLimit = 1000;

    //Traverser inspection: tracing one element (picked from the simulation list) through the remaining steps.
    private List<InspectStepRow> debugInspectRows = new();
    private string debugInspectId;
    private string debugInspectLabel;
    private string debugInspectName;
    private bool debugInspectLoaded;
    private string debugInspectError;

    //Step-into navigation: the full step tree (top-level steps plus their __-sub-traversals) and a cursor
    //that can descend into a sub-traversal. debugTopTreeIndex maps each top-level debuggable step (its
    //debugSteps index) to its position in the tree, so panel clicks and the cursor stay in sync.
    private List<GremlinStep> debugTree = new();
    private int debugCursor = -1;
    private List<int> debugTopTreeIndex = new();

    //Starts a debug session on the active tab's query: runs the query truncated at each step and shows
    //the traverser count after each, so you can see where results appear or vanish. Mutating queries are
    //refused (they'd change the graph); only Explain (which never executes) is offered for those.
    private async Task RunDebugAsync()
    {
        if (!Caps.Debug || db == null || !isConnected)
            return;

        var query = (queryText ?? "").Trim();
        if (query.Length == 0)
            return;

        queryEditorTab = 1;
        showDebug = true;
        debugError = null;
        debugSteps.Clear();
        debugStepIndex = -1;
        debugTree = new();
        debugCursor = -1;
        debugTopTreeIndex = new();
        await SetDebugKeysAsync(false);
        debugProfileLoaded = false;
        debugExplainLoaded = false;
        debugProfileRows = new();
        debugProfileError = null;
        debugExplain = null;
        debugSimLoaded = false;
        debugSimStepIndex = -1;
        debugSimRows = new();
        debugSimError = null;
        debugTab = 1;

        if (Qb.IsMutating(query))
        {
            debugError = "This query modifies the graph. Step-through and profiling are disabled so your data isn't changed. Use the Explain tab (it never executes the query).";
            return;
        }

        //An engine whose query prefixes aren't valid queries has no Steps tab to fill; the debugger opens
        //on Profile instead, which is native to it.
        if (!Caps.DebugSteps)
        {
            debugTab = 2;
            await RunProfileAsync();

            return;
        }

        var steps = GremlinStepParser.Parse(query).Where(s => s.Debuggable).ToList();
        if (steps.Count == 0)
        {
            debugError = "No debuggable steps found in this query.";
            return;
        }

        //The tree carries the same top-level steps plus their __-sub-traversals, so the cursor can step in.
        debugTree = GremlinStepParser.ParseTree(query);
        debugTopTreeIndex = new();
        for (int i = 0; i < debugTree.Count; i++)
            if (debugTree[i].Depth == 0 && debugTree[i].Debuggable)
                debugTopTreeIndex.Add(i);

        await SetDebugKeysAsync(true);

        var ct = BeginQuery();

        try
        {
            foreach (var step in steps)
            {
                var prefix = query.Substring(0, step.End);
                var result = new DebugStepResult { Step = step, Prefix = prefix };

                var r = await db.ExecuteAsync(prefix + ".count()", ct);
                if (r.IsError)
                    result.Error = r.Error;
                else
                    result.Count = ReadCountResult(r.Data);

                debugSteps.Add(result);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            debugError = "Debug run canceled.";
        }
        finally
        {
            EndQuery();
        }
    }

    private async Task ExitDebug()
    {
        showDebug = false;
        debugStepIndex = -1;
        debugCursor = -1;
        debugTree = new();
        debugTopTreeIndex = new();
        debugSteps.Clear();
        debugSimLoaded = false;
        debugSimStepIndex = -1;
        debugSimRows = new();
        await SetDebugKeysAsync(false);
    }

    //Focuses a top-level step (from a panel row or an editor click): highlights it, moves the cursor onto
    //it, and lazily fetches a sample.
    private async Task FocusDebugStepAsync(int index)
    {
        if (index < 0 || index >= debugSteps.Count)
            return;

        debugStepIndex = index;

        if (index < debugTopTreeIndex.Count)
            debugCursor = debugTopTreeIndex[index];

        await LoadDebugSampleAsync(index);
    }

    //Step-cursor navigation over the tree: Step Back / Step Over move among navigable siblings at the
    //cursor's depth; Step Into descends into a __-sub-traversal; Step Out returns to the enclosing step.
    private async Task DebugStepBackAsync()
    {
        await MoveCursorAsync(StepBackTarget(debugCursor));
    }

    private async Task DebugStepOverAsync()
    {
        await MoveCursorAsync(StepOverTarget(debugCursor));
    }

    private async Task DebugStepIntoAsync()
    {
        await MoveCursorAsync(StepIntoTarget(debugCursor));
    }

    private async Task DebugStepOutAsync()
    {
        await MoveCursorAsync(StepOutTarget(debugCursor));
    }

    //Called from JS when an arrow key is pressed while the Steps tab is open (see keyboardInterop).
    [JSInvokable]
    public async Task OnDebugStepKey(string direction)
    {
        if (!showDebug || debugTab != 1)
            return;

        if (direction == "over")
            await DebugStepOverAsync();
        else if (direction == "back")
            await DebugStepBackAsync();
        else if (direction == "into")
            await DebugStepIntoAsync();
        else if (direction == "out")
            await DebugStepOutAsync();

        StateHasChanged();
    }

    //Moves the cursor to a tree index (when valid), keeping the focused top-level step — and so the count
    //table, sample and simulation/inspection — on the step that encloses the cursor.
    private async Task MoveCursorAsync(int treeIndex)
    {
        if (treeIndex < 0 || treeIndex >= debugTree.Count)
            return;

        debugCursor = treeIndex;

        int top = EnclosingTopDebugIndex(treeIndex);
        if (top >= 0)
        {
            debugStepIndex = top;
            await LoadDebugSampleAsync(top);
        }
    }

    //A tree step is navigable if it can be debugged (counted) or has a sub-traversal to step into.
    private bool DebugStepHasChild(int i)
    {
        return i >= 0 && i + 1 < debugTree.Count && debugTree[i + 1].Depth > debugTree[i].Depth;
    }

    private bool DebugStepNavigable(int i)
    {
        if (i < 0 || i >= debugTree.Count)
            return false;

        return debugTree[i].Debuggable || DebugStepHasChild(i);
    }

    //The next navigable step at the same or a shallower depth (the following sibling, or a step-out at a block's end).
    private int StepOverTarget(int cursor)
    {
        int depth = cursor < 0 ? int.MaxValue : debugTree[cursor].Depth;

        for (int j = cursor + 1; j < debugTree.Count; j++)
            if (debugTree[j].Depth <= depth && DebugStepNavigable(j))
                return j;

        return -1;
    }

    //The previous navigable step at the same or a shallower depth (the preceding sibling).
    private int StepBackTarget(int cursor)
    {
        if (cursor < 0)
            return -1;

        int depth = debugTree[cursor].Depth;

        for (int j = cursor - 1; j >= 0; j--)
            if (debugTree[j].Depth <= depth && DebugStepNavigable(j))
                return j;

        return -1;
    }

    //The first navigable step inside the cursor's sub-traversal (skips the __ source), or -1 if it has none.
    private int StepIntoTarget(int cursor)
    {
        if (!DebugStepHasChild(cursor))
            return -1;

        int childDepth = debugTree[cursor].Depth + 1;

        for (int j = cursor + 1; j < debugTree.Count && debugTree[j].Depth >= childDepth; j++)
            if (debugTree[j].Depth == childDepth && DebugStepNavigable(j))
                return j;

        return -1;
    }

    //The step that encloses the cursor's sub-traversal (its parent), or -1 if the cursor is already top-level.
    private int StepOutTarget(int cursor)
    {
        if (cursor < 0 || debugTree[cursor].Depth == 0)
            return -1;

        int parentDepth = debugTree[cursor].Depth - 1;

        for (int j = cursor - 1; j >= 0; j--)
            if (debugTree[j].Depth == parentDepth)
                return j;

        return -1;
    }

    //The debugSteps index of the depth-0 step that encloses the cursor, or -1 when that step isn't debuggable.
    private int EnclosingTopDebugIndex(int treeIndex)
    {
        int j = treeIndex;
        while (j >= 0 && debugTree[j].Depth != 0)
            j--;

        if (j < 0)
            return -1;

        return debugTopTreeIndex.IndexOf(j);
    }

    //The exact character range of the cursor when it's inside a sub-traversal, so the editor can highlight the
    //nested step — null at the top level, where the active step decoration already covers it.
    private string DebugCursorRangeJson()
    {
        if (debugCursor < 0 || debugCursor >= debugTree.Count || debugTree[debugCursor].Depth == 0)
            return null;

        var step = debugTree[debugCursor];
        return JsonSerializer.Serialize(new[] { step.Start, step.End });
    }

    //A breadcrumb of the cursor's position inside sub-traversals (e.g. "where › out('composes')"), or null
    //when the cursor is at the top level.
    private string DebugCursorBreadcrumb()
    {
        if (debugCursor < 0 || debugCursor >= debugTree.Count || debugTree[debugCursor].Depth == 0)
            return null;

        var parts = new List<string> { debugTree[debugCursor].Text };

        int j = StepOutTarget(debugCursor);
        while (j >= 0)
        {
            parts.Insert(0, debugTree[j].Name);

            if (debugTree[j].Depth == 0)
                break;

            j = StepOutTarget(j);
        }

        return string.Join(" › ", parts);
    }

    //Toggles the arrow-key step shortcuts (active only while the Steps tab is open).
    private async Task SetDebugKeysAsync(bool active)
    {
        try
        {
            await JS.InvokeVoidAsync("keyboardInterop.setDebugStepActive", active);
        }
        catch { }
    }

    private async Task LoadDebugSampleAsync(int index)
    {
        var step = debugSteps[index];
        if (step.SampleLoaded || step.Error != null || step.Count is null or 0)
            return;

        var ct = BeginQuery();

        try
        {
            var r = await db.ExecuteAsync(step.Prefix + ".limit(20)", ct);
            if (r.IsError)
                step.Error = r.Error;
            else
                step.Sample = JsonSerializer.Serialize(r.Data, new JsonSerializerOptions { WriteIndented = true });

            step.SampleLoaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }
    }

    //Runs a traversal simulation for the given step: for each element at that step (up to DebugSimLimit),
    //runs the rest of the query to report whether it reaches the final result and how many results it
    //yields. Cached against the step (debugSimStepIndex) so it only shows for the step it was run on.
    private async Task RunSimulationAsync(int stepIndex)
    {
        if (stepIndex < 0 || stepIndex >= debugSteps.Count || db == null)
            return;

        debugSimStepIndex = stepIndex;
        debugSimLoaded = true;
        debugSimError = null;
        debugSimHideFailed = false;
        debugSimRows = new();
        debugInspectLoaded = false;
        debugInspectRows = new();

        var query = GremlinDebugQueries.BuildSimulation((queryText ?? "").Trim(), debugSteps[stepIndex].Step, DebugSimLimit);
        if (query == null)
        {
            debugSimError = "Simulation isn't available for this step. It doesn't yield graph elements, or it's the last step.";
            return;
        }

        var ct = BeginQuery();

        try
        {
            var r = await db.ExecuteAsync(query, ct);
            if (r.IsError)
                debugSimError = r.Error;
            else
                debugSimRows = ParseSimulationRows(r.Data);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }
    }

    //Whether a traversal simulation can run at the given step (it yields graph elements and has downstream steps).
    private bool CanSimulateStep(int index)
    {
        if (index < 0 || index >= debugSteps.Count)
            return false;

        return GremlinDebugQueries.CanSimulate((queryText ?? "").Trim(), debugSteps[index].Step);
    }

    //The simulation rows to display for the focused step (all, or only survivors when "hide dead" is on).
    private IEnumerable<SimulationRow> DebugSimVisibleRows()
    {
        if (debugSimHideFailed)
            return debugSimRows.Where(r => r.Survives);

        return debugSimRows;
    }

    //Parses a simulation projection (an array of {id,label,name,survives,outCount} maps) into rows.
    private static List<SimulationRow> ParseSimulationRows(JsonElement data)
    {
        var rows = new List<SimulationRow>();

        if (data.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var item in data.EnumerateArray())
        {
            var m = GraphDataConverter.UnwrapElement(item);
            if (m.ValueKind != JsonValueKind.Object)
                continue;

            rows.Add(new SimulationRow
            {
                Id = SimString(m, "id"),
                Label = SimString(m, "label"),
                Name = SimString(m, "name"),
                Survives = SimLong(m, "survives") > 0,
                OutCount = SimLong(m, "outCount")
            });
        }

        return rows;
    }

    private static string SimString(JsonElement map, string key)
    {
        if (!map.TryGetProperty(key, out var val))
            return "";

        return GraphDataConverter.UnwrapElement(val).ToString();
    }

    private static long SimLong(JsonElement map, string key)
    {
        if (!map.TryGetProperty(key, out var val))
            return 0;

        var v = GraphDataConverter.UnwrapElement(val);

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;

        if (long.TryParse(v.ToString(), out var n2))
            return n2;

        return 0;
    }

    //Traces a single element (picked from the simulation list) through the remaining steps: runs a count
    //after each remaining step so you can see exactly where this one element drops out of the query.
    private async Task InspectElementAsync(SimulationRow row)
    {
        if (row == null || debugSimStepIndex < 0 || debugSimStepIndex >= debugSteps.Count || db == null)
            return;

        debugInspectLoaded = true;
        debugInspectError = null;
        debugInspectRows = new();
        debugInspectId = row.Id;
        debugInspectLabel = row.Label;
        debugInspectName = row.Name;

        var query = (queryText ?? "").Trim();
        var step = debugSteps[debugSimStepIndex].Step;
        var remaining = GremlinDebugQueries.RemainingSteps(query, step);

        if (remaining.Count == 0)
        {
            debugInspectError = "No further steps to trace.";
            return;
        }

        var ct = BeginQuery();

        try
        {
            foreach (var r in remaining)
            {
                var q = GremlinDebugQueries.BuildInspectionCount(query, step, row.Id, r);
                var res = await db.ExecuteAsync(q, ct);

                var traced = new InspectStepRow { StepText = r.Text };
                if (res.IsError)
                    traced.IsError = true;
                else
                    traced.Count = ReadCountResult(res.Data);

                debugInspectRows.Add(traced);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }
    }

    //The index of the first traced step where the inspected element drops to 0 (where it dies), or -1.
    private int DebugInspectDeathIndex()
    {
        for (int i = 0; i < debugInspectRows.Count; i++)
            if (debugInspectRows[i].Count == 0)
                return i;

        return -1;
    }

    //Loads the intermediate result at a step into the graph view (2D/3D) for inspection.
    private async Task VisualizeDebugStepAsync(int index)
    {
        if (index < 0 || index >= debugSteps.Count)
            return;

        var step = debugSteps[index];
        var ct = BeginQuery();

        try
        {
            var r = await db.ExecuteAsync(step.Prefix + ".limit(500)", ct);
            if (!r.IsError)
            {
                lastResultData = r.Data;
                graphResultData = default;
                queryResults = r.ToString();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }

        showDebug = false;
        await SetDebugKeysAsync(false);

        if (visualizationMode != 2 && visualizationMode != 3)
            visualizationMode = 2;

        await RenderGraphAsync();
    }

    private async Task SetDebugTabAsync(int tab)
    {
        debugTab = tab;
        await SetDebugKeysAsync(tab == 1);

        if (tab == 2 && !debugProfileLoaded)
            await RunProfileAsync();
        else if (tab == 3 && !debugExplainLoaded)
            await RunExplainAsync();
    }

    private async Task RunProfileAsync()
    {
        debugProfileLoaded = true;
        debugProfileError = null;
        debugProfileRows = new();

        var ct = BeginQuery();

        try
        {
            var debugger = Provider.Debugger;
            var r = await db.ExecuteAsync(debugger.ProfileQuery(queryText ?? ""), ct);

            if (r.IsError)
            {
                debugProfileError = r.Error;
            }
            else
            {
                var (total, rows) = debugger.ParseProfile(r);
                debugProfileTotalMs = total;
                debugProfileRows = rows;

                //Fall back to the raw response if the metrics shape wasn't recognized.
                if (rows.Count == 0)
                    debugProfileError = r.ToString();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }
    }

    private async Task RunExplainAsync()
    {
        debugExplainLoaded = true;

        var ct = BeginQuery();

        try
        {
            var debugger = Provider.Debugger;
            var r = await db.ExecuteAsync(debugger.ExplainQuery(queryText ?? ""), ct);
            debugExplain = debugger.ParseExplain(r);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndQuery();
        }
    }

    //Character ranges of the debuggable steps, for the editor to highlight and make clickable.
    private string DebugStepRangesJson()
    {
        var ranges = debugSteps.Select(d => new[] { d.Step.Start, d.Step.End }).ToList();
        return JsonSerializer.Serialize(ranges);
    }

    private static long? ReadCountResult(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var v = GraphDataConverter.UnwrapElement(data[0]);

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;

        if (long.TryParse(v.ToString(), out var n2))
            return n2;

        return null;
    }
}
