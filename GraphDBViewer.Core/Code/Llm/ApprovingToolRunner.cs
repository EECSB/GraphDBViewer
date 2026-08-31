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

    public ToolRunRequest(string toolName, string argumentsJson, ToolApprovalMode mode)
    {
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        Query = GremlinToolRunner.ExtractQuery(argumentsJson);
        IsWrite = toolName == GremlinToolRunner.WriteToolName;
        AutoApproved = !ApprovingToolRunner.NeedsApproval(mode, IsWrite);
    }

    public string ToolName { get; }

    public string ArgumentsJson { get; }

    ///<summary>The query the call carries, or null for a tool that is not one. This is what is shown for approval.</summary>
    public string Query { get; }

    ///<summary>Whether this call changes the graph rather than only reading it.</summary>
    public bool IsWrite { get; }

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
///A wrapper rather than a flag inside <see cref="GremlinToolRunner"/>, and nothing in either provider
///changed: the tool loop already awaits <see cref="ILlmToolRunner.RunToolAsync"/>, so waiting on a person
///is just that await taking longer.
///</summary>
public sealed class ApprovingToolRunner : ILlmToolRunner
{
    ///<summary>What the model is told when a person declines. Phrased as a result, since that is what it is.</summary>
    public const string DeclinedMessage = "The user did not approve running that query, so it was not run. "
        + "Do not run it again. Answer with what you already know, or ask what to do instead.";

    private readonly ILlmToolRunner _inner;
    private readonly Func<ToolRunRequest, CancellationToken, Task<bool>> _approve;
    private readonly Func<ToolRunRequest, string, Task> _completed;

    ///<param name="approve">
    ///Shows the call and answers it. A request whose <see cref="ToolRunRequest.AutoApproved"/> is set has
    ///been answered by the mode already and should be shown, not waited on. Awaited otherwise, so it may
    ///take as long as the person does.
    ///</param>
    ///<param name="completed">Told what the tool returned, so the same conversation can show it. Optional.</param>
    public ApprovingToolRunner(
        ILlmToolRunner inner,
        Func<ToolRunRequest, CancellationToken, Task<bool>> approve,
        Func<ToolRunRequest, string, Task> completed = null,
        ToolApprovalMode mode = ToolApprovalMode.Ask)
    {
        _inner = inner;
        _approve = approve;
        _completed = completed;
        Mode = mode;

        //Write tools stay holstered until something asks for them, and this is the something: a runner
        //reached through an approval gate can offer them, because the gate is what makes them safe.
        if (_inner is IWriteCapableToolRunner writable)
            writable.AllowWrites = true;
    }

    public ToolApprovalMode Mode { get; }

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

    public async Task<string> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var request = new ToolRunRequest(name, argumentsJson, Mode);

        //No approver is a misconfiguration, not permission. Refusing is the safe reading of it — including
        //in the auto modes, where the approver is also how what ran gets shown.
        if (_approve == null)
            return DeclinedMessage;

        if (!await _approve(request, ct))
        {
            if (_completed != null)
                await _completed(request, null);

            return DeclinedMessage;
        }

        var result = await _inner.RunToolAsync(name, argumentsJson, ct);

        if (_completed != null)
            await _completed(request, result);

        return result;
    }
}
