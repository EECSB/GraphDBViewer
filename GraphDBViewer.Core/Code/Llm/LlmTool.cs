using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GraphDBViewerWeb.Code;

///<summary>A tool the model may call during generation, described in a provider-agnostic way.</summary>
public class LlmTool
{
    public string Name { get; set; }
    public string Description { get; set; }

    ///<summary>JSON Schema (as a JSON string) for the tool's input object, e.g. {"type":"object","properties":{...}}.</summary>
    public string InputSchemaJson { get; set; }

    ///<summary>Whether calling it changes the graph rather than only reading it: what waits for a person on the middle rung.</summary>
    public bool Changes { get; set; }

    ///<summary>
    ///Whether it only hands the person something to use (2026-11-02, a graph drawn from text): the call reads nothing and
    ///changes nothing, and what it hands over is used only by the person's own press on its card. So it never waits on the
    ///rung, since there is nothing in the call to approve; the press is the approval.
    ///</summary>
    public bool Proposes { get; set; }
}

///<summary>A tool invocation the model requested — the id is echoed back alongside the result.</summary>
public class LlmToolCall
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string ArgumentsJson { get; set; }
}

///<summary>
///What a tool answered: the text the model reads, and whether the call was refused or failed.
///
///<strong>A refusal is said as one</strong> (2026-10-14, from GDSViewer's port of this panel): Anthropic's
///<c>tool_result</c> carries an <c>is_error</c> flag, and a model told that a call failed tries another way rather
///than building on the error's text as if it were data. The OpenAI-compatible family has no flag, and is told in
///words. Until then every answer went back as a plain result.
///</summary>
public sealed class LlmToolOutput
{
    public string Text { get; init; } = "";

    ///<summary>Whether the call was refused or failed, with <see cref="Text"/> saying why.</summary>
    public bool IsError { get; init; }

    ///<summary>
    ///Whether a person declined the call, so it was not run (<see cref="ApprovingToolRunner.DeclinedMessage"/>). Not an
    ///error to the model, which is asked not to try another way; but no progress either, which <see cref="ToolProgress"/>
    ///could not tell from the words alone until 2026-11-07, so a run declined every round was never stopped as stuck.
    ///</summary>
    public bool IsDeclined { get; init; }

    ///<summary>
    ///A PNG for the model to look at, as base64, or null for an answer in words alone (2026-11-01, from GDS Viewer):
    ///Anthropic takes it inside the tool's result, the OpenAI-compatible family in a user turn after it.
    ///</summary>
    public string Png { get; init; }

    ///<summary>What the call returned.</summary>
    public static LlmToolOutput Result(string text)
    {
        return new LlmToolOutput { Text = text ?? "" };
    }

    ///<summary>A call that was refused or failed, and why.</summary>
    public static LlmToolOutput Failure(string why)
    {
        return new LlmToolOutput { Text = why ?? "", IsError = true };
    }

    ///<summary>A call a person declined: a result to the model, and no progress to the run.</summary>
    public static LlmToolOutput Declined(string text)
    {
        return new LlmToolOutput { Text = text ?? "", IsDeclined = true };
    }
}

///<summary>
///Executes the tools the model calls. Implemented by the app (e.g. running a read-only query against the
///connected database) and passed into a tool-using provider so the LLM code stays free of DB dependencies.
///</summary>
public interface ILlmToolRunner
{
    ///<summary>The tools exposed to the model.</summary>
    IReadOnlyList<LlmTool> Tools { get; }

    ///<summary>Runs a tool by name and says what came of it: its result, or why it was refused or failed.</summary>
    Task<LlmToolOutput> RunToolAsync(string name, string argumentsJson, CancellationToken ct);
}

///<summary>
///A tool runner that owns tools which change the data, and can be told whether to offer them.
///
///Off is the default and stays off unless something deliberately turns it on, so a caller that knows
///nothing about writes cannot hand the model one by accident. <see cref="ApprovingToolRunner"/> is what
///turns it on, from the mode a person chose.
///</summary>
public interface IWriteCapableToolRunner
{
    ///<summary>Whether write tools are offered to the model and honored when it calls one.</summary>
    bool AllowWrites { get; set; }
}
