using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///This browser's own notifications (the notifications spec, phase 3): what it can do, and turning them on and off in it.
///The core declares it and the settings panel draws the switch from it; the app registers one over the browser's Push
///API. The standalone viewer registers none.
///</summary>
public interface INotificationPush
{
    ///<summary>What this browser can do and has done, read fresh each time.</summary>
    Task<NotificationPushState> StateAsync();

    ///<summary>Asks the browser's permission, which is the only time it is asked (criterion 14), and subscribes it with the instance's key.</summary>
    Task<NotificationPushTurnedOn> TurnOnAsync(string applicationServerKey);

    ///<summary>Ends this browser's subscription. Its endpoint, to tell the instance, or empty when it had none.</summary>
    Task<string> TurnOffAsync();
}
