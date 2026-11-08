using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>The calls on the notifications API (<see cref="NotificationsContract"/>) that more than one page makes, made once.</summary>
public static class NotificationRequests
{
    ///<summary>
    ///Marks every notification read: the host's answer, with the count left unread, or null when it answered nothing. A
    ///host that cannot be reached throws, for the page to say. The bell and the Notifications page each made this call
    ///themselves until 2026-11-07.
    ///</summary>
    public static async Task<NotificationAnswer> ReadAllAsync(HttpClient http)
    {
        var response = await http.PostAsync(NotificationsContract.ReadAllPath, null);

        return await response.Content.ReadFromJsonAsync<NotificationAnswer>();
    }
}
