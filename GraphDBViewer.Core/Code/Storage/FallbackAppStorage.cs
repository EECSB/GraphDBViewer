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

    ///<summary>
    ///Whose mirror this is, kept in the local store: the id of the account the app last booted as. Checked
    ///against who is signed in now before anything is read, and a different answer empties the mirror
    ///first. Without it a second account on the same browser was handed the first one's workspace — the
    ///server had nothing under the new account, and "nothing on the server" meant "adopt the local copy"
    ///(the 2026-09-17 audit, reproduced end to end; fixed 2026-09-18).
    ///</summary>
    public const string OwnerKey = "graphdbviewer:__owner";

    private readonly IAppStorage _primary;
    private readonly IAppStorage _backup;
    private readonly Func<Task<string>> _accountAsync;

    //key → true when what is pending is a delete rather than a write. Loaded once, then kept in memory and
    //persisted only when it changes — which, while the server is reachable, is never.
    private Dictionary<string, bool> _pending;
    private bool _flushing;

    //The account the mirror is known to belong to, once the host has said who is signed in; null until then.
    private string _owner;
    private Task _claiming;

    ///<param name="accountAsync">
    ///Who is signed in, as the host knows it — the id, or null while the host cannot be asked. Left out
    ///where there are no accounts (the web-only edition), and then the mirror is simply the browser's.
    ///</param>
    public FallbackAppStorage(IAppStorage primary, IAppStorage backup, Func<Task<string>> accountAsync = null)
    {
        if (accountAsync != null && backup is not IMirrorStorage)
            throw new ArgumentException("A mirror claimed by an account has to be one that can be emptied for the next one.", nameof(backup));

        _primary = primary;
        _backup = backup;
        _accountAsync = accountAsync;

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
        return StoredJson.Read<T>(await GetStringAsync(key));
    }

    public async Task SetAsync<T>(string key, T value)
    {
        await SetStringAsync(key, JsonSerializer.Serialize(value));
    }

    public async Task<string> GetStringAsync(string key)
    {
        await EnsureReadyAsync();

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
    ///resurrect a deletion: a delete removes the local copy too, so there is nothing left to adopt. And it
    ///cannot hand one account another's: the mirror is stamped with whose it is (<see cref="OwnerKey"/>),
    ///and a different account signing in on this browser empties it before anything is read.
    ///</summary>
    private async Task<string> AdoptLocalAsync(string key)
    {
        //Not while it is unknown whose copy this is: adopting it would push another account's workspace
        //up as this one's, which is the bug the owner stamp exists to end.
        if (_accountAsync != null && _owner == null)
            return null;

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
        await EnsureReadyAsync();

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
        await EnsureReadyAsync();

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

    #region Whose mirror it is

    private async Task EnsureReadyAsync()
    {
        await ClaimAsync();
        await EnsurePendingLoadedAsync();
    }

    ///<summary>
    ///Makes the mirror the signed-in account's before anything reads it. Asked of the host once; while the
    ///host cannot say (it is not answering), the mirror stays whoever's it was — a read may still fall back
    ///to it, since offline it is all there is — and nothing in it is pushed up until the answer comes.
    ///Asked again on the next call until it does.
    ///</summary>
    private async Task ClaimAsync()
    {
        if (_accountAsync == null || _owner != null)
            return;

        //Callers landing here together share one ask. The in-flight task is let go by whoever finishes
        //awaiting it, and only if it is still the one they started — a claim that completes on the spot
        //(a cached answer) must not be kept as "in flight" and answered from forever after.
        var claiming = _claiming;

        if (claiming == null)
        {
            claiming = ClaimCoreAsync();
            _claiming = claiming;
        }

        try
        {
            await claiming;
        }
        finally
        {
            if (ReferenceEquals(_claiming, claiming))
                _claiming = null;
        }
    }

    private async Task ClaimCoreAsync()
    {
        string account;

        try
        {
            account = await _accountAsync();
        }
        catch
        {
            account = null;
        }

        if (string.IsNullOrEmpty(account))
            return;

        var stamped = await _backup.GetStringAsync(OwnerKey);

        if (stamped != null && stamped != account)
        {
            //Somebody else's. Emptied whole, the pending set with it: what that account wrote offline was
            //theirs to push, and this account is not them.
            await ((IMirrorStorage)_backup).ClearAsync();

            _pending = new();
        }

        if (stamped != account)
            await _backup.SetStringAsync(OwnerKey, account);

        _owner = account;
    }

    #endregion

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
        await EnsureReadyAsync();

        //Re-entrancy guard: the pushes below go through _primary directly, but a caller could still land
        //here from two directions at once.
        if (_flushing || _pending.Count == 0)
            return;

        //Nothing is pushed up until the host has said whose mirror this is (see ClaimAsync).
        if (_accountAsync != null && _owner == null)
            return;

        _flushing = true;

        try
        {
            foreach (var (key, isDelete) in _pending.ToList())
                try
                {
                    if (isDelete)
                        await _primary.RemoveAsync(key);
                    else
                        await PushAsync(key);

                    _pending.Remove(key);
                    MarkReachable();
                }
                catch
                {
                    //It went away again mid-push. Everything still pending stays pending.
                    MarkUnreachable();
                    break;
                }

            await SavePendingAsync();
        }
        finally
        {
            _flushing = false;
        }
    }

    //A pending key whose local copy has since gone — evicted, or cleared by the browser — has nothing to
    //push, and pushing the nothing would have overwritten the server's real value with an empty one.
    private async Task PushAsync(string key)
    {
        var local = await _backup.GetStringAsync(key);

        if (local == null)
            return;

        await _primary.SetStringAsync(key, local);
    }

    #endregion
}
