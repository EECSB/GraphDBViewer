using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///How much the model is allowed to do to the connected graph <em>on its own</em>.
///
///A ladder, not a set of switches: each rung includes the one below it. What moves between rungs is what
///still stops for a person, never what the model may propose — it can always ask to write, and on every
///rung but the top one that ask becomes a card somebody has to answer.
///</summary>
public enum ToolApprovalMode
{
    ///<summary>Nothing runs until a person says so, reads included. One card at a time.</summary>
    Ask,

    ///<summary>Reads run the moment the model asks. A write still waits for a person.</summary>
    AutoRead,

    ///<summary>Reads and writes both run unattended, so the model can change the graph without asking.</summary>
    AutoReadWrite
}

///<summary>What the model asked to run, in the form a person is asked to approve.</summary>
public sealed class ToolRunRequest
{
    public ToolRunRequest(string toolName, string argumentsJson) : this(toolName, argumentsJson, ToolApprovalMode.Ask)
    {
    }

    public ToolRunRequest(string toolName, string argumentsJson, ToolApprovalMode mode) : this(toolName, argumentsJson, mode, false, false)
    {
    }

    ///<param name="changes">Whether the tool changes the graph (<see cref="LlmTool.Changes"/>), which is what waits for a person on the middle rung.</param>
    ///<param name="proposes">Whether the tool only hands the person something to use (<see cref="LlmTool.Proposes"/>), which never waits.</param>
    public ToolRunRequest(string toolName, string argumentsJson, ToolApprovalMode mode, bool changes, bool proposes)
    {
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        Query = QueryToolRunner.ExtractQuery(argumentsJson);
        IsWrite = changes;
        Proposes = proposes;
        AutoApproved = proposes || !ApprovingToolRunner.NeedsApproval(mode, IsWrite);
    }

    public string ToolName { get; }

    public string ArgumentsJson { get; }

    ///<summary>The query the call carries, or null for a tool that is not one. This is what is shown for approval.</summary>
    public string Query { get; }

    ///<summary>
    ///Whether this call changes the graph rather than only reading it: the tool's own word for itself
    ///(<see cref="LlmTool.Changes"/>), so a plugin's tool marked as changing waits as the write query tool does. Until
    ///2026-11-07 the name run_write_query decided, and a plugin's change ran unasked on the middle rung.
    ///</summary>
    public bool IsWrite { get; }

    ///<summary>Whether the call only hands the person something to use, so it went ahead because there was nothing to approve rather than because of the rung.</summary>
    public bool Proposes { get; }

    ///<summary>
    ///Whether the mode in force has already answered this. It is still shown — a call that runs unwatched
    ///is exactly the one worth seeing — but it is shown as something that happened, not something to decide.
    ///</summary>
    public bool AutoApproved { get; }
}

///<summary>
///Stands between the model and the database and decides, per call, whether it runs now or waits.
///
///It offers every tool the runner has, writes included, on every rung. Withholding the write tool was
///worse than gating it: the model would answer that it is unable to make changes and stop there, when
///the useful thing is to propose the change and let somebody approve it. So the mode decides only what
///runs unattended, and everything else becomes a card.
///
///A wrapper rather than a flag inside <see cref="QueryToolRunner"/>, and nothing in either provider
///changed: the tool loop already awaits <see cref="ILlmToolRunner.RunToolAsync"/>, so waiting on a person
///is just that await taking longer.
///</summary>
public sealed class ApprovingToolRunner : ILlmToolRunner
{
    ///<summary>
    ///What the model is told when a person declines. Phrased as a result, since that is what it is, and not marked as
    ///an error when refusals began to be (2026-10-14): a model told a call failed tries another way to the same end,
    ///and this one is asked not to. Marked as declined for the run's own count (<see cref="LlmToolOutput.IsDeclined"/>),
    ///since a call that did not run is not progress either.
    ///</summary>
    public const string DeclinedMessage = "The user did not approve running that query, so it was not run. Do not run it again. Answer with what you already know, or ask what to do instead.";

    private readonly ILlmToolRunner _inner;
    private readonly Func<ToolRunRequest, CancellationToken, Task<bool>> _approve;
    private readonly Func<ToolRunRequest, LlmToolOutput, Task> _completed;
    private readonly Func<ToolApprovalMode> _mode;

    ///<param name="approve">
    ///Shows the call and answers it. A request whose <see cref="ToolRunRequest.AutoApproved"/> is set has
    ///been answered by the mode already and should be shown, not waited on. Awaited otherwise, so it may
    ///take as long as the person does.
    ///</param>
    ///<param name="completed">Told what the tool answered, or null when it was declined, so the same card can show it. Optional.</param>
    public ApprovingToolRunner(
        ILlmToolRunner inner,
        Func<ToolRunRequest, CancellationToken, Task<bool>> approve,
        Func<ToolRunRequest, LlmToolOutput, Task> completed = null,
        ToolApprovalMode mode = ToolApprovalMode.Ask)
        : this(inner, approve, completed, () => mode)
    {
    }

    ///<param name="mode">
    ///Asked for every call rather than once, so a rung chosen while a run is going is the one its next call meets
    ///(2026-11-01, from GDS Viewer). Read once, a switch to auto-run went on asking until the answer was done.
    ///</param>
    public ApprovingToolRunner(
        ILlmToolRunner inner,
        Func<ToolRunRequest, CancellationToken, Task<bool>> approve,
        Func<ToolRunRequest, LlmToolOutput, Task> completed,
        Func<ToolApprovalMode> mode)
    {
        _inner = inner;
        _approve = approve;
        _completed = completed;
        _mode = mode;

        //Write tools stay holstered until something asks for them, and this is the something: a runner
        //reached through an approval gate can offer them, because the gate is what makes them safe.
        if (_inner is IWriteCapableToolRunner writable)
            writable.AllowWrites = true;
    }

    ///<summary>The rung in force now, which is what the next call is put to.</summary>
    public ToolApprovalMode Mode => _mode();

    ///<summary>Whether a call of this kind still has to be put to a person.</summary>
    public static bool NeedsApproval(ToolApprovalMode mode, bool isWrite)
    {
        if (mode == ToolApprovalMode.AutoReadWrite)
            return false;

        if (mode == ToolApprovalMode.AutoRead && !isWrite)
            return false;

        return true;
    }

    ///<summary>Every tool the runner has. What the mode changes is which of them stop for a person.</summary>
    public IReadOnlyList<LlmTool> Tools
    {
        get { return _inner.Tools; }
    }

    public async Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        //The tool's own description says whether the call changes anything or only proposes; one the runner does not
        //list is a read, and the runner's to refuse.
        var tool = _inner.Tools.FirstOrDefault(each => each.Name == name);
        var changes = tool != null && tool.Changes;
        var proposes = tool != null && tool.Proposes;
        var request = new ToolRunRequest(name, argumentsJson, Mode, changes, proposes);

        //No approver is a misconfiguration, not permission. Refusing is the safe reading of it — including
        //in the auto modes, where the approver is also how what ran gets shown.
        if (_approve == null)
            return LlmToolOutput.Declined(DeclinedMessage);

        if (!await _approve(request, ct))
        {
            if (_completed != null)
                await _completed(request, null);

            return LlmToolOutput.Declined(DeclinedMessage);
        }

        var output = await _inner.RunToolAsync(name, argumentsJson, ct);

        if (_completed != null)
            await _completed(request, output);

        return output;
    }
}
