using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;
using GraphDBViewerWeb.Components;

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

    //A query, a load, a commit or a connect writes its answer into the tab and the connection it started in, wherever the
    //person has gone meanwhile, so the tabs and connections switch freely while one runs (the multiple-connections spec,
    //story 4, 2026-11-07; from 2026-11-03 they waited for it, since until then a query tab switched to mid-run took the
    //other one's answer). Only the debugger still holds them: its steps are the page's, not a tab's.
    private bool SwitchHeld => frontRunHeld;

    //Adds a fresh query tab (reusing the lowest free "Query N" number) and switches to it.
    private async Task AddTabAsync()
    {
        if (SwitchHeld)
            return;

        CloseSetupTabs();
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

    //A new query tab for any connection: it comes in front with its connection (the multiple-connections spec, task 4).
    private async Task AddTabAsync(int connectionIndex)
    {
        if (SwitchHeld || connectionIndex < 0 || connectionIndex >= openConnections.Count)
            return;

        BringConnectionInFront(connectionIndex);
        await AddTabAsync();
    }

    //Begins inline editing of a tab's name (triggered by a double-click), only of the connection in front's: the click
    //before the double-click brought a tab of another one in front.
    private void StartRenameTab(int connectionIndex, int index)
    {
        if (connectionIndex != activeConnectionIndex)
            return;

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
        if (SwitchHeld)
            return;

        CloseSetupTabs();

        if (index < 0 || index >= tabs.Count || index == activeTabIndex)
            return;

        activeTabIndex = index;
        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //Switches to a query tab of any connection, bringing its connection in front with it (the multiple-connections spec,
    //task 4): the page then shows that connection's graph and state.
    private async Task SwitchTabAsync(int connectionIndex, int index)
    {
        if (connectionIndex == activeConnectionIndex)
        {
            await SwitchTabAsync(index);
            return;
        }

        if (SwitchHeld || connectionIndex < 0 || connectionIndex >= openConnections.Count)
            return;

        CloseSetupTabs();
        BringConnectionInFront(connectionIndex);

        if (index >= 0 && index < tabs.Count)
            activeTabIndex = index;

        await SaveTabsAsync();
        await RenderGraphAsync();
    }

    //Closes a tab of any connection (never its last one) after confirmation, dropping its persisted data, then redraws.
    private async Task CloseTabAsync(int connectionIndex, int index)
    {
        if (SwitchHeld || connectionIndex < 0 || connectionIndex >= openConnections.Count)
            return;

        var owner = openConnections[connectionIndex];

        if (owner.Tabs.Count <= 1 || index < 0 || index >= owner.Tabs.Count)
            return;

        bool confirmed = await JS.InvokeAsync<bool>("confirm", $"Close tab \"{owner.Tabs[index].Name}\"? Its results will be discarded.");
        if (!confirmed)
            return;

        owner.Tabs.RemoveAt(index);

        if (owner.ActiveTabIndex > index)
            owner.ActiveTabIndex--;
        else if (owner.ActiveTabIndex >= owner.Tabs.Count)
            owner.ActiveTabIndex = owner.Tabs.Count - 1;

        await SaveTabsAsync();
        await PersistTabTextAsync();
        await RenderGraphAsync();
    }

    //Brings a connection in front: the page's fields read it from then on. A rename under way was the one in front's.
    private void BringConnectionInFront(int connectionIndex)
    {
        if (connectionIndex == activeConnectionIndex)
            return;

        editingTabIndex = -1;
        activeConnectionIndex = connectionIndex;
    }

    //A connection's tab: its connection comes in front with its panel open over the graph's card. The one in front's only
    //comes to the front, as before.
    private async Task ShowConnectionAsync(int connectionIndex)
    {
        if (connectionIndex != activeConnectionIndex)
        {
            if (SwitchHeld || connectionIndex < 0 || connectionIndex >= openConnections.Count)
                return;

            BringConnectionInFront(connectionIndex);
            OpenConnectionCard();
            await SaveTabsAsync();
            await RenderGraphAsync();
        }
        else
            ToggleConnectionCard();
    }

    //The add-connection button (the multiple-connections spec, task 4): a new connection comes in front, its empty form
    //open over the graph's card, with one query tab of its own.
    private async Task AddConnectionAsync()
    {
        if (SwitchHeld)
            return;

        openConnections.Add(new ViewerConnection());
        BringConnectionInFront(openConnections.Count - 1);
        OpenConnectionCard();
        await SaveTabsAsync();
        await PersistTabTextAsync();
    }

    //The × on a connection's tab, there only while there are several, as the last query tab has none (the
    //multiple-connections spec, task 5).
    private EventCallback CloseConnectionCallback(int connectionIndex)
    {
        if (openConnections.Count > 1)
            return EventCallback.Factory.Create(this, () => CloseConnectionAsync(connectionIndex));

        return default;
    }

    //Closes a connection after asking (never the last one): its query tabs go with it and its live link closes. When it
    //was in front, the one after it comes in front, as a closed query tab's neighbor does.
    private async Task CloseConnectionAsync(int connectionIndex)
    {
        if (SwitchHeld || openConnections.Count <= 1 || connectionIndex < 0 || connectionIndex >= openConnections.Count)
            return;

        var owner = openConnections[connectionIndex];
        var provider = GraphDbProviders.For(owner.Connection.DatabaseType);
        var name = ViewerToolbar.HostText(LabelFor(owner), provider.DisplayHost(owner.Connection), provider.DisplayPort(owner.Connection));
        var tabsText = $"{owner.Tabs.Count} query tabs";

        if (owner.Tabs.Count == 1)
            tabsText = "query tab";

        bool confirmed = await JS.InvokeAsync<bool>("confirm", $"Close connection {name} and its {tabsText}? Their results will be discarded.");
        if (!confirmed)
            return;

        if (connectionIndex == activeConnectionIndex)
            CloseSetupTabs();

        openConnections.RemoveAt(connectionIndex);

        if (activeConnectionIndex > connectionIndex)
            activeConnectionIndex--;
        else if (activeConnectionIndex >= openConnections.Count)
            activeConnectionIndex = openConnections.Count - 1;

        await SaveTabsAsync();
        await PersistTabTextAsync();
        await RenderGraphAsync();

        try
        {
            await owner.CloseAsync();
        }
        catch { }
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
