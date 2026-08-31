namespace GraphDBViewerWeb.Code;

///<summary>
///The wire shape of the server-backed store, named once so the two halves cannot drift apart — the host
///builds its routes from these constants the same way <c>GraphApi</c> builds its own from
///<see cref="ServerProxyGraphDb.ExecutePath"/>.
///
///Deliberately blob-by-key rather than domain endpoints: it is exactly what <see cref="IAppStorage"/>
///already is, so the adapter stays a translation with no logic of its own, and the eventual table is a
///plain <c>(user, key, value)</c>. Anything wanting a richer server-side shape should grow beside this,
///not bend it.
///</summary>
public static class AppStorageContract
{
    ///<summary>
    ///Base path for a single key's value. The key is appended as a catch-all segment, so it may contain
    ///the <c>graphdbviewer:</c> colon without any escaping games.
    ///</summary>
    public const string BasePath = "api/storage";

    ///<summary>The full relative path for one key.</summary>
    public static string PathFor(string key)
    {
        return $"{BasePath}/{Uri.EscapeDataString(key)}";
    }

    ///<summary>
    ///Values cross the wire packed by <see cref="StorageCompression"/>, and the server stores them that
    ///way — opaque. That keeps the workspace blob (tabs, with their results) an affordable payload, and it
    ///means the eventual database column holds one string per key with nothing to interpret.
    ///</summary>
    public const string ContentType = "text/plain";

    ///<summary>
    ///What the endpoint accepts for one value. A saved workspace with several tabs of results is genuinely
    ///large even deflated, and the framework's default cap would reject it well before the store minded.
    ///</summary>
    public const long MaxValueBytes = 64L * 1024 * 1024;
}
