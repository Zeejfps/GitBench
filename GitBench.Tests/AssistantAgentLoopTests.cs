using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using Xunit;

namespace GitBench.Tests;

public sealed class AssistantAgentLoopTests
{
    private static AgentDefinition Agent(params string[] tools) =>
        new("test", "You are a test agent.", tools, ModelTier.Chat);

    private static AssistantAgentLoop Loop(FakeAssistantBackend backend, params IAssistantTool[] tools)
    {
        var names = tools.Select(t => t.Name).ToArray();
        return new AssistantAgentLoop(backend, Agent(names), AssistantToolset.Create(tools, names));
    }

    [Fact]
    public async Task ParallelToolUses_AnswerInOneToolResultMessage()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "alpha", AssistantTestJson.Empty),
                new BackendEvent.ToolUse("call_2", "beta", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("all done"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

        var loop = Loop(backend, new StubTool("alpha"), new StubTool("beta"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);

        var toolResultMessages = conversation.OfType<AssistantMessage.ToolResults>().ToList();
        var single = Assert.Single(toolResultMessages);
        Assert.Equal(new[] { "call_1", "call_2" }, single.Results.Select(r => r.ToolUseId));
        Assert.Equal(new[] { "alpha-result", "beta-result" }, single.Results.Select(r => r.Content));
        Assert.All(single.Results, r => Assert.False(r.IsError));

        // The second request must carry that one message, not two.
        Assert.Equal(2, backend.Requests.Count);
        Assert.Single(backend.Requests[1].Messages.OfType<AssistantMessage.ToolResults>());

        Assert.Equal(2, events.OfType<AssistantEvent.ToolStarted>().Count());
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task FailingTool_BecomesAnErrorResultRatherThanEndingTheTurn()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "alpha", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[] { new BackendEvent.TurnComplete(StopReason.EndTurn) });

        var failing = new StubTool("alpha", _ => ToolInvocation.Error("not a repository"));
        var loop = Loop(backend, failing);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);

        var results = Assert.Single(conversation.OfType<AssistantMessage.ToolResults>());
        Assert.True(Assert.Single(results.Results).IsError);
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task UnknownTool_BecomesAnErrorResult()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "nope", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[] { new BackendEvent.TurnComplete(StopReason.EndTurn) });

        var loop = Loop(backend, new StubTool("alpha"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        await foreach (var _ in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None)) { }

        var results = Assert.Single(conversation.OfType<AssistantMessage.ToolResults>());
        Assert.True(Assert.Single(results.Results).IsError);
    }

    [Fact]
    public async Task Refusal_EndsTheTurnWithoutRecordingContent()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TextDelta("partial"),
            new BackendEvent.Refusal("cyber", "declined"),
        });

        var loop = Loop(backend, new StubTool("alpha"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);

        var refused = Assert.IsType<AssistantEvent.Refused>(events[^1]);
        Assert.Equal("cyber", refused.Category);
        Assert.Empty(conversation.OfType<AssistantMessage.Assistant>());
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task BackendError_EndsTheTurn()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.Error("overloaded", "overloaded_error"),
        });

        var loop = Loop(backend, new StubTool("alpha"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);

        var failed = Assert.IsType<AssistantEvent.Failed>(Assert.Single(events));
        Assert.Equal("overloaded", failed.Message);
    }

    [Fact]
    public async Task Cancellation_MidTurn_Propagates()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TextDelta("one"),
            new BackendEvent.TextDelta("two"),
            new BackendEvent.TextDelta("three"),
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });

        var loop = Loop(backend, new StubTool("alpha"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };
        using var cts = new CancellationTokenSource();

        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in loop.RunAsync(conversation, null, new FakeApprovals(), cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, seen);
        Assert.DoesNotContain(conversation, m => m is AssistantMessage.Assistant);
    }

    // One tool_use for a write tool, then a turn that answers — the shape every approval test needs.
    private static FakeAssistantBackend WriteThenAnswer(string tool, string args = "{}") =>
        new(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", tool, AssistantTestJson.Element(args)),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[]
            {
                new BackendEvent.TextDelta("done"),
                new BackendEvent.TurnComplete(StopReason.EndTurn),
            });

    [Fact]
    public async Task ApprovedWrite_RunsTheToolAndReportsItsResult()
    {
        var backend = WriteThenAnswer("stage_files", """{"paths":["a.txt"]}""");
        var write = new StubTool("stage_files") { IsWrite = true };
        var approvals = new FakeApprovals(approve: true);
        var loop = Loop(backend, write);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("stage it") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, approvals, CancellationToken.None))
            events.Add(e);

        Assert.Equal(new[] { "stage_files" }, approvals.Asked);
        // The card is asked with the arguments the tool will actually get, not a summary of them.
        Assert.Equal("""paths: ["a.txt"]""", Assert.Single(approvals.Arguments));
        Assert.Equal(1, write.Invocations);

        var result = Assert.Single(Assert.Single(conversation.OfType<AssistantMessage.ToolResults>()).Results);
        Assert.False(result.IsError);
        Assert.Equal("stage_files-result", result.Content);
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task DeniedWrite_ReturnsAnErrorResultAndTheTurnCarriesOn()
    {
        var backend = WriteThenAnswer("commit", """{"message":"wip"}""");
        var write = new StubTool("commit") { IsWrite = true };
        var loop = Loop(backend, write);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("commit it") };

        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(approve: false), CancellationToken.None))
            events.Add(e);

        Assert.Equal(0, write.Invocations);
        var result = Assert.Single(Assert.Single(conversation.OfType<AssistantMessage.ToolResults>()).Results);
        Assert.True(result.IsError);
        Assert.Contains("declined", result.Content, StringComparison.OrdinalIgnoreCase);

        // The model gets its say afterwards: a refusal is a turn that continues, not one that stops.
        Assert.Equal(2, backend.Requests.Count);
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
        // Nothing ran, so nothing is reported as having run.
        Assert.Empty(events.OfType<AssistantEvent.ToolStarted>());
    }

    [Fact]
    public async Task ReadsRunWithoutAsking_WritesNeverRunBeforeTheAnswer()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "get_status", AssistantTestJson.Empty),
                new BackendEvent.ToolUse("call_2", "stage_files", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            new BackendEvent[] { new BackendEvent.TurnComplete(StopReason.EndTurn) });

        var read = new StubTool("get_status");
        var write = new StubTool("stage_files") { IsWrite = true };
        // Asserts the ordering at the moment it matters: when the question is asked, the write has
        // not run, and the read beside it already has.
        var approvals = new FakeApprovals(_ =>
        {
            Assert.Equal(0, write.Invocations);
            Assert.Equal(1, read.Invocations);
            return true;
        });

        var loop = Loop(backend, read, write);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        await foreach (var _ in loop.RunAsync(conversation, null, approvals, CancellationToken.None)) { }

        Assert.Equal(new[] { "stage_files" }, approvals.Asked);
        Assert.Equal(1, write.Invocations);
    }

    [Fact]
    public async Task CancellingWhileAnApprovalIsPending_UnwindsWithoutRunningTheTool()
    {
        var backend = WriteThenAnswer("commit", """{"message":"wip"}""");
        var write = new StubTool("commit") { IsWrite = true };
        var loop = Loop(backend, write);
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("commit it") };

        using var cts = new CancellationTokenSource();
        var approvals = new BlockingApprovals(onAsked: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in loop.RunAsync(conversation, null, approvals, cts.Token)) { }
        });

        Assert.Equal(1, approvals.Asked);
        Assert.Equal(0, write.Invocations);
        // The abandoned round never became a tool-result message, so the model's view stays clean.
        Assert.Empty(conversation.OfType<AssistantMessage.ToolResults>());
        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task RepoContext_RidesInTheMessageListNotTheSystemPrompt()
    {
        var backend = new FakeAssistantBackend(new BackendEvent[]
        {
            new BackendEvent.TurnComplete(StopReason.EndTurn),
        });

        var loop = Loop(backend, new StubTool("alpha"));
        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };

        await foreach (var _ in loop.RunAsync(conversation, "branch: main", new FakeApprovals(), CancellationToken.None)) { }

        var request = Assert.Single(backend.Requests);
        Assert.DoesNotContain("branch: main", request.SystemPrompt);
        var context = Assert.Single(request.Messages.OfType<AssistantMessage.RepoContext>());
        Assert.Equal("branch: main", context.Text);
        // It has to follow a user message and cannot be the first entry.
        Assert.IsType<AssistantMessage.User>(request.Messages[0]);
    }

    // A model with no tool calling answers from nothing and looks exactly like one that had nothing
    // to look up, so where tool calling is not a given the loop says which it was.
    [Fact]
    public async Task AProviderThatNeverCallsATool_IsReportedOnceRatherThanLeftUnsaid()
    {
        var backend = new FakeAssistantBackend(
            Answer("no tools here"),
            Answer("still none"));

        var loop = Loop(backend, toolCallingIsUnproven: true, new StubTool("alpha"));
        var conversation = new List<AssistantMessage>();

        var first = await Run(loop, conversation, "go");
        Assert.Single(first.OfType<AssistantEvent.NoToolSupport>());
        Assert.IsType<AssistantEvent.Completed>(first[^1]);

        // Said once per conversation: the point is made, and repeating it every turn is noise.
        var second = await Run(loop, conversation, "again");
        Assert.Empty(second.OfType<AssistantEvent.NoToolSupport>());
    }

    [Fact]
    public async Task AProviderWhoseToolCallingIsKnownGood_SaysNothingWhenATurnNeedsNoTools()
    {
        var backend = new FakeAssistantBackend(Answer("nothing to look up"));

        var loop = Loop(backend, toolCallingIsUnproven: false, new StubTool("alpha"));
        var events = await Run(loop, new List<AssistantMessage>(), "go");

        Assert.Empty(events.OfType<AssistantEvent.NoToolSupport>());
    }

    // One demonstrated tool call settles the question for the conversation, so a later turn that
    // simply needs no tools is not reported as a broken provider.
    [Fact]
    public async Task ATurnThatCalledATool_SilencesTheReportForLaterTurns()
    {
        var backend = new FakeAssistantBackend(
            new BackendEvent[]
            {
                new BackendEvent.ToolUse("call_1", "alpha", AssistantTestJson.Empty),
                new BackendEvent.TurnComplete(StopReason.ToolUse),
            },
            Answer("used it"),
            Answer("nothing to look up"));

        var loop = Loop(backend, toolCallingIsUnproven: true, new StubTool("alpha"));
        var conversation = new List<AssistantMessage>();

        var first = await Run(loop, conversation, "go");
        Assert.Empty(first.OfType<AssistantEvent.NoToolSupport>());

        var second = await Run(loop, conversation, "again");
        Assert.Empty(second.OfType<AssistantEvent.NoToolSupport>());
    }

    private static BackendEvent[] Answer(string text) =>
    [
        new BackendEvent.TextDelta(text),
        new BackendEvent.TurnComplete(StopReason.EndTurn),
    ];

    private static AssistantAgentLoop Loop(
        FakeAssistantBackend backend,
        bool toolCallingIsUnproven,
        params IAssistantTool[] tools)
    {
        var names = tools.Select(t => t.Name).ToArray();
        return new AssistantAgentLoop(
            backend,
            Agent(names),
            AssistantToolset.Create(tools, names),
            () => toolCallingIsUnproven);
    }

    private static async Task<List<AssistantEvent>> Run(
        AssistantAgentLoop loop,
        List<AssistantMessage> conversation,
        string message)
    {
        conversation.Add(new AssistantMessage.User(message));
        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);
        return events;
    }
}
