namespace GraphDBViewerWeb.Code;

///<summary>
///A preview a plugin offers for files of some kinds, which a page that shows assets (the DAM's) renders without
///knowing who draws it, the way the picker finds an <see cref="IAssetSource"/>. A plugin registers one in its
///ConfigureServices, so a disabled plugin offers none and the page falls back to its own. The component is
///given the asset's <c>Url</c>, file <c>Name</c> and <c>MediaType</c> as string parameters.
///</summary>
public interface IAssetPreview
{
    ///<summary>
    ///Whether this draws a file of this media type and name. The name counts here, unlike in the store's own
    ///sniffing: formats such as STEP carry no signature a store can rely on.
    ///</summary>
    bool Previews(string mediaType, string fileName);

    ///<summary>The component the page renders, with the parameters above.</summary>
    Type Component { get; }
}

public static class AssetPreviews
{
    ///<summary>The first preview registered that draws this file, or null when the page's own preview is the one.</summary>
    public static IAssetPreview? For(IEnumerable<IAssetPreview> previews, string mediaType, string fileName)
    {
        return previews.FirstOrDefault(preview => preview.Previews(mediaType ?? "", fileName ?? ""));
    }

    ///<summary>The file name a URL ends in, unescaped and without its query or fragment: <c>house.ifc</c>, or empty when it names none.</summary>
    public static string NameInUrl(string? url)
    {
        var path = url ?? "";
        var end = path.IndexOfAny(['?', '#']);

        if (end >= 0)
            path = path[..end];

        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    ///<summary>The parameters the component is rendered with.</summary>
    public static Dictionary<string, object> Parameters(string url, string fileName, string mediaType)
    {
        return new Dictionary<string, object>
        {
            ["Url"] = url ?? "",
            ["Name"] = fileName ?? "",
            ["MediaType"] = mediaType ?? ""
        };
    }
}
