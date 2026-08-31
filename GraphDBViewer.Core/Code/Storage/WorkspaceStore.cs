using System.Text.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Persists the query workspace behind <see cref="IAppStorage"/>: the open tabs (heavy snapshots
///written on result changes + a tiny per-keystroke editor-text overlay), the last query text, the
///saved-queries list and the query history. The QueryTab ⇄ TabSnapshot conversion lives here too,
///so the round-trip (including the restore-time defaulting of legacy snapshots) is unit-testable.
///</summary>
public class WorkspaceStore
{
    private const string QueryStorageKey = "graphdbviewer:lastQuery";
    private const string SavedQueriesKey = "graphdbviewer:savedQueries";
    private const string HistoryKey = "graphdbviewer:queryHistory";
    private const string TabsKey = "graphdbviewer:tabs";
    private const string TabTextKey = "graphdbviewer:tabText";

    public const int MaxHistory = 20;

    private readonly IAppStorage _storage;

    public WorkspaceStore(IAppStorage storage)
    {
        _storage = storage;
    }

    #region Tabs

    ///<summary>
    ///Restores all open query tabs (each with its results, view mode, settings and styling) from the
    ///previous session, with the fresher per-keystroke editor text overlaid on top. Returns null when
    ///nothing was stored.
    ///</summary>
    public async Task<List<QueryTab>> LoadTabsAsync()
    {
        var snaps = await _storage.GetAsync<List<TabSnapshot>>(TabsKey);

        if (snaps == null || snaps.Count == 0)
            return null;

        var tabs = snaps.Select(FromSnapshot).ToList();

        //Overlay the per-keystroke editor text, which is fresher than the last heavy checkpoint.
        var texts = await _storage.GetAsync<List<TabText>>(TabTextKey);
        if (texts != null)
        {
            foreach (var tab in tabs)
            {
                var t = texts.FirstOrDefault(x => x.Id == tab.Id);
                if (t == null)
                    continue;

                tab.QueryText = t.Query;
                tab.GeneratedQueryText = t.Generated;
            }
        }

        return tabs;
    }

    ///<summary>Persists every tab as a heavy snapshot (results included).</summary>
    public async Task SaveTabsAsync(IEnumerable<QueryTab> tabs)
    {
        try
        {
            var snaps = tabs.Select(ToSnapshot).ToList();
            await _storage.SetAsync(TabsKey, snaps);
        }
        catch (Exception ex)
        {
            //A very large graph across several tabs can exceed the localStorage quota; don't let
            //a persistence failure break rendering or tab actions.
            Console.WriteLine($"Saving tabs failed: {ex.Message}");
        }
    }

    ///<summary>Persists only the editor text of every tab (no results) — cheap enough to run on each keystroke.</summary>
    public async Task SaveTabTextAsync(IEnumerable<QueryTab> tabs)
    {
        try
        {
            var texts = tabs.Select(t => new TabText { Id = t.Id, Query = t.QueryText, Generated = t.GeneratedQueryText }).ToList();
            await _storage.SetAsync(TabTextKey, texts);
        }
        catch { }
    }

    public static TabSnapshot ToSnapshot(QueryTab t)
    {
        return new TabSnapshot
        {
            Id = t.Id,
            Name = t.Name,
            QueryText = t.QueryText,
            EditorLanguage = t.EditorLanguage,
            GeneratedQueryText = t.GeneratedQueryText,
            QueryEditorTab = t.QueryEditorTab,
            QueryResults = t.QueryResults,
            VisualizationMode = t.VisualizationMode,
            SearchTerm = t.SearchTerm,
            HiddenLabels = t.HiddenLabels.ToList(),
            LastResultJson = ElementToJson(t.LastResultData),
            GraphResultJson = ElementToJson(t.GraphResultData),
            ResultsCleared = t.ResultsCleared,
            LoadDbLimit = t.LoadDbLimit,
            AutoReloadAfterCommit = t.AutoReloadAfterCommit,
            ReflectDbState = t.ReflectDbState,
            Cy2dLayout = t.Cy2dLayout,
            G3dLayout = t.G3dLayout,
            Saved2dPositionsJson = t.Saved2dPositionsJson,
            Saved3dPositionsJson = t.Saved3dPositionsJson,
            Show3dVertexLabels = t.Show3dVertexLabels,
            Show3dEdgeLabels = t.Show3dEdgeLabels,
            Show3dProps = t.Show3dProps,
            G3dExportFormat = t.G3dExportFormat,
            ImageExportFormat = t.ImageExportFormat,
            TableExportFormat = t.TableExportFormat,
            LabelStyles = t.LabelStyles,
            EdgeColorMode = t.EdgeColorMode,
            EdgeColors = t.EdgeColors
        };
    }

    ///<summary>
    ///Rebuilds a tab from its snapshot, defaulting anything a legacy or zeroed snapshot is missing
    ///(view mode, editor tab, limits, layouts and formats; the image-download format always resets
    ///to PNG on load).
    ///</summary>
    public static QueryTab FromSnapshot(TabSnapshot s)
    {
        return new QueryTab
        {
            Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString() : s.Id,
            Name = string.IsNullOrEmpty(s.Name) ? "Query" : s.Name,
            QueryText = s.QueryText,
            EditorLanguage = NormalizeEditorLanguage(s.EditorLanguage),
            GeneratedQueryText = s.GeneratedQueryText,
            QueryEditorTab = s.QueryEditorTab == 0 ? 1 : s.QueryEditorTab,
            QueryResults = s.QueryResults,
            VisualizationMode = s.VisualizationMode == 0 ? 2 : s.VisualizationMode,
            SearchTerm = s.SearchTerm,
            HiddenLabels = s.HiddenLabels == null ? new HashSet<string>() : new HashSet<string>(s.HiddenLabels),
            LastResultData = JsonToElement(s.LastResultJson),
            GraphResultData = JsonToElement(s.GraphResultJson),
            ResultsCleared = s.ResultsCleared,
            LoadDbLimit = s.LoadDbLimit == 0 ? 100 : s.LoadDbLimit,
            AutoReloadAfterCommit = s.AutoReloadAfterCommit,
            ReflectDbState = s.ReflectDbState,
            Cy2dLayout = string.IsNullOrEmpty(s.Cy2dLayout) ? "cose" : s.Cy2dLayout,
            G3dLayout = string.IsNullOrEmpty(s.G3dLayout) ? "force" : s.G3dLayout,
            Saved2dPositionsJson = s.Saved2dPositionsJson,
            Saved3dPositionsJson = s.Saved3dPositionsJson,
            Show3dVertexLabels = s.Show3dVertexLabels,
            Show3dEdgeLabels = s.Show3dEdgeLabels,
            Show3dProps = s.Show3dProps ?? true,
            G3dExportFormat = string.IsNullOrEmpty(s.G3dExportFormat) ? "obj" : s.G3dExportFormat,
            ImageExportFormat = "png",//always default the image-download format to PNG on load
            TableExportFormat = string.IsNullOrEmpty(s.TableExportFormat) ? "csv" : s.TableExportFormat,
            LabelStyles = s.LabelStyles ?? new Dictionary<string, LabelStyle>(),
            EdgeColorMode = s.EdgeColorMode,
            EdgeColors = s.EdgeColors ?? new Dictionary<string, string>()
        };
    }

    //Coerces a saved / embedded editor language to one the dropdown can actually select — Gremlin, Cypher,
    //SPARQL or AQL. Anything else (an empty legacy value, an unknown id) falls back to Gremlin, otherwise
    //the language <select> would show no selection.
    public static string NormalizeEditorLanguage(string language)
    {
        if (language == "gremlin" || language == "cypher" || language == "sparql" || language == "aql" || language == "dql")
            return language;

        return "gremlin";
    }

    private static string ElementToJson(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Undefined)
            return null;

        return el.GetRawText();
    }

    private static JsonElement JsonToElement(string json)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        return JsonDocument.Parse(json).RootElement;
    }

    #endregion


    #region Last query, saved queries & history

    public async Task<string> LoadLastQueryAsync()
    {
        return await _storage.GetStringAsync(QueryStorageKey);
    }

    public async Task SaveLastQueryAsync(string query)
    {
        await _storage.SetStringAsync(QueryStorageKey, query ?? string.Empty);
    }

    public async Task<Dictionary<string, string>> LoadSavedQueriesAsync()
    {
        return await _storage.GetAsync<Dictionary<string, string>>(SavedQueriesKey);
    }

    public async Task SaveSavedQueriesAsync(Dictionary<string, string> savedQueries)
    {
        await _storage.SetAsync(SavedQueriesKey, savedQueries);
    }

    public async Task<List<string>> LoadHistoryAsync()
    {
        return await _storage.GetAsync<List<string>>(HistoryKey);
    }

    public async Task SaveHistoryAsync(List<string> history)
    {
        await _storage.SetAsync(HistoryKey, history);
    }

    ///<summary>
    ///Records a query at the top of the history list in place (de-duplicated, capped at
    ///<see cref="MaxHistory"/>). Returns false — with the list untouched — for a blank query.
    ///</summary>
    public static bool PushHistory(List<string> history, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        query = query.Trim();
        history.RemoveAll(q => q == query);//move an existing entry to the top
        history.Insert(0, query);

        if (history.Count > MaxHistory)
            history.RemoveRange(MaxHistory, history.Count - MaxHistory);

        return true;
    }

    #endregion
}
