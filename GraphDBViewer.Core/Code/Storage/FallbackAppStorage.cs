using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///An <see cref="IAppStorage"/> made of two: the server is the truth, the browser is the copy that keeps
///you working when the server is not there.
///
///Reads prefer the server and fall back to the local store. Writes go to **both** — local first, so the
///copy is never behind — and a write the server refused marks its key pending; pending keys are pushed the
///next time the server answers. A key with something pending reads locally even when the server is up,
///because the local copy is the newer one until it has been pushed.
///
///The conflict story is last-write-wins per key, and that is a deliberate fit rather than a shortcut: this
///is one person's own workspace, so the two sides diverge only when the same user edits the same key from
///two places while one of them is offline — rare, and the loss is bounded to one key's last edit. A
///version-vector or an operation log would buy correctness nobody here is paying for. Revisit if accounts
///ever mean several people editing one workspace.
///</summary>
public class FallbackAppStorage : IAppStorage
{
    ///<summary>
    ///Where the pending set lives, in the local store. Persisted so that closing the browser while offline
    ///does not silently strand the writes made in that session.
    ///</summary>
    public const string PendingKey = "graphdbviewer:__pendingSync";

    private readonly IAppStorage _primary;
    private readonly IAppStorage _backup;

    //key → true when what is pending is a delete rather than a write. Loaded once, then kept in memory and
    //persisted only when it changes — which, while the server is reachable, is never.
    private Dictionary<string, bool> _pending;
    private bool _flushing;

    public FallbackAppStorage(IAppStorage primary, IAppStorage backup)
    {
        _primary = primary;
        _backup = backup;

        //A quota belongs to the browser, so the warning comes from the local half.
        _backup.StorageQuotaExceeded += () => StorageQuotaExceeded?.Invoke();
    }

    public event Action StorageQuotaExceeded;

    ///<summary>
    ///Raised when the server's reachability changes, so the UI can say "working offline — your changes are
    ///saved here and will sync" rather than leaving the user to guess.
    ///</summary>
    public event Action ServerReachabilityChanged;

    ///<summary>Whether the last attempt to reach the server succeeded. True until proven otherwise.</summary>
    public bool IsServerReachable { get; private set; } = true;

    ///<summary>How many keys are written locally but not yet on the server.</summary>
    public int PendingCount
    {
        get
        {
            if (_pending == null)
                return 0;

            return _pending.Count;
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var json = await GetStringAsync(key);

        if (string.IsNullOrEmpty(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        await SetStringAsync(key, JsonSerializer.Serialize(value));
    }

    public async Task<string> GetStringAsync(string key)
    {
        await EnsurePendingLoadedAsync();

        //A backlog is cleared before it is read around, and this is also the only thing that notices the
        //server came back during a session that never writes — a reload straight into offline reads would
        //otherwise stay offline forever. It costs one failed call while the server really is gone, because
        //the push stops at its first refusal rather than retrying every key.
        if (_pending.Count > 0)
            await FlushPendingAsync();

        //Still pending means the local copy is ahead of the server's, so asking the server would hand back
        //the value this device has already replaced.
        if (_pending.ContainsKey(key))
            return await _backup.GetStringAsync(key);

        try
        {
            var value = await _primary.GetStringAsync(key);

            MarkReachable();

            if (value != null)
                return value;

            return await AdoptLocalAsync(key);
        }
        catch
        {
            MarkUnreachable();

            return await _backup.GetStringAsync(key);
        }
    }

    ///<summary>
    ///The server has nothing under this key. If the browser does, that is a workspace which predates the
    ///server — someone upgrading from a build that only ever saved locally, or signing in on the machine
    ///they had been working on — and answering "nothing" would show them an empty app and then save that
    ///emptiness over what they had.
    ///
    ///So local wins when the server is silent, and is pushed up so the next read finds it there. It cannot
    ///resurrect a deletion: a delete removes the local copy too, so there is nothing left to adopt.
    ///</summary>
    private async Task<string> AdoptLocalAsync(string key)
    {
        var local = await _backup.GetStringAsync(key);

        if (local == null)
            return null;

        try
        {
            await _primary.SetStringAsync(key, local);
        }
        catch
        {
            MarkUnreachable();
        }

        return local;
    }

    public async Task SetStringAsync(string key, string value)
    {
        await EnsurePendingLoadedAsync();

        //Local first and unconditionally: the copy must never be the one that is behind.
        await _backup.SetStringAsync(key, value);

        try
        {
            await _primary.SetStringAsync(key, value);

            await ClearPendingAsync(key);
            MarkReachable();

            await FlushPendingAsync();
        }
        catch
        {
            await AddPendingAsync(key, isDelete: false);
            MarkUnreachable();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await EnsurePendingLoadedAsync();

        await _backup.RemoveAsync(key);

        try
        {
            await _primary.RemoveAsync(key);

            await ClearPendingAsync(key);
            MarkReachable();

            await FlushPendingAsync();
        }
        catch
        {
            //Recorded as a pending *delete*: the value is gone locally, so a plain "push this key" would
            //have nothing to push and the server would keep serving what the user deleted.
            await AddPendingAsync(key, isDelete: true);
            MarkUnreachable();
        }
    }

    #region The pending set

    private async Task EnsurePendingLoadedAsync()
    {
        if (_pending != null)
            return;

        try
        {
            _pending = await _backup.GetAsync<Dictionary<string, bool>>(PendingKey) ?? new();
        }
        catch
        {
            _pending = new();
        }
    }

    private async Task AddPendingAsync(string key, bool isDelete)
    {
        if (_pending.TryGetValue(key, out var existing) && existing == isDelete)
            return;

        _pending[key] = isDelete;

        await SavePendingAsync();
    }

    private async Task ClearPendingAsync(string key)
    {
        if (!_pending.Remove(key))
            return;

        await SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        try
        {
            await _backup.SetAsync(PendingKey, _pending);
        }
        catch { }
    }

    #endregion

    #region Reachability and the push

    private void MarkUnreachable()
    {
        if (!IsServerReachable)
            return;

        IsServerReachable = false;
        ServerReachabilityChanged?.Invoke();
    }

    //A call that succeeded is the only reliable signal the server is back — there is no reconnect event to
    //subscribe to and nothing worth polling for.
    private void MarkReachable()
    {
        if (IsServerReachable)
            return;

        IsServerReachable = true;
        ServerReachabilityChanged?.Invoke();
    }

    ///<summary>
    ///Pushes every locally-written key the server has not seen. Safe to call at any time; it does nothing
    ///when there is nothing pending, which is the normal case.
    ///</summary>
    public async Task FlushPendingAsync()
    {
        await EnsurePendingLoadedAsync();

        //Re-entrancy guard: the pushes below go through _primary directly, but a caller could still land
        //here from two directions at once.
        if (_flushing || _pending.Count == 0)
            return;

        _flushing = true;

        try
        {
            foreach (var (key, isDelete) in _pending.ToList())
            {
                try
                {
                    if (isDelete)
                        await _primary.RemoveAsync(key);
                    else
                        await _primary.SetStringAsync(key, await _backup.GetStringAsync(key));

                    _pending.Remove(key);
                    MarkReachable();
                }
                catch
                {
                    //It went away again mid-push. Everything still pending stays pending.
                    MarkUnreachable();
                    break;
                }
            }

            await SavePendingAsync();
        }
        finally
        {
            _flushing = false;
        }
    }

    #endregion
}
