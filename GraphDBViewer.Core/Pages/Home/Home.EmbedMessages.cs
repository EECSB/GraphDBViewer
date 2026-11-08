using Microsoft.JSInterop;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Pages;

//The embedded viewer's messages with the page that embeds it (2026-10-23, DESIGN.md's embeddable viewer): a query to run,
//a view and a theme from the page, and back to it the query's result and the selection whenever it changes. Off unless
//the viewer is the embedded one and its address names the page's origin (EmbedSettings.Origin). The browser's half,
//EmbedMessagesInterop.js, holds every message to the parent window and that origin, reads it, and posts the answers;
//this half does what a message asks, with nothing in it run as code and nothing that edits the graph.
public partial class Home
{
    //Whether the page around the embedded viewer was given the messages: what the browser's half said when asked.
    private bool embedMessagesOn;

    //The selection the page was last told of, as EmbedSelection.KeyOf writes it: told again only when that changes.
    private string embedSelectionTold = EmbedSelection.KeyOf(null);

    ///<summary>
    ///Gives the page that embeds the viewer its messages, when the address names that page's origin, and so says ready
    ///to it. Run after the address's own settings, so a query the page sends on ready finds the graph the address
    ///named opened, and its own query run.
    ///</summary>
    private async Task StartEmbedMessagesAsync()
    {
        if (!Embedded)
            return;

        string origin;

        try
        {
            origin = EmbedSettings.Parse(new Uri(Nav.Uri).Query).Origin;
        }
        catch
        {
            return;
        }

        if (origin == null)
            return;

        try
        {
            embedMessagesOn = await JS.InvokeAsync<bool>("embedMessagesInterop.attach", SelfRef(), origin);
        }
        catch { }
    }

    ///<summary>
    ///A query the page around the viewer sent (<c>treeality:query</c>). Run the way the address's own query is, by
    ///RunQueryAsync, so it is drawn, and on the instance graph goes to the host with the token the page passed, where a
    ///write is refused whoever sends it. It reads and nothing else: one the engine's own guard reads as a write is not
    ///run here either, as Ask AI's read tool runs none. One at a time: one sent while another runs is refused, not queued.
    ///</summary>
    [JSInvokable]
    public async Task<EmbedQueryAnswer> OnEmbedQuery(string query)
    {
        if (!embedMessagesOn)
            return EmbedQueryAnswer.Refused(EmbedQueryAnswer.Off);

        if (string.IsNullOrWhiteSpace(query))
            return EmbedQueryAnswer.Refused(EmbedQueryAnswer.Blank);

        //What the viewer says on its own page while it has nothing to show, which is why: no graph of the host's, a token
        //that may not read it, an address that names none.
        if (db == null || !isConnected)
            return EmbedQueryAnswer.Refused(EmbeddedEmptySays);

        if (isQuerying)
            return EmbedQueryAnswer.Refused(EmbedQueryAnswer.Busy);

        if (Qb != null && Qb.IsMutating(query))
            return EmbedQueryAnswer.Refused(EmbedQueryAnswer.Edits);

        queryText = query;
        await RunQueryAsync();
        StateHasChanged();

        //A result the viewer cannot read drew nothing, and says why on the page; the page around it is told the same.
        var problem = ResultProblem();

        if (string.IsNullOrEmpty(queryError) && problem != null)
            return EmbedQueryAnswer.Refused(EmbedQueryAnswer.Unreadable(problem));

        return EmbedQueryAnswer.After(queryError, tableResult != null, CurrentGraphData());
    }

    ///<summary>A view the page asked for (<c>treeality:view</c>), by the names the address's view takes: one it does not know changes nothing.</summary>
    [JSInvokable]
    public async Task OnEmbedView(string view)
    {
        var mode = EmbedSettings.ParseView(view);

        if (!embedMessagesOn || mode == null)
            return;

        await SetVisualizationMode(mode.Value);
        StateHasChanged();
    }

    ///<summary>A theme the page asked for (<c>treeality:theme</c>), by the names the address's theme takes: one it does not know changes nothing.</summary>
    [JSInvokable]
    public async Task OnEmbedTheme(string theme)
    {
        var chosen = EmbedSettings.NormalizeTheme(theme);

        if (!embedMessagesOn || chosen == null)
            return;

        await Shell.SetThemeAsync(chosen);
    }

    ///<summary>
    ///Tells the page what is selected when that is not what it was last told (<c>treeality:selection</c>). Run after
    ///every render rather than where the selection is set: a click in any view, a delete, a redraw and a change of tab all
    ///change it, and each of them renders.
    ///</summary>
    private async Task TellEmbedSelectionAsync()
    {
        if (!embedMessagesOn)
            return;

        var selection = EmbedSelection.Of(selectedElement);
        var key = EmbedSelection.KeyOf(selection);

        if (key == embedSelectionTold)
            return;

        embedSelectionTold = key;

        try
        {
            await JS.InvokeVoidAsync("embedMessagesInterop.selection", selection);
        }
        catch { }
    }
}
