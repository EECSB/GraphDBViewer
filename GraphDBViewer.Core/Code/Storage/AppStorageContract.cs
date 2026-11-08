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
    ///<summary>The address of every key's value, which names its key in the query string (<see cref="PathFor"/>).</summary>
    public const string BasePath = "api/storage";

    ///<summary>The query string's name for the key.</summary>
    public const string KeyParameter = "key";

    ///<summary>
    ///The full relative address for one key: the key in the query string, where it is decoded once and exactly, its
    ///<c>graphdbviewer:</c> colon and anything else. Until 2026-09-29 it was a catch-all segment of the path, where
    ///routing leaves an escaped "/" escaped so that it cannot split the path, which makes a key's own "%2F" the same
    ///text as a "/": two keys that differed only so were one key to the host. The host reads the query string alone.
    ///</summary>
    public static string PathFor(string key)
    {
        return $"{BasePath}?{KeyParameter}={Uri.EscapeDataString(key)}";
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

    ///<summary>
    ///The account a request is for, named by the client that booted as it. The host refuses a request
    ///whose session is somebody else's, so a tab still open as one account cannot write its workspace into
    ///whoever signed in after it in another tab. Absent when the client could not learn who it is, and
    ///then the session alone decides.
    ///</summary>
    public const string AccountHeader = "X-Gdbv-Account";
}
