namespace GraphDBViewerWeb.Code;

///<summary>
///Backups: what the page asks for and what it is told (the backups spec). The host's own, like accounts
///and the instance graph, because it covers every plugin's data at once — there is no plugin id.
///</summary>
public static class BackupsContract
{
    public const string Route = "/backups";

    ///<summary>GET: the destination's state, the setting, and the backups it holds. Answers <see cref="BackupsState"/>.</summary>
    public const string StatePath = "api/backups/state";

    ///<summary>POST: take one now. Answers <see cref="BackupResult"/> — the manifest, or the sentence saying why not.</summary>
    public const string TakePath = "api/backups/take";

    ///<summary>POST: restore one, named in <see cref="BackupRestoreRequest"/>. Answers <see cref="BackupResult"/>.</summary>
    public const string RestorePath = "api/backups/restore";

    ///<summary>PUT: the destination and the schedule, as <see cref="BackupSettings"/>. GET is part of the state.</summary>
    public const string SettingsPath = "api/backups/settings";

    ///<summary>Where the settings live in instance state. Reserved: a destination may carry an S3 secret.</summary>
    public const string SettingsKey = "instance:backups:settings";

    ///<summary>Where the last backup's fact lives — read by the page and by the purge, written only when one finished.</summary>
    public const string LatestKey = "instance:backups:latest";

    ///<summary>Taking a backup, and restoring one: whoever may take one may need the other on the same day (question 5).</summary>
    public const string PermissionTake = "backups.take";

    ///<summary>Seeing the list and the last one's time.</summary>
    public const string PermissionRead = "backups.read";

    ///<summary>How old the last backup may be and still satisfy the purge. Twenty-four hours unless an administrator says otherwise.</summary>
    public const int DefaultWindowHours = 24;

    ///<summary>How many backups the destination keeps. Seven unless an administrator says otherwise.</summary>
    public const int DefaultKeep = 7;

    ///<summary>The schedule's three shapes.</summary>
    public const string ScheduleOff = "off";
    public const string ScheduleDaily = "daily";
    public const string ScheduleWeekly = "weekly";

    ///<summary>A destination is a folder on a mounted volume, or an S3 bucket. Nothing is a default.</summary>
    public const string DestinationNone = "none";
    public const string DestinationFolder = "folder";
    public const string DestinationS3 = "s3";

    ///<summary>What a setting has to be for the page to send it, in the words the page shows. Null when it is fine.</summary>
    public static string Validate(BackupSettings settings)
    {
        if (settings == null)
            return "No settings were sent.";

        if (settings.Destination != DestinationNone && settings.Destination != DestinationFolder && settings.Destination != DestinationS3)
            return "A destination is a folder or an S3 bucket, or none at all.";

        if (settings.Destination == DestinationFolder && string.IsNullOrWhiteSpace(settings.Folder))
            return "A folder destination needs a path.";

        if (settings.Destination == DestinationS3 && (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Bucket)))
            return "An S3 destination needs an endpoint and a bucket.";

        if (settings.Schedule != ScheduleOff && settings.Schedule != ScheduleDaily && settings.Schedule != ScheduleWeekly)
            return "A schedule is off, daily or weekly.";

        if (settings.Hour < 0 || settings.Hour > 23)
            return "The hour is between 0 and 23.";

        if (settings.Weekday < 0 || settings.Weekday > 6)
            return "The day is between Sunday (0) and Saturday (6).";

        if (settings.Keep < 1 || settings.Keep > 365)
            return "Keep between 1 and 365 backups.";

        if (settings.WindowHours < 1 || settings.WindowHours > 24 * 365)
            return "The window is between an hour and a year.";

        return null;
    }
}

///<summary>
///Where backups go and when they are taken. Held in instance state under a reserved key, because an S3
///destination carries a secret and the generic storage surface must never hand one back.
///</summary>
public sealed class BackupSettings
{
    public string Destination { get; set; } = BackupsContract.DestinationNone;

    ///<summary>An absolute path, on a volume that outlives the container — the page says why that matters.</summary>
    public string Folder { get; set; } = "";

    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "";
    public string AccessKey { get; set; } = "";

    ///<summary>Write-only to the browser: the page sends a new one or the empty string to keep what is stored.</summary>
    public string SecretKey { get; set; } = "";

    public string Schedule { get; set; } = BackupsContract.ScheduleOff;

    ///<summary>The hour, in the host's local time, that a daily or weekly backup starts.</summary>
    public int Hour { get; set; } = 2;

    ///<summary>For a weekly schedule: 0 is Sunday, as <see cref="System.DayOfWeek"/> counts.</summary>
    public int Weekday { get; set; }

    public int Keep { get; set; } = BackupsContract.DefaultKeep;

    public int WindowHours { get; set; } = BackupsContract.DefaultWindowHours;
}

///<summary>One backup, as its manifest records it and the page lists it.</summary>
public sealed class BackupManifest
{
    ///<summary>The instant, and the name of the folder the three objects sit in.</summary>
    public string Stamp { get; set; }

    public DateTimeOffset TakenAt { get; set; }

    ///<summary>Who asked for it, or "the schedule".</summary>
    public string By { get; set; }

    ///<summary>What the graph called itself when it was read — a restore into a different engine is visible rather than silent.</summary>
    public string Engine { get; set; }

    ///<summary>What the READ-BACK counted, never what was sent (criterion 8).</summary>
    public long Vertices { get; set; }

    public long Edges { get; set; }

    public long GraphBytes { get; set; }

    public long DatabaseBytes { get; set; }

    ///<summary>The DAM's store as it was then, and what it held: named and counted, never copied (criterion 3).</summary>
    public string AssetStore { get; set; }

    public long Assets { get; set; }

    public long AssetBytes { get; set; }

    ///<summary>The app that took it, so a restore into an older one is a question somebody can ask.</summary>
    public string Version { get; set; }
}

///<summary>What the page draws: whether there is a destination, what it holds, and what the schedule is.</summary>
public sealed class BackupsState
{
    ///<summary>False when no destination is configured — the page's first face, a setup state and not a fault.</summary>
    public bool Configured { get; set; }

    ///<summary>Why the destination does not answer, when it does not. Null when it does.</summary>
    public string Problem { get; set; }

    public bool MayTake { get; set; }

    public BackupSettings Settings { get; set; } = new();

    ///<summary>Newest first.</summary>
    public List<BackupManifest> Backups { get; set; } = new();

    ///<summary>The last one, as instance state records it — the fact the purge checks. Null when there has never been one.</summary>
    public BackupManifest Latest { get; set; }

    ///<summary>When the schedule next falls due, or null when it is off.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    ///<summary>What the last scheduled run did, on this server. Null until one has.</summary>
    public string LastScheduled { get; set; }
}

///<summary>Taking one or putting one back: what happened, or the sentence saying why nothing did.</summary>
public sealed class BackupResult
{
    public bool Ok { get; set; }

    public string Error { get; set; }

    ///<summary>The backup taken, on a take that finished.</summary>
    public BackupManifest Manifest { get; set; }

    ///<summary>On a restore: what went in.</summary>
    public long Vertices { get; set; }

    public long Edges { get; set; }

    ///<summary>Elements that could not be written, each named — never skipped silently.</summary>
    public List<string> Refused { get; set; } = new();

    public static BackupResult Refusal(string error)
    {
        return new BackupResult { Ok = false, Error = error };
    }
}

///<summary>Which backup to restore, and the instance's own name typed out when the graph is not empty.</summary>
public sealed class BackupRestoreRequest
{
    public string Stamp { get; set; }

    ///<summary>The graph is emptied first only when this says so, and the page makes the caller type it.</summary>
    public bool ReplaceExisting { get; set; }
}
