using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Where the viewer, and any plugin, asks for assets without knowing who keeps them (DAM requirement
///24). A plugin that keeps files registers one of these in its ConfigureServices; the picker in the
///core resolves it and is inert without one, which is how the web-only edition stays as it was.
///</summary>
public interface IAssetSource
{
    ///<summary>What the picker calls the source — "Digital Asset Manager".</summary>
    string Name { get; }

    ///<summary>The subfolders of a folder and the assets it holds, filtered and paged.</summary>
    Task<AssetBrowseResult> BrowseAsync(AssetBrowseRequest request, CancellationToken cancellationToken);

    ///<summary>What a URL already in a field is: not ours, gone, found, or the source could not say.</summary>
    Task<AssetLookup> LookupAsync(string url, CancellationToken cancellationToken);
}

public sealed class AssetBrowseRequest
{
    ///<summary>The folder to browse — null for everything, "" for the root, else a path such as "cad/enclosures".</summary>
    public string Folder { get; set; }

    ///<summary>Whether the assets of the folder's subtree count too.</summary>
    public bool Recursive { get; set; }

    ///<summary>A substring of the name or the media type.</summary>
    public string Filter { get; set; }

    public string Tag { get; set; }

    ///<summary>image, model, or any.</summary>
    public string Kind { get; set; } = "any";

    public int Offset { get; set; }

    public int Limit { get; set; } = 60;
}

public sealed class AssetBrowseResult
{
    ///<summary>The whole visible folder tree, flattened, each with its depth — the picker draws it as a tree.</summary>
    public List<AssetFolderInfo> Folders { get; set; } = new();

    public List<AssetInfo> Items { get; set; } = new();

    public int Total { get; set; }

    ///<summary>The tags in use, for the filter.</summary>
    public List<string> Tags { get; set; } = new();

    ///<summary>Why nothing came back, when the source could not answer.</summary>
    public string Error { get; set; }
}

public sealed class AssetFolderInfo
{
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public int Depth { get; set; }

    ///<summary>Assets directly inside.</summary>
    public int Count { get; set; }
}

public sealed class AssetInfo
{
    public string Url { get; set; }

    public string Name { get; set; }

    public string MediaType { get; set; }

    public long Size { get; set; }

    public string Folder { get; set; } = "";

    public List<string> Tags { get; set; } = new();

    public bool IsImage => (MediaType ?? "").StartsWith("image/");

    public bool IsModel => (MediaType ?? "").StartsWith("model/") || MediaType == "application/zip";
}

public enum AssetLookupState
{
    ///<summary>The URL is not one of the source's — an external link, or empty.</summary>
    NotOurs,

    ///<summary>The URL is the source's, but the asset is gone.</summary>
    Missing,

    Found,

    ///<summary>The source could not answer; the message says why.</summary>
    Unavailable
}

public sealed class AssetLookup
{
    public AssetLookupState State { get; set; }

    public AssetInfo Item { get; set; }

    public string Message { get; set; }
}
