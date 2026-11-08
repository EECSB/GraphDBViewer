using System;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///A page's subscription to its person's notifications (the notifications spec): the unread count, and an event for
///each one that arrives or changes, as the host publishes them. The core declares it and draws the bell from it; the
///app registers one over its hub. The standalone viewer registers none, so it shows no bell.
///</summary>
public interface INotificationFeed
{
    ///<summary>How many are unread, as of the last event or the last count read.</summary>
    int Unread { get; }

    ///<summary>Whether the subscription is open now. While it is not, the count is the last one known.</summary>
    bool Connected { get; }

    ///<summary>Opens the subscription once; calling it again does nothing. Reads the count on every (re)connection.</summary>
    Task StartAsync();

    ///<summary>Sets the count from an answer the page already has, such as the list's, without waiting for an event.</summary>
    void Counted(int unread);

    ///<summary>Raised for every event, and when the count is read again after a reconnection (with a null event).</summary>
    event Action<NotificationEvent> Changed;
}
