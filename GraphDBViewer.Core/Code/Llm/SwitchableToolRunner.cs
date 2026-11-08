using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>
///The tools of a runner less the ones switched off in Ask AI's tool picker (2026-11-01, GDS Viewer's, which had it from
///ConfigurationRuleEditor's context picker).
///
///<strong>Switched off means not offered, refused, and said.</strong> The tool is left out of what the model is offered; a
///call to it anyway is refused in words, since the model can see the tool in what the conversation says it did; and the
///system prompt names it (<see cref="NlQueryPrompt.BuildChatSystemPrompt"/>). Withholding a tool without a word is what
///made this panel's model answer that it could not make changes, and stop.
///</summary>
public sealed class SwitchableToolRunner : ILlmToolRunner, IWriteCapableToolRunner
{
    private readonly ILlmToolRunner _inner;
    private readonly IReadOnlyCollection<string> _off;

    ///<param name="off">The tools switched off, read at every call, so a change in the picker holds from the next one.</param>
    public SwitchableToolRunner(ILlmToolRunner inner, IReadOnlyCollection<string> off)
    {
        _inner = inner;
        _off = off;
    }

    ///<summary>What the model is told when it calls a tool that was switched off.</summary>
    public static string Refusal(string name)
    {
        return $"{name} is switched off in this conversation: the user turned it off in the window's tool picker. Say what you would have used it for, and that it can be switched back on there.";
    }

    public IReadOnlyList<LlmTool> Tools => _inner.Tools.Where(tool => !_off.Contains(tool.Name)).ToList();

    ///<summary>Passed on, so the approval gate that turns the write tool on reaches the runner that holds it.</summary>
    public bool AllowWrites
    {
        get
        {
            return _inner is IWriteCapableToolRunner writable && writable.AllowWrites;
        }
        set
        {
            if (_inner is IWriteCapableToolRunner writable)
                writable.AllowWrites = value;
        }
    }

    public Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
    {
        if (_off.Contains(name))
            return Task.FromResult(LlmToolOutput.Failure(Refusal(name)));

        return _inner.RunToolAsync(name, argumentsJson, ct);
    }
}
