using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace GraphDBViewerWeb.Code;

///<summary>
///The action registry and broker's wire (ADR 0017 §4): what a member declares it can do, and what a run of it
///looks like while it runs and after. <strong>In the core because both halves read it</strong>: a member's
///server half declares actions and the platform brokers them, and a coordinator's page lists them to choose
///from and follows a run — Manufacturing's routing editor and step panel are the first.
///</summary>
public static class ActionsContract
{
    ///<summary><c>GET</c>: the actions the caller may run, from installed members.</summary>
    public const string BasePath = "api/actions";

    ///<summary><c>POST</c>: a one-time ticket for one action, which takes the action's permission.</summary>
    public static string TicketPath(string actionId)
    {
        return BasePath + "/" + Uri.EscapeDataString(actionId ?? "") + "/ticket";
    }

    ///<summary><c>POST</c>: starts a run — the ticket, the inputs, what it is for — and answers the run's id at once.</summary>
    public static string StartPath(string actionId)
    {
        return BasePath + "/" + Uri.EscapeDataString(actionId ?? "") + "/runs";
    }

    ///<summary><c>GET</c>: one run, as it stands.</summary>
    public static string RunPath(string runId)
    {
        return BasePath + "/runs/" + Uri.EscapeDataString(runId ?? "");
    }

    ///<summary><c>GET</c>: the run's events as they happen, ending when the run does — a stream of server-sent events.</summary>
    public static string StreamPath(string runId)
    {
        return RunPath(runId) + "/stream";
    }

    ///<summary><c>POST</c>: asks the run to stop.</summary>
    public static string CancelPath(string runId)
    {
        return RunPath(runId) + "/cancel";
    }
}

///<summary>How a run stands: running, or ended one of four ways.</summary>
public static class ActionStatuses
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool HasEnded(string status)
    {
        return status == Done || status == Failed || status == Canceled;
    }
}

///<summary>
///What a run concluded. <c>pass</c> and <c>fail</c> are the member's judgment of the work; <c>none</c> is a
///run that judges nothing, as sending a command does; <c>error</c> is a run that could not do its work at all;
///<c>canceled</c> is one somebody stopped.
///</summary>
public static class ActionVerdicts
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string None = "none";
    public const string Error = "error";
    public const string Canceled = "canceled";
}

///<summary>One value an action takes, declared as data (ADR 0017 rule 9) in a vocabulary's own words for a kind.</summary>
public sealed class ActionInput
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    ///<summary><c>text</c>, <c>number</c>, <c>date</c>, <c>flag</c>, or <c>choice:a|b|c</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("unit")]
    public string Unit { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("default")]
    public string Default { get; set; }

    ///<summary>The alternatives of a <c>choice:</c> input, or empty for every other kind.</summary>
    public IReadOnlyList<string> Choices
    {
        get
        {
            if (Kind == null || !Kind.StartsWith("choice:", StringComparison.Ordinal))
                return [];

            return Kind.Substring("choice:".Length).Split('|', StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

///<summary>One value an action answers with.</summary>
public sealed class ActionOutput
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";
}

///<summary>
///One action a member declares (ADR 0017 rule 4): its id under the member's plugin id, what it takes and
///returns, the permission it needs, and whether it moves something physical — in which case it runs only
///from a deliberate request carrying a one-time ticket (rule 7).
///</summary>
public sealed class ActionDeclaration
{
    ///<summary>Under the plugin's id: <c>treeality.mqtt.command.&lt;key&gt;</c>. An id keeps its meaning.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; }

    ///<summary>Whose it is, so absence can be said by name.</summary>
    [JsonPropertyName("plugin")]
    public string Plugin { get; set; }

    [JsonPropertyName("pluginName")]
    public string PluginName { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("inputs")]
    public List<ActionInput> Inputs { get; set; } = new();

    [JsonPropertyName("returns")]
    public List<ActionOutput> Returns { get; set; } = new();

    ///<summary>The member's own permission, which running takes beside the coordinator's (rule 6).</summary>
    [JsonPropertyName("permission")]
    public string Permission { get; set; }

    [JsonPropertyName("physical")]
    public bool Physical { get; set; }

    ///<summary>Why a declaration cannot stand, or null: an id under a plugin, a name, and a permission.</summary>
    public static string Refusal(ActionDeclaration declaration)
    {
        if (declaration == null)
            return "There is no action here.";

        if (string.IsNullOrWhiteSpace(declaration.Plugin))
            return "An action is a plugin's: it names whose it is.";

        if (string.IsNullOrWhiteSpace(declaration.Id) || !declaration.Id.StartsWith(declaration.Plugin + ".", StringComparison.Ordinal))
            return $"An action's id is under its plugin's: {declaration.Plugin}.<name>.";

        if (string.IsNullOrWhiteSpace(declaration.Name))
            return "An action needs a name: it is what the operator chooses.";

        if (string.IsNullOrWhiteSpace(declaration.Permission))
            return "An action names the permission running it takes.";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in declaration.Inputs ?? [])
        {
            if (input == null || string.IsNullOrWhiteSpace(input.Name))
                return $"{declaration.Name}: every input has a name.";

            if (!names.Add(input.Name.Trim()))
                return $"{declaration.Name} takes '{input.Name.Trim()}' twice.";
        }

        return null;
    }

    ///<summary>
    ///Why these values cannot be this action's inputs, or null: every required one given, and each of a kind the
    ///input declares. What a coordinator asks when an operation is configured, and the broker asks again when a
    ///run starts.
    ///</summary>
    public static string InputsRefusal(ActionDeclaration declaration, IReadOnlyDictionary<string, string> values)
    {
        foreach (var input in declaration?.Inputs ?? [])
        {
            string value = null;

            if (values != null && values.TryGetValue(input.Name ?? "", out var given))
                value = given;

            if (string.IsNullOrWhiteSpace(value))
                value = input.Default;

            if (string.IsNullOrWhiteSpace(value))
            {
                if (input.Required)
                    return $"{declaration.Name} needs {input.Name}.";

                continue;
            }

            var kind = (input.Kind ?? "text").Trim().ToLowerInvariant();

            if (kind == "number" && !decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out _))
                return $"{input.Name} is a number, with a decimal point.";

            if (kind == "flag" && value.Trim().ToLowerInvariant() is not ("true" or "false"))
                return $"{input.Name} is true or false.";

            if (kind == "date" && !DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return $"{input.Name} is a day, written like 2026-09-30.";

            if (kind.StartsWith("choice:", StringComparison.Ordinal) && !input.Choices.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                return $"{input.Name} is one of {string.Join(", ", input.Choices)}.";
        }

        return null;
    }
}

///<summary>The actions a caller may run, from installed members.</summary>
public sealed class ActionsAnswer
{
    [JsonPropertyName("items")]
    public List<ActionDeclaration> Items { get; set; } = new();

    [JsonPropertyName("error")]
    public string Error { get; set; }
}

public sealed class ActionTicketAnswer
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; }
}

///<summary>What starts a run: the ticket the request carries, the inputs, and what the run is for.</summary>
public sealed class ActionStartRequest
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; }

    [JsonPropertyName("inputs")]
    public Dictionary<string, string> Inputs { get; set; } = new();

    ///<summary>The vertex the run is for — a build — by its key and label, so a member's record can point at it (rule 3).</summary>
    [JsonPropertyName("forKey")]
    public string ForKey { get; set; }

    [JsonPropertyName("forLabel")]
    public string ForLabel { get; set; }

    ///<summary>
    ///The vertex the run <strong>happens at</strong> — a <c>Location</c> — so a member can resolve what it needs
    ///from where the work is (Instruments criterion 24).
    ///
    ///<strong>The coordinator says where; the member decides what a place means to it.</strong> Manufacturing
    ///sends the workstation its step is being worked at, which is Organization's <c>Location</c> and not a word
    ///of Instruments'; Instruments finds the bench bound there. A member that needs none of this ignores it,
    ///as MQTT does. <c>at</c> is the name Organization already uses for being somewhere.
    ///</summary>
    [JsonPropertyName("atKey")]
    public string AtKey { get; set; }

    [JsonPropertyName("atLabel")]
    public string AtLabel { get; set; }
}

public sealed class ActionStartAnswer
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; }
}

///<summary>One moment of a run, in a sentence.</summary>
public sealed class ActionEvent
{
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("said")]
    public string Said { get; set; }
}

///<summary>A run as it stands: who started it and when, how it stands, and once it has ended what it concluded and what it wrote.</summary>
public sealed class ActionRunView
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("plugin")]
    public string Plugin { get; set; }

    [JsonPropertyName("by")]
    public string By { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("endedAt")]
    public DateTimeOffset? EndedAt { get; set; }

    ///<summary>One of <see cref="ActionStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }

    ///<summary>One of <see cref="ActionVerdicts"/>, once the run has ended.</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    ///<summary>The keys of the graph records the run wrote, which is how a coordinator finds what a member recorded.</summary>
    [JsonPropertyName("wrote")]
    public List<string> Wrote { get; set; } = new();

    [JsonPropertyName("forKey")]
    public string ForKey { get; set; }

    [JsonPropertyName("forLabel")]
    public string ForLabel { get; set; }

    ///<summary>Where it happened, beside what it was for. A run that says one and not the other is a trap.</summary>
    [JsonPropertyName("atKey")]
    public string AtKey { get; set; }

    [JsonPropertyName("atLabel")]
    public string AtLabel { get; set; }

    [JsonPropertyName("events")]
    public List<ActionEvent> Events { get; set; } = new();

    [JsonPropertyName("error")]
    public string Error { get; set; }
}
