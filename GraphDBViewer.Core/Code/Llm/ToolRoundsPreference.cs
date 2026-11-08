using System;
using System.Globalization;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///How many rounds of tool calls Ask AI may make for one message before it pauses to ask whether to go on for another as many
///(2026-11-02, GDS Viewer's, where it was asked for on 2026-11-01): set in AI settings, <see cref="LlmProviderFactory.DefaultToolRounds"/>
///until something else is. Each round sends the whole conversation again and is billed, so this is the person's own cap on
///spend, not a limit on what works. Kept where the models are, behind <see cref="IAppStorage"/>, and held by
///<see cref="LlmConnectionStore.Rounds"/>, so the settings window and the chat share one and the chat reads what was set at
///every message.
///</summary>
public sealed class ToolRoundsPreference
{
    public const string StorageKey = "graphdbviewer:aiToolRounds";

    ///<summary>The fewest that can be set: one round is a single query or look, and anything less is no tools at all.</summary>
    public const int Fewest = 1;

    ///<summary>The most: a cap on a cap, so a slip of the keyboard cannot hand a run unwatched spend without end.</summary>
    public const int Most = 1000;

    private readonly IAppStorage _storage;
    private bool _loaded;
    private Task<int> _loading;
    private int _rounds = LlmProviderFactory.DefaultToolRounds;

    public ToolRoundsPreference(IAppStorage storage)
    {
        _storage = storage;
    }

    ///<summary>
    ///The rounds, read once from where they are kept.
    ///
    ///One read, shared by everybody who asks before it answers, as <see cref="SpeechPreference.GetAsync"/> is: AI settings
    ///and the Ask AI window both read them as a page comes up, and until 2026-11-07 the first was told what was kept and
    ///the second the default, since the first ask marked them loaded before the storage had answered, and a number set
    ///while the read was out was overwritten by what it read.
    ///</summary>
    public Task<int> GetAsync()
    {
        if (_loaded)
            return Task.FromResult(_rounds);

        _loading ??= LoadAsync();

        return _loading;
    }

    private async Task<int> LoadAsync()
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
            _rounds = Read(text);
        }

        return _rounds;
    }

    ///<summary>Keeps a new number, held between <see cref="Fewest"/> and <see cref="Most"/>, and says what was kept.</summary>
    public async Task<int> SetAsync(int chosen)
    {
        _loaded = true;
        chosen = Math.Clamp(chosen, Fewest, Most);

        if (chosen == _rounds)
            return _rounds;

        _rounds = chosen;

        await _storage.SetStringAsync(StorageKey, chosen.ToString(CultureInfo.InvariantCulture));

        return _rounds;
    }

    ///<summary>Kept rounds out of their text: anything that is not a number is the default, and a number out of range is brought into it.</summary>
    public static int Read(string text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kept))
            return LlmProviderFactory.DefaultToolRounds;

        return Math.Clamp(kept, Fewest, Most);
    }
}
