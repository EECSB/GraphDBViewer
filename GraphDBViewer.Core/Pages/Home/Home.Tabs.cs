using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//Tab management: adding, inline renaming, switching and closing query tabs, plus the derived
//"Data model" tab built from the loaded graph.
public partial class Home
{

    //Inline tab rename (double-click a tab name).
    private int editingTabIndex = -1;
    private string editingTabName;
    private ElementReference tabNameInput;
    private bool focusTabInput;

    //Adds a fresh query tab (reusing the lowest free "Query N" number) and switches to it.
    private async Task AddTabAsync()
    {
        tabs.Add(new QueryTab { Name = NextTabName() });
        activeTabIndex = tabs.Count - 1;
        await SaveTabsAsync();
        await PersistTabTextAsync();
        await RenderGraphAsync();
    }

    //The lowest positive N whose default name "Query N" isn't already in use, so closing a tab
    //frees its number for the next new tab.
    private string NextTabName()
    {
        int n = 1;
        while (tabs.Any(t => t.Name == $"Query {n}"))
            n++;

        return $"Query {n}";
    }

    //Begins inline editing of a tab's name (triggered by a double-click).
    private void StartRenameTab(int index)
    {
        editingTabIndex = index;
        editingTabName = tabs[index].Name;
        focusTabInput = true;
    }

    private async Task CommitTabNameAsync(int index)
    {
        if (editingTabIndex != index)
            return;

        var name = (editingTabName ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(name))
            tabs[index].Name = name;

        editingTabIndex = -1;
        await SaveTabsAsync();
    }

    private async Task OnTabNameKeyDown(KeyboardEventArgs e, int index)
    {
        if (e.Key == "Enter")
            await CommitTabNameAsync(index);
        else if (e.Key == "Escape")
            editingTabIndex = -1;
    }

    //Switches to another tab and redraws its graph.
    private async Task SwitchTabAsync(int index)
    {
        if (index < 0 || index >= tabs.Count || index == activeTabIndex)
            return;

        activeTabIndex = index;
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //Closes a tab (never the last one) after confirmation, dropping its persisted data, then redraws.
    private async Task CloseTabAsync(int index)
    {
        if (tabs.Count <= 1 || index < 0 || index >= tabs.Count)
            return;

        bool confirmed = await JS.InvokeAsync<bool>("confirm", $"Close tab \"{tabs[index].Name}\"? Its results will be discarded.");
        if (!confirmed)
            return;

        tabs.RemoveAt(index);

        if (activeTabIndex > index)
            activeTabIndex--;
        else if (activeTabIndex >= tabs.Count)
            activeTabIndex = tabs.Count - 1;

        await SaveTabsAsync();
        await PersistTabTextAsync();
        await RenderGraphAsync();
    }

    //Builds a data-model / schema diagram from the loaded graph (any engine, including imported
    //data) and opens it in a new tab, so the current result is preserved.
    private async Task ShowDataModelAsync()
    {
        var data = CurrentGraphData();

        if (data.ValueKind == JsonValueKind.Undefined)
            return;

        var json = GraphDataConverter.BuildSchemaFromData(data);
        var parsed = JsonSerializer.Deserialize<JsonElement>(json);

        var tab = new QueryTab
        {
            Name = "Data model",
            VisualizationMode = 2,
            LastResultData = parsed,
            GraphResultData = parsed,
            QueryResults = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true })
        };

        tabs.Add(tab);
        activeTabIndex = tabs.Count - 1;

        await SaveTabsAsync();
        await PersistTabTextAsync();
        await RenderGraphAsync();
    }
}
