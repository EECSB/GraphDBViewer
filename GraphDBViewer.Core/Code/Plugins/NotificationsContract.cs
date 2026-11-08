using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///Notifications: what the bell and the page ask for and what they are told (the notifications spec). The host's own,
///like backups, because every plugin raises them: there is no plugin id, and no permission of their own. Everybody
///signed in has their own, and each one is seen only by somebody holding the permission its kind is gated by.
///</summary>
public static class NotificationsContract
{
    public const string Route = "/notifications";

    ///<summary>GET: a page of the caller's own, newest first, as <see cref="NotificationPage"/>. DELETE: all of them.</summary>
    public const string BasePath = "api/notifications";

    ///<summary>GET: how many of the caller's are unread, as <see cref="NotificationCount"/>.</summary>
    public const string CountPath = "api/notifications/count";

    ///<summary>POST: mark every one of the caller's read. <c>{id}/read</c> marks one.</summary>
    public const string ReadAllPath = "api/notifications/read";

    ///<summary>GET and PUT: the caller's <see cref="NotificationSettings"/>.</summary>
    public const string SettingsPath = "api/notifications/settings";

    ///<summary>GET: every kind the caller may hear about, as <see cref="NotificationKindShown"/>s.</summary>
    public const string KindsPath = "api/notifications/kinds";

    ///<summary>GET: which ways besides the bell this instance can carry, as <see cref="NotificationOffer"/>.</summary>
    public const string OfferPath = "api/notifications/offer";

    ///<summary>GET: the instance's public key for Web Push, as <see cref="NotificationPushKey"/> (phase 3).</summary>
    public const string PushKeyPath = "api/notifications/push/key";

    ///<summary>POST: this browser's subscription, as <see cref="NotificationPushSubscription"/>. DELETE: this browser's, ended.</summary>
    public const string PushSubscriptionPath = "api/notifications/push/subscription";

    ///<summary>GET: the browsers the caller has notifications on in, as <see cref="NotificationBrowserShown"/>s. DELETE <c>{id}</c>: one of them, ended.</summary>
    public const string PushBrowsersPath = "api/notifications/push/browsers";

    ///<summary>POST: a test notification to this browser, now, whatever the settings say.</summary>
    public const string PushTestPath = "api/notifications/push/test";

    ///<summary>The worker that shows a push, at the root so that its scope is the whole app.</summary>
    public const string PushWorker = "/notify-worker.js";

    ///<summary>POST: an installed plugin's container raising one, with a token scoped <c>notify:&lt;plugin id&gt;</c>.</summary>
    public const string RaisePath = "api/notifications/raise";

    ///<summary>The hub a page subscribes to: each new notification and each change is published to it as it happens.</summary>
    public const string HubPath = "api/notifications/hub";

    ///<summary>What the hub calls on a page, with a <see cref="NotificationEvent"/>.</summary>
    public const string HubMethod = "Notified";

    ///<summary>How many the bell shows when it opens.</summary>
    public const int BellCount = 10;

    ///<summary>A page of the list.</summary>
    public const int PageSize = 50;

    ///<summary>How long notifications are kept unless a person chooses otherwise (question 4).</summary>
    public const int DefaultKeepDays = 30;

    ///<summary>How long the first one waiting to be mailed waits for others, so a burst goes as one mail (question 7).</summary>
    public const int MailGatherMinutes = 15;

    ///<summary>How many quiet times a person may set.</summary>
    public const int MostQuietTimes = 14;

    ///<summary>The kinds of event the hub publishes.</summary>
    public const string Arrived = "arrived";
    public const string Changed = "changed";
    public const string Removed = "removed";
}

///<summary>One notification as its person sees it.</summary>
public sealed class NotificationShown
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; }

    ///<summary>The kind's name, e.g. <em>A purchase order waiting for approval</em>.</summary>
    [JsonPropertyName("kindName")] public string KindName { get; set; }

    ///<summary>The plugin that raised it, by name, for the icon and for an installed plugin's mark.</summary>
    [JsonPropertyName("plugin")] public string Plugin { get; set; }

    ///<summary>A Bootstrap icon class for the plugin, e.g. <c>bi-cart</c>.</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; }

    [JsonPropertyName("sentence")] public string Sentence { get; set; }
    [JsonPropertyName("link")] public string Link { get; set; }
    [JsonPropertyName("subject")] public string Subject { get; set; }
    [JsonPropertyName("raisedAt")] public DateTimeOffset RaisedAt { get; set; }
    [JsonPropertyName("readAt")] public DateTimeOffset? ReadAt { get; set; }
    [JsonPropertyName("settledAt")] public DateTimeOffset? SettledAt { get; set; }

    ///<summary>Raised by an installed plugin through the API rather than by the host or a bundled plugin.</summary>
    [JsonPropertyName("thirdParty")] public bool ThirdParty { get; set; }
}

///<summary>A page of a person's notifications, and how many of all of them are unread.</summary>
public sealed class NotificationPage
{
    [JsonPropertyName("items")] public List<NotificationShown> Items { get; set; } = [];
    [JsonPropertyName("unread")] public int Unread { get; set; }

    ///<summary>Whether there are older ones than the last on this page.</summary>
    [JsonPropertyName("more")] public bool More { get; set; }
}

public sealed class NotificationCount
{
    [JsonPropertyName("unread")] public int Unread { get; set; }
}

///<summary>What the hub publishes to a page: one arrived, changed or was removed, and the unread count after it.</summary>
public sealed class NotificationEvent
{
    ///<summary><see cref="NotificationsContract.Arrived"/>, <see cref="NotificationsContract.Changed"/> or <see cref="NotificationsContract.Removed"/>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; }

    ///<summary>The notification, for one that arrived or changed. Null for one removed, or for many changed at once.</summary>
    [JsonPropertyName("notification")] public NotificationShown Notification { get; set; }

    [JsonPropertyName("unread")] public int Unread { get; set; }
}

///<summary>A kind a person may hear about, for the settings page.</summary>
public sealed class NotificationKindShown
{
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("plugin")] public string Plugin { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; }

    ///<summary>Noticed by the daily look rather than raised as it happens.</summary>
    [JsonPropertyName("condition")] public bool Condition { get; set; }
}

///<summary>
///Which ways besides the bell this instance can carry, for the settings panel to offer or to say why not (criterion 18).
///Mail needs the instance's mail server. The browser's own notifications need nothing of the instance's, and a page
///served over HTTPS, which the page finds out for itself.
///</summary>
public sealed class NotificationOffer
{
    [JsonPropertyName("mail")] public bool Mail { get; set; }

    ///<summary>Why mail is not offered, said to whoever reads the settings; empty when it is.</summary>
    [JsonPropertyName("mailWhyNot")] public string MailWhyNot { get; set; } = "";

    ///<summary>Whether this instance pushes to browsers (phase 3).</summary>
    [JsonPropertyName("browser")] public bool Browser { get; set; }
}

///<summary>The instance's public key for Web Push: uncompressed P-256, base64url, as a browser's <c>applicationServerKey</c> takes it.</summary>
public sealed class NotificationPushKey
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

///<summary>A browser's push subscription, as its <c>PushSubscription.toJSON()</c> gives it: where to push, and the keys to encrypt for it with.</summary>
public sealed class NotificationPushSubscription
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("p256dh")] public string P256dh { get; set; } = "";
    [JsonPropertyName("auth")] public string Auth { get; set; } = "";
}

///<summary>One browser a person has notifications on in, for the settings' list.</summary>
public sealed class NotificationBrowserShown
{
    [JsonPropertyName("id")] public long Id { get; set; }

    ///<summary>The browser as its user agent names it, e.g. <em>Chrome on Windows</em>.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }

    ///<summary>Whether it is the browser asking.</summary>
    [JsonPropertyName("thisBrowser")] public bool ThisBrowser { get; set; }
}

///<summary>What this browser can do and has done, as the page reads it from the browser.</summary>
public sealed class NotificationPushState
{
    ///<summary>Whether the browser has a service worker, the Push API and notifications at all.</summary>
    [JsonPropertyName("supported")] public bool Supported { get; set; }

    ///<summary>Whether this page is a secure context, HTTPS or the machine's own address, which push needs (criterion 18).</summary>
    [JsonPropertyName("secure")] public bool Secure { get; set; }

    ///<summary>What the person told the browser: <c>granted</c>, <c>denied</c> or <c>default</c> (not asked yet, or dismissed).</summary>
    [JsonPropertyName("permission")] public string Permission { get; set; } = "";

    ///<summary>Where this browser is subscribed, or empty when it is not.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
}

///<summary>Turning on in this browser: the subscription made, or why there is none (<c>denied</c>, <c>default</c>, or the browser's own words).</summary>
public sealed class NotificationPushTurnedOn
{
    [JsonPropertyName("subscription")] public NotificationPushSubscription Subscription { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

///<summary>
///One person's settings. <see cref="KeepDays"/> null keeps them forever; a person who never saved any has 30 days and
///the bell alone for every kind (question 5). The mail switches and the quiet time are acted on since phase 2, and the
///browser switches since phase 3, in every browser the person turned them on in.
///</summary>
public sealed class NotificationSettings
{
    [JsonPropertyName("keepDays")] public int? KeepDays { get; set; } = NotificationsContract.DefaultKeepDays;

    [JsonPropertyName("kinds")] public Dictionary<string, NotificationWays> Kinds { get; set; } = [];

    [JsonPropertyName("quiet")] public NotificationQuiet Quiet { get; set; } = new();
}

///<summary>The ways besides the bell, per kind. The bell always lists everything the person hears about.</summary>
public sealed class NotificationWays
{
    [JsonPropertyName("browser")] public bool Browser { get; set; }
    [JsonPropertyName("mail")] public bool Mail { get; set; }
}

///<summary>
///When the browser and mail hold back: spans of hours on days of the week, in the person's own zone. What a quiet time
///held reaches the person as one mail, and as one browser notification, when it ends (question 6).
///</summary>
public sealed class NotificationQuiet
{
    ///<summary>An IANA or Windows zone id, as the browser reports it. Empty, or one the server does not know, is the instance's.</summary>
    [JsonPropertyName("zone")] public string Zone { get; set; } = "";

    [JsonPropertyName("spans")] public List<NotificationQuietSpan> Spans { get; set; } = [];
}

public sealed class NotificationQuietSpan
{
    ///<summary>
    ///Days of the week, 0 Sunday to 6 Saturday, as <see cref="DayOfWeek"/> counts them: the days the span starts on. One
    ///that runs over midnight carries on into the next morning whatever day that is.
    ///</summary>
    [JsonPropertyName("days")] public List<int> Days { get; set; } = [];

    ///<summary>
    ///<c>HH:mm</c>. A span whose end is before its start runs over midnight, and one whose end is its start lasts the
    ///whole day from then.
    ///</summary>
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

///<summary>What an installed plugin's container sends to raise one: never people, only its own permissions.</summary>
public sealed class NotificationRaiseRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("subject")] public string Subject { get; set; }
    [JsonPropertyName("sentence")] public string Sentence { get; set; }
    [JsonPropertyName("link")] public string Link { get; set; }
    [JsonPropertyName("holders")] public List<string> Holders { get; set; } = [];
}

///<summary>The answer to a raise, read or delete: whether it went, and why not.</summary>
public sealed class NotificationAnswer
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; }
    [JsonPropertyName("unread")] public int Unread { get; set; }

    ///<summary>How many were raised, marked or removed.</summary>
    [JsonPropertyName("count")] public int Count { get; set; }
}
