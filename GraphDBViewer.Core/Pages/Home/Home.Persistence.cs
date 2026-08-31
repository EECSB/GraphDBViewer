using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Workspace persistence glue: debounced editor-text saves plus the load/save calls into
//WorkspaceStore (open tabs, last query, saved queries, history). The snapshot conversion and the
//storage keys live in Code/Storage/WorkspaceStore.cs.
public partial class Home
{

    [Inject]
    private WorkspaceStore Workspace { get; set; }

    #region Saved queries
    private Dictionary<string, string> savedQueries = new();
    private string selectedQueryKey;
    #endregion

    #region Query history
    private List<string> queryHistory = new();
    #endregion


    #region Editor text

    private async Task SaveQueryAsync()
    {
        await Workspace.SaveLastQueryAsync(queryText);
        await PersistTabTextAsync();
    }

    private CancellationTokenSource textSaveCts;

    //Mirrors Monaco edits into the active tab immediately (so Run/tab-switch always see the latest text),
    //but persists the editor text only after a short pause — writing localStorage on every keystroke
    //(two interop round-trips + serializing every tab's text) made typing feel sluggish.
    private void OnQueryTextChanged(string text)
    {
        queryText = text;
        ScheduleTextSave();
    }

    //Mirror of OnQueryTextChanged for the Generated tab's Monaco editor. Like the main editor it
    //avoids a Home re-render per keystroke, but the Commit button and the tab's "!" badge depend on
    //whether any generated text remains — so re-render only when that emptiness actually flips.
    private void OnGeneratedTextChanged(string text)
    {
        bool wasEmpty = string.IsNullOrWhiteSpace(generatedQueryText);
        generatedQueryText = text;
        ScheduleTextSave();

        if (wasEmpty != string.IsNullOrWhiteSpace(text))
            StateHasChanged();
    }

    //Debounces the lightweight editor-text persistence so it runs ~half a second after typing stops.
    private void ScheduleTextSave()
    {
        textSaveCts?.Cancel();
        textSaveCts = new CancellationTokenSource();
        _ = SaveTextAfterDelayAsync(textSaveCts.Token);
    }

    private async Task SaveTextAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
            await SaveQueryAsync();
    }

    private async Task LoadQueryAsync()
    {
        //Don't clobber a query the active tab already carries (e.g. restored from a saved tab).
        if (!string.IsNullOrEmpty(queryText))
            return;

        var saved = await Workspace.LoadLastQueryAsync();

        if (!string.IsNullOrEmpty(saved))
            queryText = saved;
    }

    #endregion


    #region Tabs

    //Restores all open query tabs from the previous session. Returns true when something was
    //restored. Closed tabs were dropped on close.
    private async Task<bool> LoadTabsAsync()
    {
        var restored = await Workspace.LoadTabsAsync();

        if (restored == null)
            return false;

        tabs = restored;
        activeTabIndex = 0;

        return true;
    }

    private async Task PersistTabTextAsync()
    {
        await Workspace.SaveTabTextAsync(tabs);
    }

    private async Task SaveTabsAsync()
    {
        await Workspace.SaveTabsAsync(tabs);
    }

    #endregion


    #region History & saved queries

    //Records a run query at the top of the history (de-duplicated, capped).
    private async Task AddToHistoryAsync(string query)
    {
        if (!WorkspaceStore.PushHistory(queryHistory, query))
            return;

        await Workspace.SaveHistoryAsync(queryHistory);
    }

    private async Task LoadHistoryAsync()
    {
        var stored = await Workspace.LoadHistoryAsync();

        if (stored is { Count: > 0 })
            queryHistory = stored;
    }

    private async Task LoadSavedQueriesAsync()
    {
        var stored = await Workspace.LoadSavedQueriesAsync();

        if (stored is { Count: > 0 })
            savedQueries = stored;
    }

    private void OnSavedQuerySelected(ChangeEventArgs e)
    {
        var key = e.Value?.ToString();
        if (string.IsNullOrEmpty(key))
            selectedQueryKey = null;
        else
            selectedQueryKey = key;

        if (selectedQueryKey != null && savedQueries.TryGetValue(selectedQueryKey, out var q))
            queryText = q;
    }

    private async Task AddSavedQueryAsync()
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return;

        string name;
        if (queryText.Length > 40)
            name = queryText[..40] + "…";
        else
            name = queryText;

        name = name.ReplaceLineEndings(" ");

        var baseName = name;
        var i = 1;
        while (savedQueries.ContainsKey(name))
            name = $"{baseName} ({i++})";

        savedQueries[name] = queryText;
        selectedQueryKey = name;

        await Workspace.SaveSavedQueriesAsync(savedQueries);
    }

    private async Task RemoveSavedQueryAsync()
    {
        if (string.IsNullOrEmpty(selectedQueryKey))
            return;

        bool confirmed = await JS.InvokeAsync<bool>("confirm", $"Delete the saved query \"{selectedQueryKey}\"?");
        if (!confirmed)
            return;

        savedQueries.Remove(selectedQueryKey);
        selectedQueryKey = null;

        await Workspace.SaveSavedQueriesAsync(savedQueries);
    }

    #endregion
}
