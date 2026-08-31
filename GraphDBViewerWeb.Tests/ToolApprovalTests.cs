using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GraphDBViewerWeb.Code;

namespace GraphDBViewerWeb.Tests;

//How far the model is allowed to go on its own. Three rungs, and what separates them is worth pinning
//down twice over: which tools it is even shown, and what happens when it calls one anyway. The rung that
//lets it change data is the reason these are tests and not a comment.
public class ToolApprovalTests
{
    private sealed class RecordingRunner : ILlmToolRunner, IWriteCapableToolRunner
    {
        public bool AllowWrites { get; set; }

        public IReadOnlyList<LlmTool> Tools
        {
            get
            {
                var tools = new List<LlmTool> { new LlmTool { Name = GremlinToolRunner.ReadToolName } };

                if (AllowWrites)
                    tools.Add(new LlmTool { Name = GremlinToolRunner.WriteToolName });

                return tools;
            }
        }

        public List<string> Ran { get; } = new();

        public Task<string> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Ran.Add(name);

            return Task.FromResult("[]");
        }
    }

    private const string OneCall = "{\"query\":\"g.V().limit(1)\"}";
    private const string OneWrite = "{\"query\":\"g.addV('Product')\"}";

    //Every request the approver was shown, so a test can tell "was not asked" from "was asked and said yes".
    private sealed class Approvals
    {
        public List<ToolRunRequest> Seen { get; } = new();

        public Task<bool> Approve(ToolRunRequest request, CancellationToken ct)
        {
            Seen.Add(request);

            return Task.FromResult(true);
        }
    }

    private static ApprovingToolRunner Wrap(RecordingRunner inner, Approvals approvals, ToolApprovalMode mode)
    {
        return new ApprovingToolRunner(inner, approvals.Approve, null, mode);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Ask, false, true)]
    [InlineData(ToolApprovalMode.Ask, true, true)]
    [InlineData(ToolApprovalMode.AutoRead, false, false)]
    [InlineData(ToolApprovalMode.AutoRead, true, true)]
    [InlineData(ToolApprovalMode.AutoReadWrite, false, false)]
    [InlineData(ToolApprovalMode.AutoReadWrite, true, false)]
    public void EachRungSaysWhatStillHasToBeAsked(ToolApprovalMode mode, bool isWrite, bool expected)
    {
        Assert.Equal(expected, ApprovingToolRunner.NeedsApproval(mode, isWrite));
    }

    //The write tool is offered on every rung. Withholding it did not make anything safer — the model
    //simply answered that it was unable to make changes and stopped, when the useful thing is to propose
    //the change and let somebody approve it. The mode governs what runs unattended, not what may be asked.
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    [InlineData(ToolApprovalMode.AutoReadWrite)]
    public void TheWriteToolIsOfferedOnEveryRung(ToolApprovalMode mode)
    {
        var inner = new RecordingRunner();
        var runner = Wrap(inner, new Approvals(), mode);

        var names = runner.Tools.Select(t => t.Name).ToList();

        Assert.Contains(GremlinToolRunner.ReadToolName, names);
        Assert.Contains(GremlinToolRunner.WriteToolName, names);

        //And the runner holding the database was told to offer them, which is the only thing that does.
        Assert.True(inner.AllowWrites);
    }

    [Fact]
    public async Task AskingStillStopsForAPerson()
    {
        var inner = new RecordingRunner();
        var approvals = new Approvals();

        await Wrap(inner, approvals, ToolApprovalMode.Ask).RunToolAsync(GremlinToolRunner.ReadToolName, OneCall, CancellationToken.None);

        var request = Assert.Single(approvals.Seen);

        Assert.False(request.AutoApproved);
        Assert.Equal(GremlinToolRunner.ReadToolName, Assert.Single(inner.Ran));
    }

    //Auto is not silent. The call is still handed to the panel — that is how it reaches the transcript —
    //but it arrives already answered, so nothing waits on a button.
    [Fact]
    public async Task AutoReadRunsWithoutWaitingButIsStillShown()
    {
        var inner = new RecordingRunner();
        var approvals = new Approvals();

        var answer = await Wrap(inner, approvals, ToolApprovalMode.AutoRead)
            .RunToolAsync(GremlinToolRunner.ReadToolName, OneCall, CancellationToken.None);

        var request = Assert.Single(approvals.Seen);

        Assert.True(request.AutoApproved);
        Assert.False(request.IsWrite);
        Assert.Equal("[]", answer);
        Assert.Equal(GremlinToolRunner.ReadToolName, Assert.Single(inner.Ran));
    }

    [Fact]
    public async Task AutoReadWriteRunsAWriteWithoutWaiting()
    {
        var inner = new RecordingRunner();
        var approvals = new Approvals();

        var answer = await Wrap(inner, approvals, ToolApprovalMode.AutoReadWrite)
            .RunToolAsync(GremlinToolRunner.WriteToolName, OneWrite, CancellationToken.None);

        var request = Assert.Single(approvals.Seen);

        Assert.True(request.AutoApproved);
        Assert.True(request.IsWrite);
        Assert.Equal("[]", answer);
        Assert.Equal(GremlinToolRunner.WriteToolName, Assert.Single(inner.Ran));
    }

    //Below the top rung a write is a question, not a refusal: it reaches a person, and it does not reach
    //the database until they say so.
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    public async Task AWriteBelowTheTopRungIsPutToAPerson(ToolApprovalMode mode)
    {
        var inner = new RecordingRunner();
        var approvals = new Approvals();

        await Wrap(inner, approvals, mode).RunToolAsync(GremlinToolRunner.WriteToolName, OneWrite, CancellationToken.None);

        var request = Assert.Single(approvals.Seen);

        Assert.True(request.IsWrite);
        Assert.False(request.AutoApproved);
        Assert.Equal(GremlinToolRunner.WriteToolName, Assert.Single(inner.Ran));
    }

    //And when they say no, nothing runs — the same answer a declined read gets.
    [Fact]
    public async Task ADeclinedWriteTouchesNothing()
    {
        var inner = new RecordingRunner();
        var runner = new ApprovingToolRunner(inner, (r, ct) => Task.FromResult(false), null, ToolApprovalMode.AutoRead);

        var answer = await runner.RunToolAsync(GremlinToolRunner.WriteToolName, OneWrite, CancellationToken.None);

        Assert.Equal(ApprovingToolRunner.DeclinedMessage, answer);
        Assert.Empty(inner.Ran);
    }

    //Auto mode is about not asking, not about running unattended with nowhere to show it. No approver is
    //still a misconfiguration, and refusing is still the safe reading of one.
    [Fact]
    public async Task AutoModeWithNoApproverStillRefuses()
    {
        var inner = new RecordingRunner();
        var runner = new ApprovingToolRunner(inner, null, null, ToolApprovalMode.AutoReadWrite);

        Assert.Equal(ApprovingToolRunner.DeclinedMessage, await runner.RunToolAsync(GremlinToolRunner.ReadToolName, OneCall, CancellationToken.None));
        Assert.Empty(inner.Ran);
    }

    //An inner runner that knows nothing about writes must not be broken by being told about them.
    private sealed class ReadOnlyRunner : ILlmToolRunner
    {
        public IReadOnlyList<LlmTool> Tools { get; } = new List<LlmTool> { new LlmTool { Name = GremlinToolRunner.ReadToolName } };

        public Task<string> RunToolAsync(string name, string argumentsJson, CancellationToken ct)
        {
            return Task.FromResult("[]");
        }
    }

    [Fact]
    public void ARunnerWithNoWriteToolIsLeftAlone()
    {
        var runner = new ApprovingToolRunner(new ReadOnlyRunner(), (r, ct) => Task.FromResult(true), null, ToolApprovalMode.AutoReadWrite);

        Assert.Equal(GremlinToolRunner.ReadToolName, Assert.Single(runner.Tools).Name);
    }

    //Below: the runner that actually holds the database, checked on its own. The gate above can be wired
    //up wrong; this is the one standing on the data.
    private sealed class FakeGraphDb : IGraphDb
    {
        public string LastQuery { get; private set; }
        public int Calls { get; private set; }

        public Task<GraphDbResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            Calls++;

            return Task.FromResult(GraphDbResult.Success(JsonDocument.Parse("[]").RootElement));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void TheWriteToolIsNotEvenListedUntilItIsTurnedOn()
    {
        var runner = new GremlinToolRunner(new FakeGraphDb());

        Assert.Equal(GremlinToolRunner.ReadToolName, Assert.Single(runner.Tools).Name);

        runner.AllowWrites = true;

        Assert.Contains(runner.Tools, t => t.Name == GremlinToolRunner.WriteToolName);
    }

    [Fact]
    public async Task AWriteCallOnARunnerThatIsNotAllowedToWriteTouchesNothing()
    {
        var db = new FakeGraphDb();
        var runner = new GremlinToolRunner(db);

        var answer = await runner.RunToolAsync(GremlinToolRunner.WriteToolName, OneWrite, default);

        Assert.Contains("turned off", answer);
        Assert.Equal(0, db.Calls);
    }

    [Fact]
    public async Task AWriteRunsOnceWritingIsOn()
    {
        var db = new FakeGraphDb();
        var runner = new GremlinToolRunner(db) { AllowWrites = true };

        await runner.RunToolAsync(GremlinToolRunner.WriteToolName, OneWrite, default);

        Assert.Equal("g.addV('Product')", db.LastQuery);
    }

    //Turning writes on opens the write tool, not the read one: the read tool's whole safety is its guard.
    [Fact]
    public async Task TheReadToolKeepsRefusingMutationsEvenWithWritesOn()
    {
        var db = new FakeGraphDb();
        var runner = new GremlinToolRunner(db) { AllowWrites = true };

        var answer = await runner.RunToolAsync(GremlinToolRunner.ReadToolName, OneWrite, default);

        Assert.Contains("not allowed", answer);
        Assert.Equal(0, db.Calls);
    }

    //Named on every rung, and told each time whether calling it will stop for somebody. A model that
    //believes it has no way to write says so and gives up, which is the failure this replaced.
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    [InlineData(ToolApprovalMode.AutoReadWrite)]
    public void TheChatPromptAlwaysNamesTheWriteTool(ToolApprovalMode mode)
    {
        var prompt = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true, mode);

        Assert.Contains(GremlinToolRunner.WriteToolName, prompt);
    }

    //An exact-match lookup that comes back empty is the normal case, not a dead end: the graph stores
    //"Los Angeles" and the user typed "LA". Left to itself the model reported the graph had neither and
    //asked whether to try a different capitalization — handing back the looking it was holding the tool for.
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    [InlineData(ToolApprovalMode.AutoReadWrite)]
    public void TheChatPromptTellsItToDigWhenALookupComesBackEmpty(ToolApprovalMode mode)
    {
        var prompt = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true, mode);

        Assert.Contains("An empty result is a lead, not an answer", prompt);
        Assert.Contains("other capitalizations", prompt);
        Assert.Contains("Los Angeles", prompt);
        Assert.Contains("yourself rather than asking the user", prompt);
    }

    //One step crosses one edge. "A route from Portland to LA" written as a single out-step came back
    //empty against a graph that does have the route — through San Francisco — because the query had
    //asked whether the two are adjacent. Advice about writing a query, so both prompts carry it.
    [Fact]
    public void BothPromptsSayThatAPathIsRarelyOneHop()
    {
        var chat = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true);
        var oneShot = NlQueryPrompt.BuildSystemPrompt("gremlin", null);

        foreach (var prompt in new[] { chat, oneShot })
        {
            Assert.Contains("rarely one hop", prompt);
            Assert.Contains("repeating traversal", prompt);
            Assert.Contains("follow them either way", prompt);
        }
    }

    //And it is advice about using a tool, so it is not given to a model that has none.
    [Fact]
    public void ThatAdviceIsAbsentWhenThereAreNoTools()
    {
        var prompt = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, false);

        Assert.DoesNotContain("An empty result is a lead", prompt);
    }

    //And on the rungs where a write waits, it is told to ask by calling rather than to beg off.
    [Theory]
    [InlineData(ToolApprovalMode.Ask)]
    [InlineData(ToolApprovalMode.AutoRead)]
    public void TheChatPromptTellsItToProposeWritesRatherThanRefuseThem(ToolApprovalMode mode)
    {
        var prompt = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true, mode);

        Assert.Contains("never answer that you are unable to make changes", prompt);
    }

    //A model told its calls are vetted writes as if nothing can land; one told nothing is watched writes
    //as if everything is free. Neither is true in the other's mode, so the prompt has to differ.
    [Fact]
    public void TheChatPromptSaysWhetherAnybodyIsWatching()
    {
        var ask = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true, ToolApprovalMode.Ask);
        var autoRead = NlQueryPrompt.BuildChatSystemPrompt("gremlin", null, true, ToolApprovalMode.AutoRead);

        Assert.Contains("read call is shown to the user and run only if they approve", ask);
        Assert.DoesNotContain("read call is shown to the user and run only if they approve", autoRead);
        Assert.Contains("Read calls run immediately", autoRead);
    }
}
