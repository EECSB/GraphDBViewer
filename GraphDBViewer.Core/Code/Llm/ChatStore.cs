using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///An AI window's chats kept (2026-11-02, GDS Viewer's ChatStore), behind <see cref="IAppStorage"/>: with the account in
///Treeality, whose storage is its server's, and in the browser's IndexedDB in the standalone viewer, which is where GDS
///Viewer's own Treeality plugin plan put them. Held by <see cref="LlmConnectionStore.ChatsFor"/>, one per window, rather
///than registered on its own, as the speech preference is, so neither host has a service to add.
///
///<strong>A list and the chats, under keys of their own</strong>: the list is read whenever the window shows it, and
///reading every chat whole to name it would read every picture in every one. Each chat is written before the list that
///names it, so a write cut short leaves a chat the list does not name rather than a row that opens nothing, and a row whose
///chat is gone anyway is dropped from the list when it is opened (<see cref="LoadAsync"/>).
///</summary>
public sealed class ChatStore
{
    ///<summary>Ask AI's chats: the window these were first kept for, under the keys below.</summary>
    public const string AskAi = "ask-ai";

    ///<summary>Ask AI's list. Another window's is this with its name after a colon.</summary>
    public const string ListKey = "graphdbviewer:aiChats";

    ///<summary>Where Ask AI's chats are kept, each under its id. Another window's are under its name and a colon after this.</summary>
    public const string ChatKeyPrefix = "graphdbviewer:aiChat:";

    private readonly IAppStorage _storage;
    private readonly string _listKey;
    private readonly string _chatKeyPrefix;

    ///<summary>
    ///The chats of the window named <paramref name="window"/>: each window's are its own (the user, 2026-11-02: "don't mix
    ///stuff"), so a plugin's chats never show in Ask AI's list, nor Ask AI's in a plugin's.
    ///</summary>
    public ChatStore(IAppStorage storage, string window = AskAi)
    {
        _storage = storage;
        Window = window;

        if (window == AskAi)
        {
            _listKey = ListKey;
            _chatKeyPrefix = ChatKeyPrefix;
        }
        else
        {
            _listKey = $"{ListKey}:{window}";
            _chatKeyPrefix = $"{ChatKeyPrefix}{window}:";
        }
    }

    ///<summary>The window whose chats these are.</summary>
    public string Window { get; }

    ///<summary>Raised after a chat is kept or deleted, so the list can be read again.</summary>
    public event Func<Task> Changed;

    ///<summary>The chats kept, the one changed last first. Never null.</summary>
    public async Task<List<ChatSummary>> ListAsync()
    {
        return ReadList(await _storage.GetStringAsync(_listKey));
    }

    ///<summary>A chat by its id, or null when there is none kept under it, in which case the list stops naming it.</summary>
    public async Task<SavedChat> LoadAsync(string id)
    {
        var chat = ReadChat(await _storage.GetStringAsync(_chatKeyPrefix + id));

        if (chat == null)
            await DropFromListAsync(id);

        return chat;
    }

    ///<summary>Keeps a chat, and puts it at the top of the list.</summary>
    public async Task SaveAsync(SavedChat chat)
    {
        await _storage.SetStringAsync(_chatKeyPrefix + chat.Id, WriteChat(chat));

        var list = await ListAsync();

        list.RemoveAll(each => each.Id == chat.Id);
        list.Insert(0, chat.Summary());

        await _storage.SetStringAsync(_listKey, WriteList(Ordered(list)));
        await OnChanged();
    }

    public async Task DeleteAsync(string id)
    {
        await DropFromListAsync(id);
        await _storage.RemoveAsync(_chatKeyPrefix + id);
        await OnChanged();
    }

    private async Task DropFromListAsync(string id)
    {
        var list = await ListAsync();

        if (list.RemoveAll(each => each.Id == id) > 0)
            await _storage.SetStringAsync(_listKey, WriteList(list));
    }

    private async Task OnChanged()
    {
        var handler = Changed;

        if (handler == null)
            return;

        foreach (var listener in handler.GetInvocationList())
            await ((Func<Task>)listener)();
    }

    ///<summary>The list in the order it is shown: changed last first.</summary>
    public static List<ChatSummary> Ordered(IEnumerable<ChatSummary> list)
    {
        return list.OrderByDescending(each => each.Updated).ToList();
    }

    ///<summary>The list out of its stored text. Text that will not read is no chats rather than an error, as the models' is.</summary>
    public static List<ChatSummary> ReadList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<ChatSummary>();

        try
        {
            return JsonSerializer.Deserialize<List<ChatSummary>>(json) ?? new List<ChatSummary>();
        }
        catch (JsonException)
        {
            return new List<ChatSummary>();
        }
    }

    public static string WriteList(List<ChatSummary> list)
    {
        return JsonSerializer.Serialize(list);
    }

    ///<summary>A chat out of its stored text, or null when there is none or it will not read.</summary>
    public static SavedChat ReadChat(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SavedChat>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string WriteChat(SavedChat chat)
    {
        return JsonSerializer.Serialize(chat);
    }
}
