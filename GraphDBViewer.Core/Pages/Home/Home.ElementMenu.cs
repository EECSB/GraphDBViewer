using Microsoft.JSInterop;

namespace GraphDBViewerWeb.Pages;

//The menu a right-click on a vertex or an edge opens where the pointer is, on the 2D canvas or in 3D (2026-11-04, the
//user): Expand loads a vertex's neighbors as a double-click does, and Delete stages its drop as the element card's X does.
//Show / Hide properties is a 2D vertex's +/- button (2026-11-06, the user).
public partial class Home
{
    private ElementMenu elementMenu;

    //Selection is what a click on it would send (OnElementSelected's JSON), for Style, which works on the selected one. Props
    //is whether its properties are shown on the 2D canvas, "shown" or "hidden", and null where it has no +/- button.
    private sealed record ElementMenu(string Type, string Id, string Label, double X, double Y, string Selection, string Props);

    //Expand needs a database that can go from an id to its neighbors, as the double-click does (OnNodeExpand).
    private bool ElementMenuCanExpand => Caps.Traverse && db != null && isConnected;

    //Called from both canvases' right-click, with the pointer's place in the window and what a click on it would send.
    [JSInvokable]
    public void OnElementContextMenu(string type, string id, string label, double x, double y, string selection, string props)
    {
        if (string.IsNullOrEmpty(id))
            return;

        elementMenu = new ElementMenu(type, id, label, x, y, selection, props);
        StateHasChanged();
    }

    //Style opens the Style dialog on this one, as its card's palette does (2026-11-04, the user): it is selected first,
    //since that is what the dialog styles.
    private async Task StyleFromElementMenuAsync()
    {
        var menu = elementMenu;

        elementMenu = null;

        if (menu == null || string.IsNullOrEmpty(menu.Selection))
            return;

        using var doc = System.Text.Json.JsonDocument.Parse(menu.Selection);

        await SelectAsync(doc.RootElement, menu.Type, menu.Id);
        OpenStyleForSelected();
    }

    ///<summary>A right-click's menu on the canvas's empty space, or null. GraphX and GraphY are where a new component goes; null in 3D, whose force layout places it.</summary>
    private CanvasMenu canvasMenu;

    private sealed record CanvasMenu(double X, double Y, double? GraphX, double? GraphY);

    [JSInvokable]
    public void OnCanvasContextMenu(double x, double y, double? graphX, double? graphY)
    {
        elementMenu = null;
        canvasMenu = new CanvasMenu(x, y, graphX, graphY);

        StateHasChanged();
    }

    private void CloseCanvasMenu()
    {
        canvasMenu = null;
    }

    private string CanvasMenuPlace()
    {
        return FormattableString.Invariant($"left: {canvasMenu.X}px; top: {canvasMenu.Y}px;");
    }

    private async Task NewComponentFromCanvasMenuAsync()
    {
        var menu = canvasMenu;

        canvasMenu = null;

        if (menu != null)
            await PlaceComponentAsync(menu.GraphX, menu.GraphY);
    }

    private async Task ClearFromCanvasMenuAsync()
    {
        canvasMenu = null;

        await ClearResultsAsync();
    }

    private void CloseElementMenu()
    {
        elementMenu = null;
    }

    private async Task TogglePropsFromElementMenuAsync()
    {
        var menu = elementMenu;

        elementMenu = null;

        if (menu == null || menu.Props == null)
            return;

        try
        {
            await JS.InvokeVoidAsync("toggleCytoscapeNodeProps", menu.Id);
        }
        catch { }
    }

    private async Task ExpandFromElementMenuAsync()
    {
        var menu = elementMenu;

        elementMenu = null;

        if (menu != null && menu.Type == "node")
            await OnNodeExpand(menu.Id);
    }

    //The drop is staged on the Generated tab, which comes to the front, as the card's X does; the card closes when it
    //was showing the element just staged.
    private void DeleteFromElementMenu()
    {
        var menu = elementMenu;

        elementMenu = null;

        if (menu == null || !EditsAllowed)
            return;

        StageElementDrop(menu.Type, menu.Id);
        queryEditorTab = 2;

        if (selectedElement != null && selectedElement.Id == menu.Id)
        {
            selectedElement = null;
            hasPropertyChanges = false;
            commitStatus = null;
        }
    }

    //Where the menu stands: at the pointer, written with a point whatever the culture, since it is CSS.
    private string ElementMenuPlace()
    {
        return FormattableString.Invariant($"left: {elementMenu.X}px; top: {elementMenu.Y}px;");
    }
}
