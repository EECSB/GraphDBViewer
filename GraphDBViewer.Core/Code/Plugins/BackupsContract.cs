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

    ///<summary>
    ///POST: take one now. <strong>Starts a run on the server and answers at once</strong> with the
    ///<see cref="BackupJobState"/> that has begun — it does not wait for it. A backup of a real catalog
    ///takes longer than a browser will hold a request open, which is what made this a job rather than an
    ///answer (specs/research/backup-restore-2026-09-15.md, finding 3).
    ///</summary>
    public const string TakePath = "api/backups/take";

    ///<summary>POST: restore one, named in <see cref="BackupRestoreRequest"/>. Starts a run and answers the <see cref="BackupJobState"/>.</summary>
    public const string RestorePath = "api/backups/restore";

    ///<summary>GET: what the run on this server is doing, or what the last one did. Answers <see cref="BackupJobState"/>.</summary>
    public const string JobPath = "api/backups/job";

    ///<summary>POST: ask the run to stop. It stops where it is and says so; nothing is undone.</summary>
    public const string StopPath = "api/backups/stop";

    ///<summary>PUT: the destination and the schedule, as <see cref="BackupSettings"/>. GET is part of the state.</summary>
    public const string SettingsPath = "api/backups/settings";

    ///<summary>Where the settings live in instance state. Reserved: a destination may carry an S3 secret.</summary>
    public const string SettingsKey = "instance:backups:settings";

    ///<summary>Where the last backup's fact lives — read by the page and by the purge, written only when one finished.</summary>
    public const string LatestKey = "instance:backups:latest";

    ///<summary>
    ///Where the last run's record lives — written when one starts and again when it ends, so a server that
    ///stopped in the middle of a restore leaves the fact behind it rather than an unexplained graph.
    ///</summary>
    public const string JobKey = "instance:backups:job";

    ///<summary>A run is taking a backup, or putting one back.</summary>
    public const string JobTake = "take";
    public const string JobRestore = "restore";

    ///<summary>How often the page asks a running job what it is doing. A run is minutes long; a second is plenty.</summary>
    public const int JobPollMs = 1000;

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

    ///<summary>
    ///Whether an S3 endpoint is dialed with TLS: anything but an http address is, a bare host included, which the
    ///client dials with https.
    ///</summary>
    public static bool IsEncrypted(string endpoint)
    {
        return !(endpoint ?? "").Trim().StartsWith("http:", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>
    ///Whether a save of <paramref name="given"/> over <paramref name="stored"/> forgets the secret key kept for it: when it
    ///takes the endpoint from one with TLS to one without and gives no secret of its own. The user's rule of 2026-10-03:
    ///a stored secret is never sent in plain text without somebody entering it again, and plain stays a choice, so a
    ///save that types the secret keeps it for http too. The server forgets by this, and the page says so before the save.
    ///</summary>
    public static bool ForgetsSecret(BackupSettings stored, BackupSettings given)
    {
        if (stored == null || given == null || !string.IsNullOrEmpty(given.SecretKey))
            return false;

        return IsEncrypted(stored.Endpoint) && !IsEncrypted(given.Endpoint);
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

    ///<summary>The run going on now, or the last one there was. Null when this instance has never had one.</summary>
    public BackupJobState Job { get; set; }
}

///<summary>
///A run of the backup or the restore: what it is, who started it, and how far it has got. This is what
///makes either of them possible on a real catalog — the work happens on the server and the page watches
///it, rather than the work being a request the browser holds open and gives up on after 100 seconds.
///
///<strong>The counts are what has been done, not what is left.</strong> Neither a backup nor a restore
///knows its own total until it has read the graph, and a percentage invented before then would be a
///guess dressed as a measurement.
///</summary>
public sealed class BackupJobState
{
    ///<summary><see cref="BackupsContract.JobTake"/> or <see cref="BackupsContract.JobRestore"/>.</summary>
    public string Kind { get; set; } = "";

    ///<summary>The backup being put back, on a restore.</summary>
    public string Stamp { get; set; }

    ///<summary>Who started it, or "the schedule".</summary>
    public string By { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    ///<summary>When it ended. Null while it is still going.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public bool Running { get; set; }

    ///<summary>
    ///What it is doing, in the words the page shows: emptying the graph, writing vertices, reading it
    ///back. The server writes the whole sentence — including the numbers an emptying has, which are its
    ///rounds rather than its vertices, because counting the graph after every round was most of what
    ///emptying cost until that was taken out (the restore research, finding 2).
    ///</summary>
    public string Phase { get; set; } = "";

    public long Vertices { get; set; }

    public long Edges { get; set; }

    ///<summary>Elements the restore would not write, counted. The first few are named in <see cref="Refused"/>.</summary>
    public long RefusedCount { get; set; }

    public List<string> Refused { get; set; } = new();

    ///<summary>True when it finished having done what it set out to do. Meaningless while <see cref="Running"/>.</summary>
    public bool Ok { get; set; }

    ///<summary>Why it did not, when it did not.</summary>
    public string Error { get; set; }

    ///<summary>True when somebody asked it to stop. It stopped where it was; nothing was undone.</summary>
    public bool Stopped { get; set; }

    ///<summary>
    ///True when the record says a run was going and this server is not the one that was running it — so
    ///the app was restarted or stopped in the middle of one. A restore interrupted this way leaves a graph
    ///that is part of a backup, and this is the only thing that says so.
    ///</summary>
    public bool Interrupted { get; set; }

    ///<summary>The stamp of the backup a take produced, once it has one.</summary>
    public string Produced { get; set; }
}

///<summary>
///Taking one or putting one back: what happened, or the sentence saying why nothing did.
///
///<strong>This one no longer crosses the wire.</strong> Since both became runs on the server (2026-09-16)
///it is what the runner hands back to the run, and <see cref="BackupJobState"/> is what a browser sees.
///It stays here beside the rest of the backups vocabulary rather than moving to the host, because it is
///the shape the manifest and the counts are written in and those are the contract.
///</summary>
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
