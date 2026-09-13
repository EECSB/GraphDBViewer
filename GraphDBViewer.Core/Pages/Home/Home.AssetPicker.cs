using System;
using System.Threading.Tasks;
using GraphDBViewerWeb.Components;
using Microsoft.AspNetCore.Components;

namespace GraphDBViewerWeb.Pages;

//The asset picker's hooks (DAM requirement 25): a Browse button at the end of each of the four URL
//fields — the vertex's image and model, the per-label icon and model in the style dialog — opens the
//core's AssetPicker over whatever IAssetSource the host registered, and the picked URL goes to the
//setter the field already had. With no source registered the buttons are not rendered, which is how
//the web-only edition stays as it was.
public partial class Home
{
    [Inject] private IServiceProvider Services { get; set; }

    private bool assetPickerVisible;
    private string assetPickerKind = "any";
    private string assetPickerUrl;
    private Func<string, Task> assetPickerApply;

    ///<summary>Whether a source is registered — the condition for rendering a Browse button.</summary>
    private bool AssetPickerAvailable => Services != null && AssetPicker.IsAvailable(Services);

    private void OpenAssetPicker(string kind, string currentUrl, Func<string, Task> apply)
    {
        assetPickerKind = kind;
        assetPickerUrl = currentUrl;
        assetPickerApply = apply;
        assetPickerVisible = true;
    }

    private async Task OnAssetPickedAsync(string url)
    {
        assetPickerVisible = false;

        if (assetPickerApply != null)
            await assetPickerApply(url);
    }
}
