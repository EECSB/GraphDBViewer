using System.Net.Http.Json;

namespace GraphDBViewerWeb.Code;

///<summary>
///Who the browser is signed in as, asked of the host once per app lifetime and remembered. The storage
///seam needs only the id: the browser's mirror of the workspace is stamped with it, and every request to
///the host's store names it (see <see cref="FallbackAppStorage.OwnerKey"/> and
///<see cref="AppStorageContract.AccountHeader"/>). An ask that failed, or that came back without an id,
///is not remembered, so the next caller asks again — the host may simply not have been reachable yet.
///</summary>
public class SignedInAccount
{
    private readonly HttpClient _http;
    private Task<string> _asked;

    public SignedInAccount(HttpClient http)
    {
        _http = http;
    }

    ///<summary>The account's id, or null when the host cannot say.</summary>
    public Task<string> IdAsync()
    {
        var asked = _asked;

        if (asked != null && (!asked.IsCompleted || (asked.IsCompletedSuccessfully && asked.Result != null)))
            return asked;

        asked = AskAsync();
        _asked = asked;

        return asked;
    }

    private async Task<string> AskAsync()
    {
        try
        {
            var response = await _http.GetAsync(AccountInfo.Path);

            if (!response.IsSuccessStatusCode)
                return null;

            var account = await response.Content.ReadFromJsonAsync<AccountInfo>();

            return account?.Id;
        }
        catch
        {
            return null;
        }
    }
}
