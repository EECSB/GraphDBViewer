namespace GraphDBViewerWeb.Code;

///<summary>
///Abstraction over where the viewer persists its state — saved connections, open tabs, query
///history and preferences. Keeps the app decoupled from any particular storage mechanism.
///
///The web-only build keeps everything in the browser's IndexedDB (<see cref="IndexedDbAppStorage"/>); Treeality's
///client keeps it on the host for the signed-in account (<see cref="RemoteAppStorage"/>), mirrored to IndexedDB
///through <see cref="FallbackAppStorage"/> so it works offline, and the embedded viewer keeps nothing
///(<see cref="MemoryAppStorage"/>). Each <c>Program.cs</c> chooses; no calling code has to change.
///</summary>
public interface IAppStorage
{
    ///<summary>Reads a JSON-serialized value, or <c>default(T)</c> when the key is absent.</summary>
    Task<T> GetAsync<T>(string key);

    ///<summary>Writes a value as JSON under the given key.</summary>
    Task SetAsync<T>(string key, T value);

    ///<summary>Reads a raw (unserialized) string value, or <c>null</c> when the key is absent.</summary>
    Task<string> GetStringAsync(string key);

    ///<summary>Writes a raw (unserialized) string value under the given key.</summary>
    Task SetStringAsync(string key, string value);

    ///<summary>Removes a key and its value. A no-op when the key is absent.</summary>
    Task RemoveAsync(string key);

    ///<summary>
    ///Raised when a write is rejected because the browser's storage quota is full. The UI subscribes so it
    ///can warn the user their changes are no longer being saved, instead of the QuotaExceededError bubbling
    ///up and crashing the component mid-render.
    ///</summary>
    event Action StorageQuotaExceeded;
}
