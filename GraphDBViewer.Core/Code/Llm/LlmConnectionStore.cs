namespace GraphDBViewerWeb.Code;

///<summary>
///Persists the saved AI-model connections behind <see cref="IAppStorage"/>. Owns the
///graphdbviewer:llmConnections key, lifted out of NlQueryModal so every AI feature ("Ask AI" today,
///knowledge-graph generation next) shares one store and one config UI instead of growing a second
///copy of the CRUD. The stored shape is unchanged from when the modal owned the key, so existing
///saved models load with no migration.
///</summary>
public class LlmConnectionStore
{
    private const string StorageKey = "graphdbviewer:llmConnections";

    private readonly IAppStorage _storage;

    public LlmConnectionStore(IAppStorage storage)
    {
        _storage = storage;
    }

    ///<summary>The saved connections by name. Never null — no models yet reads as an empty dictionary.</summary>
    public async Task<Dictionary<string, LlmConnection>> LoadAsync()
    {
        var stored = await _storage.GetAsync<Dictionary<string, LlmConnection>>(StorageKey);

        if (stored == null)
            return new Dictionary<string, LlmConnection>();

        return stored;
    }

    ///<summary>
    ///Raised after the saved set changes, so anything showing it can show it again.
    ///
    ///Needed because the list is edited in one place and used in several. A picker loads once, when it is
    ///created, and every AI panel is created with the page and merely hidden afterwards — so without
    ///this, a model added under Settings stays invisible to the Ask AI panel until a reload, which looks
    ///exactly like the model not having been saved.
    ///</summary>
    public event Func<Task> Changed;

    public async Task SaveAsync(Dictionary<string, LlmConnection> connections)
    {
        await _storage.SetAsync(StorageKey, connections);

        var handler = Changed;

        if (handler == null)
            return;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();
    }
}
