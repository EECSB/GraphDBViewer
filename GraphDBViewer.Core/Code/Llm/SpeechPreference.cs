using System;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///Which way Ask AI's microphone turns speech into words (<see cref="SpeechMode"/>), chosen in AI settings (2026-11-02,
///GDS Viewer's): the browser's own to begin with, the AI provider for accuracy. Kept where the models are, behind
///<see cref="IAppStorage"/>, and held by <see cref="LlmConnectionStore.Speech"/> rather than registered on its own, so
///the settings window and the chat share one and hear each other's change, and neither host has a service to add.
///</summary>
public sealed class SpeechPreference
{
    public const string StorageKey = "graphdbviewer:speechInput";

    private readonly IAppStorage _storage;
    private bool _loaded;
    private Task<SpeechMode> _loading;
    private SpeechMode _mode = SpeechMode.Browser;

    public SpeechPreference(IAppStorage storage)
    {
        _storage = storage;
    }

    ///<summary>Raised after the mode changes, so the window's microphone says what it does now.</summary>
    public event Func<Task> Changed;

    ///<summary>
    ///The mode, read once from where it is kept. The browser's until something else was chosen.
    ///
    ///One read, shared by everybody who asks before it answers: AI settings and the Ask AI window both read it as a page
    ///comes up, and until 2026-11-07 the first was told what was kept and the second the default, since the first ask
    ///marked it loaded before the storage had answered.
    ///</summary>
    public Task<SpeechMode> GetAsync()
    {
        if (_loaded)
            return Task.FromResult(_mode);

        _loading ??= LoadAsync();

        return _loading;
    }

    private async Task<SpeechMode> LoadAsync()
    {
        string text;

        try
        {
            text = await _storage.GetStringAsync(StorageKey);
        }
        catch
        {
            //A read that failed is not kept as the answer: the next ask reads again.
            _loading = null;

            throw;
        }

        //A choice made while the read was out is newer than what was read.
        if (!_loaded)
        {
            _loaded = true;
            _mode = Read(text);
        }

        return _mode;
    }

    public async Task SetAsync(SpeechMode chosen)
    {
        _loaded = true;

        if (chosen == _mode)
            return;

        _mode = chosen;

        await _storage.SetStringAsync(StorageKey, chosen.ToString());

        var handler = Changed;

        if (handler == null)
            return;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();
    }

    ///<summary>A kept mode out of its text: anything that is not the provider's is the browser's.</summary>
    public static SpeechMode Read(string text)
    {
        if (string.Equals(text, nameof(SpeechMode.Provider), StringComparison.Ordinal))
            return SpeechMode.Provider;

        return SpeechMode.Browser;
    }
}
