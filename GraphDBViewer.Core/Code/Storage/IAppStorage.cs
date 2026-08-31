namespace GraphDBViewerWeb.Code;

///<summary>
///Abstraction over where the viewer persists its state — saved connections, open tabs, query
///history and preferences. Keeps the app decoupled from any particular storage mechanism.
///
///The WebAssembly build stores everything in the browser's localStorage
///(<see cref="LocalAppStorage"/>); a later build can drop in a server-backed implementation
///(e.g. a REST API) by registering a different <see cref="IAppStorage"/> in <c>Program.cs</c> —
///no calling code has to change.
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
