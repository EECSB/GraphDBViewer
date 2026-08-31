using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The form that stands in for the query editor when the engine has no query language — GUN, so far.
//
//There is nothing to type against GUN: it is a chained JavaScript API, gun.get('alice').map().once(…),
//not a language. So the Query tab shows three controls instead of an editor, and the Generated tab shows
//the JavaScript those controls amount to, read-only. The query text is still the single source of truth
//underneath — the form parses it and writes it back — so saved queries, history and the tab's persistence
//keep working unchanged.
public partial class Home
{
    //Parsed on demand rather than held in fields, so a saved query or a history entry dropped into the
    //editor text shows up in the form without anything having to push it there.
    private GunQuery GunForm => GunQuery.Parse(queryText);

    ///<summary>The key path the read starts from — <c>alice</c>, or <c>users/alice</c> to follow a link.</summary>
    private string GunStartPath
    {
        get
        {
            return string.Join("/", GunForm.Keys);
        }
        set
        {
            WriteGunQuery(GunWith(startPath: value));
        }
    }

    private bool GunMapChildren
    {
        get
        {
            return GunForm.MapChildren;
        }
        set
        {
            WriteGunQuery(GunWith(map: value));
        }
    }

    private int GunDepth
    {
        get
        {
            return GunForm.Depth;
        }
        set
        {
            WriteGunQuery(GunWith(depth: value));
        }
    }

    //Rebuilds the query from the current form, with one field replaced.
    private GunQuery GunWith(string startPath = null, bool? map = null, int? depth = null)
    {
        var current = GunForm;

        var keys = current.Keys;
        if (startPath != null)
            keys = startPath.Split('/', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        return new GunQuery
        {
            Keys = keys,
            MapChildren = map ?? current.MapChildren,
            Depth = depth ?? current.Depth
        };
    }

    private void WriteGunQuery(GunQuery query)
    {
        queryText = query.ToQueryString();
        ScheduleTextSave();
    }

    ///<summary>The GUN JavaScript the form describes — what the Generated tab shows in place of staged edits.</summary>
    private string GunJavaScript => GunForm.ToJavaScript();

    ///<summary>
    ///Drops query text this engine's form could not have written. A tab keeps its text across connections,
    ///so switching to GUN can leave a Gremlin query sitting where a key belongs — which the form would then
    ///show as a start key, and Run would read as one.
    ///</summary>
    private void NormalizeFormQueryText(string example)
    {
        if (!GunQuery.IsWireString(queryText))
            queryText = example;
    }

    //Runs the read the form describes. Separate from the Run button's handler only in that the form's
    //Enter key lands here too, which is what anyone typing a key expects.
    private async Task RunGunQueryAsync()
    {
        if (isQuerying)
            return;

        await RunQueryAsync();
    }

    //── Live updates ────────────────────────────────────────────────────

    ///<summary>
    ///Whether the canvas is following the database rather than showing one moment of it. Off by default:
    ///a subscription that keeps redrawing is not what someone reading a graph always wants.
    ///</summary>
    private bool liveUpdates;

    ///<summary>Live only means anything where the engine pushes; everywhere else a result is final.</summary>
    private ILiveGraphDb LiveDb
    {
        get
        {
            if (Caps.LiveUpdates && isConnected)
                return db as ILiveGraphDb;

            return null;
        }
    }

    private async Task ToggleLiveAsync()
    {
        liveUpdates = !liveUpdates;

        if (liveUpdates)
            await StartLiveAsync();
        else
            await StopLiveAsync();
    }

    ///<summary>
    ///Subscribes to whatever the current read reaches. Called again after each run, so the subscription
    ///follows what is on screen rather than what was on screen when Live was switched on.
    ///</summary>
    private async Task StartLiveAsync()
    {
        var live = LiveDb;

        if (live == null || !liveUpdates)
            return;

        live.GraphChanged -= OnLiveGraphChanged;
        live.GraphChanged += OnLiveGraphChanged;

        try
        {
            await live.WatchAsync(queryText ?? "");
        }
        catch (Exception ex)
        {
            queryError = ex.Message;
            liveUpdates = false;
        }
    }

    private async Task StopLiveAsync()
    {
        var live = db as ILiveGraphDb;

        if (live == null)
            return;

        live.GraphChanged -= OnLiveGraphChanged;

        await live.StopWatchingAsync();
    }

    //Arrives from the driver's subscription, off the render loop — hence the InvokeAsync inside.
    private void OnLiveGraphChanged(GraphDbResult result)
    {
        _ = ApplyLiveChangeAsync(result);
    }

    private async Task ApplyLiveChangeAsync(GraphDbResult result)
    {
        if (result.IsError || result.Data.ValueKind != JsonValueKind.Array || result.Data.GetArrayLength() == 0)
            return;

        await InvokeAsync(async () =>
        {
            var current = CurrentGraphData();

            //A push is later than what is drawn, so it replaces those elements rather than being weighed
            //against them — a property the peer removed has to disappear here too.
            if (current.ValueKind == JsonValueKind.Undefined)
            {
                graphResultData = result.Data;
            }
            else
            {
                //A pushed node's links are all of its links — GUN keeps them as keys, and a key holds one
                //link — so an edge leaving it that the push does not carry is one the peer replaced.
                var pushed = new HashSet<string>(GraphDataConverter.ToTable(result.Data).Nodes.Select(n => n.Id));

                graphResultData = GraphDataConverter.MergeGraphResults(GraphDataConverter.WithoutEdgesFrom(current, pushed), result.Data, true);
            }

            //Reconciled rather than added to: a live update can take an element away — a peer re-pointing a
            //link leaves the old edge with nothing behind it — and the add-only path has no way to say so.
            if (visualizationMode == 2)
                await JS.InvokeVoidAsync("syncCytoscapeGraph", GraphDataConverter.ToCytoscapeJson(graphResultData, labelStyles, edgeColorMode, edgeColors));
            else if (visualizationMode == 3)
                await JS.InvokeVoidAsync("graph3DInterop.syncData", GraphDataConverter.ToForceGraphJson(graphResultData, labelStyles, edgeColorMode, edgeColors));
            else
                await RenderGraphAsync();

            StateHasChanged();
        });
    }

    //Copies the JavaScript preview, reusing the Generated tab's transient "copied" feedback.
    private async Task CopyGunJavaScriptAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", GunJavaScript);
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
}
