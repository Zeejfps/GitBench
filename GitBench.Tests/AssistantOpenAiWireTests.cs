using System.Net;
using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Agents;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;
using Xunit;

namespace GitBench.Tests;

// The second wire format, exercised without a socket: what the writer renders, and what the reader
// makes of an event stream. The rules that differ from Anthropic's are the point — one tool-result
// message becomes several, tool arguments travel as a JSON-encoded string, and the stream ends on a
// sentinel rather than an event.
public sealed class AssistantOpenAiWireTests
{
    private static readonly AssistantConnection OpenAi = AssistantConnection.For(AssistantProviders.OpenAi);
    private static readonly AssistantConnection Ollama = AssistantConnection.For(AssistantProviders.Ollama);

    private static readonly AssistantMessage[] AToolExchange =
    {
        new AssistantMessage.User("what changed"),
        new AssistantMessage.Assistant(new AssistantContent[]
        {
            new AssistantContent.Text("Looking."),
            new AssistantContent.ToolUse("call_1", "get_status", AssistantTestJson.Element("""{"depth":2}""")),
            new AssistantContent.ToolUse("call_2", "get_branches", AssistantTestJson.Empty),
        }),
        new AssistantMessage.ToolResults(new[]
        {
            new AssistantToolResult("call_1", "clean", false),
            new AssistantToolResult("call_2", "boom", true),
        }),
    };

    private static byte[] Bytes(
        AssistantConnection connection,
        IReadOnlyList<AssistantMessage> messages,
        params IAssistantTool[] tools) =>
        OpenAiRequestWriter.Write(
            new AssistantTurn(ModelTier.Chat, "system prompt", messages), tools, connection);

    private static JsonDocument Parse(byte[] body) => JsonDocument.Parse(Encoding.UTF8.GetString(body));

    private static JsonDocument Body(
        AssistantConnection connection,
        ModelTier tier,
        IReadOnlyList<AssistantMessage> messages,
        params IAssistantTool[] tools) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(
            OpenAiRequestWriter.Write(new AssistantTurn(tier, "system prompt", messages), tools, connection)));

    [Fact]
    public void ToolResults_SplitIntoOneToolMessagePerResult()
    {
        using var wire = Body(OpenAi, ModelTier.Chat, AToolExchange);
        var messages = wire.RootElement.GetProperty("messages");

        // system, user, assistant, then one message per result.
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("clean", messages[3].GetProperty("content").GetString());
        Assert.Equal("tool", messages[4].GetProperty("role").GetString());
        Assert.Equal("call_2", messages[4].GetProperty("tool_call_id").GetString());
        // There is no is_error flag on this wire, so the failure says so where the model reads it.
        Assert.StartsWith("Error:", messages[4].GetProperty("content").GetString());
    }

    // The same conversation, rendered by both writers: the invariant lives in the writer, and the
    // conversation stays one logical message carrying a list.
    [Fact]
    public void TheSameConversation_StaysOneMessageForAnthropicAndSeveralForOpenAi()
    {
        var turn = new AssistantTurn(ModelTier.Chat, "system prompt", AToolExchange);

        using var anthropic = JsonDocument.Parse(Encoding.UTF8.GetString(
            AnthropicRequestWriter.Write(turn, Array.Empty<IAssistantTool>(), AssistantConnection.Default)));
        var anthropicResults = anthropic.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "user"
                        && m.GetProperty("content")[0].GetProperty("type").GetString() == "tool_result")
            .ToArray();
        Assert.Single(anthropicResults);
        Assert.Equal(2, anthropicResults[0].GetProperty("content").GetArrayLength());

        using var openAi = Body(OpenAi, ModelTier.Chat, AToolExchange);
        Assert.Equal(2, openAi.RootElement.GetProperty("messages").EnumerateArray()
            .Count(m => m.GetProperty("role").GetString() == "tool"));

        Assert.Single(AToolExchange.OfType<AssistantMessage.ToolResults>());
    }

    [Fact]
    public void AssistantToolCalls_CarryArgumentsAsAJsonEncodedString()
    {
        using var wire = Body(OpenAi, ModelTier.Chat, AToolExchange);
        var assistant = wire.RootElement.GetProperty("messages")[2];

        Assert.Equal("Looking.", assistant.GetProperty("content").GetString());
        var calls = assistant.GetProperty("tool_calls");
        Assert.Equal(2, calls.GetArrayLength());
        Assert.Equal("function", calls[0].GetProperty("type").GetString());
        Assert.Equal("get_status", calls[0].GetProperty("function").GetProperty("name").GetString());

        var arguments = calls[0].GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        using var decoded = JsonDocument.Parse(arguments.GetString()!);
        Assert.Equal(2, decoded.RootElement.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void SystemPromptHeadsTheMessageListAndToolsAreFunctionWrapped()
    {
        using var wire = Body(
            OpenAi,
            ModelTier.Chat,
            new AssistantMessage[] { new AssistantMessage.User("hi") },
            new StubTool("zeta"),
            new StubTool("alpha"));
        var root = wire.RootElement;

        var head = root.GetProperty("messages")[0];
        Assert.Equal("system", head.GetProperty("role").GetString());
        Assert.Equal("system prompt", head.GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("system", out _));

        var tools = root.GetProperty("tools");
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        // Sorted, as on the other wire: a stable tool list is worth having on both.
        Assert.Equal("alpha", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tools[0].GetProperty("function").GetProperty("parameters").ValueKind);
    }

    // There is no mid-conversation system entry here, so live repo state rides in the user turn on
    // every tier, not just the quick one.
    [Fact]
    public void RepoContext_IsAUserTurnOnEveryTier()
    {
        using var wire = Body(
            Ollama,
            ModelTier.Chat,
            new AssistantMessage[]
            {
                new AssistantMessage.User("hi"),
                new AssistantMessage.RepoContext("branch: main"),
            });

        var messages = wire.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("branch: main", messages[2].GetProperty("content").GetString());
        Assert.Single(messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "system"));
    }

    [Fact]
    public void TokenCap_UsesTheFieldAndCeilingTheProviderDeclares()
    {
        var messages = new AssistantMessage[] { new AssistantMessage.User("hi") };

        using var openAi = Body(OpenAi, ModelTier.Chat, messages);
        Assert.False(openAi.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(
            AssistantProviders.OpenAi.MaxOutputTokens,
            openAi.RootElement.GetProperty("max_completion_tokens").GetInt32());

        using var ollama = Body(Ollama, ModelTier.Chat, messages);
        Assert.Equal(AssistantProviders.Ollama.MaxOutputTokens, ollama.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(ollama.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    // The live bug: OpenAI's models reason by default and then refuse a request that also carries
    // function tools, so leaving reasoning_effort unsaid is what 400s — the body has to name the
    // opt-out. Endpoints that take reasoning alongside tools say nothing and keep theirs.
    [Fact]
    public void AToolBearingRequest_NamesTheProvidersReasoningOptOut()
    {
        var messages = new AssistantMessage[] { new AssistantMessage.User("what changed") };

        var withTools = Bytes(OpenAi, messages, new StubTool("get_status"));
        Assert.Equal("none", Parse(withTools).RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Contains("\"reasoning_effort\":\"none\"", Encoding.UTF8.GetString(withTools), StringComparison.Ordinal);

        // Nothing to be incompatible with, so nothing is said.
        var withoutTools = Bytes(OpenAi, messages);
        Assert.DoesNotContain("reasoning_effort", Encoding.UTF8.GetString(withoutTools), StringComparison.Ordinal);

        // A model that declares no opt-out carries none, tools or not.
        var local = Bytes(Ollama, messages, new StubTool("get_status"));
        Assert.DoesNotContain("reasoning_effort", Encoding.UTF8.GetString(local), StringComparison.Ordinal);
        Assert.Null(Ollama.Capabilities(ModelTier.Chat).ToolReasoningEffort);
    }

    [Fact]
    public void ModelOverride_AppliesToBothTiers()
    {
        var connection = AssistantConnection.For(AssistantProviders.Ollama, model: "qwen2.5-coder");
        var messages = new AssistantMessage[] { new AssistantMessage.User("hi") };

        using var chat = Body(connection, ModelTier.Chat, messages);
        using var quick = Body(connection, ModelTier.Quick, messages);

        Assert.Equal("qwen2.5-coder", chat.RootElement.GetProperty("model").GetString());
        Assert.Equal("qwen2.5-coder", quick.RootElement.GetProperty("model").GetString());
    }

    // ---- the read side ----

    private static async Task<List<BackendEvent>> Read(string stream)
    {
        using var reader = new StringReader(stream);
        var events = new List<BackendEvent>();
        await foreach (var backendEvent in OpenAiStreamReader.ReadAsync(reader, CancellationToken.None))
            events.Add(backendEvent);
        return events;
    }

    [Fact]
    public async Task ToolCallArguments_AreAssembledFromDeltasAndDecodedIntoRealJson()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_diff","arguments":"{\"path\":"}}]}}]}
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"src/a.cs\"}"}}]}}]}
            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}
            data: [DONE]

            """);

        var use = Assert.IsType<BackendEvent.ToolUse>(events[0]);
        Assert.Equal("call_1", use.Id);
        Assert.Equal("get_diff", use.Name);
        Assert.Equal(JsonValueKind.Object, use.Input.ValueKind);
        Assert.Equal("src/a.cs", use.Input.GetProperty("path").GetString());

        var complete = Assert.IsType<BackendEvent.TurnComplete>(events[1]);
        Assert.Equal(StopReason.ToolUse, complete.Reason);
    }

    [Fact]
    public async Task ParallelToolCalls_AreKeyedByIndexAndEmittedInOrder()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","function":{"name":"beta","arguments":"{}"}}]}}]}
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","function":{"name":"alpha","arguments":"{}"}}]}}]}
            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}
            data: [DONE]

            """);

        Assert.Equal(
            new[] { "call_a", "call_b" },
            events.OfType<BackendEvent.ToolUse>().Select(u => u.Id));
    }

    [Fact]
    public async Task DoneSentinel_EndsTheStreamAndWhateverFollowsIsIgnored()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"content":"hello"},"finish_reason":"stop"}]}
            data: [DONE]
            data: {"choices":[{"delta":{"content":" and more"}}]}

            """);

        Assert.Equal("hello", Assert.IsType<BackendEvent.TextDelta>(events[0]).Text);
        var complete = Assert.IsType<BackendEvent.TurnComplete>(Assert.Single(events.Skip(1)));
        Assert.Equal(StopReason.EndTurn, complete.Reason);
    }

    // A filtered turn arrives as HTTP 200 with empty or partial content, so it is settled from the
    // finish reason — and it is a refusal, not just another way for a turn to end.
    [Fact]
    public async Task ContentFilter_IsARefusalRatherThanACompletedTurn()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"content":"I ca"}}]}
            data: {"choices":[{"delta":{},"finish_reason":"content_filter"}]}
            data: [DONE]

            """);

        var refusal = Assert.IsType<BackendEvent.Refusal>(events[^1]);
        Assert.Equal("content_filter", refusal.Category);
        Assert.Empty(events.OfType<BackendEvent.TurnComplete>());
    }

    // Spelled as names because StopReason is internal and a theory's parameters are not.
    [Theory]
    [InlineData("stop", nameof(StopReason.EndTurn))]
    [InlineData("tool_calls", nameof(StopReason.ToolUse))]
    [InlineData("function_call", nameof(StopReason.ToolUse))]
    [InlineData("length", nameof(StopReason.MaxTokens))]
    [InlineData("something_new", nameof(StopReason.Other))]
    public async Task FinishReasons_MapOntoTheStopReasonsTheLoopKnows(string reason, string expectedName)
    {
        var expected = Enum.Parse<StopReason>(expectedName);
        var events = await Read(
            $$"""
              data: {"choices":[{"delta":{"content":"hi"},"finish_reason":"{{reason}}"}]}
              data: [DONE]

              """);

        Assert.Equal(expected, Assert.IsType<BackendEvent.TurnComplete>(events[^1]).Reason);
    }

    [Fact]
    public async Task ReasoningDeltas_ReportThinkingAndNeverItsContent()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"reasoning_content":"weighing options"}}]}
            data: {"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}
            data: [DONE]

            """);

        Assert.IsType<BackendEvent.Thinking>(events[0]);
        Assert.DoesNotContain(events.OfType<BackendEvent.TextDelta>(), d => d.Text.Contains("weighing"));
    }

    [Fact]
    public async Task AnErrorFrame_EndsTheStreamAsAnError()
    {
        var events = await Read(
            """
            data: {"error":{"type":"model_not_found","message":"no such model"}}
            data: {"choices":[{"delta":{"content":"never"}}]}

            """);

        var error = Assert.IsType<BackendEvent.Error>(Assert.Single(events));
        Assert.Equal("no such model", error.Message);
    }

    [Fact]
    public async Task AStreamThatJustStops_IsReportedRatherThanReadAsAFinishedTurn()
    {
        var events = await Read("\n");

        Assert.IsType<BackendEvent.Error>(Assert.Single(events));
    }

    // The reachable case: a close-delimited endpoint, or a gateway that gives up on its upstream and
    // ends the body cleanly. Framing cannot tell that from a finished turn, so only the terminators
    // can — and a half-written answer must not be committed to the conversation as a whole one.
    [Fact]
    public async Task AStreamThatStopsMidAnswer_IsAnErrorRatherThanACompletedTurn()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"content":"The change is "}}]}
            data: {"choices":[{"delta":{"content":"in src/a."}}]}

            """);

        Assert.Equal(
            new[] { "The change is ", "in src/a." },
            events.OfType<BackendEvent.TextDelta>().Select(d => d.Text));
        Assert.IsType<BackendEvent.Error>(events[^1]);
        Assert.Empty(events.OfType<BackendEvent.TurnComplete>());
    }

    // Either terminator alone settles the turn: endpoints that never send the sentinel are common,
    // and a finish reason is the model saying why it stopped.
    [Fact]
    public async Task AFinishReasonWithoutTheSentinel_StillCompletesTheTurn()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"content":"done"},"finish_reason":"stop"}]}

            """);

        Assert.Equal("done", Assert.IsType<BackendEvent.TextDelta>(events[0]).Text);
        Assert.Equal(StopReason.EndTurn, Assert.IsType<BackendEvent.TurnComplete>(events[^1]).Reason);
    }

    [Fact]
    public async Task TheSentinelWithoutAFinishReason_StillCompletesTheTurn()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"content":"done"}}]}
            data: [DONE]

            """);

        Assert.Equal(StopReason.EndTurn, Assert.IsType<BackendEvent.TurnComplete>(events[^1]).Reason);
    }

    // The deliberate trade: assembled tool calls are not enough to settle a turn on their own. A
    // stream that ends with neither terminator is reported, not salvaged into a tool-call turn.
    [Fact]
    public async Task ToolCallsWithNeitherTerminator_AreReportedRatherThanSalvaged()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_diff","arguments":"{}"}}]}}]}

            """);

        Assert.IsType<BackendEvent.Error>(Assert.Single(events));
        Assert.Empty(events.OfType<BackendEvent.ToolUse>());
    }

    // ...but the sentinel alone still is, which is the only way that branch is now reached.
    [Fact]
    public async Task ToolCallsEndingOnTheSentinelAlone_AreStillAToolCallTurn()
    {
        var events = await Read(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_diff","arguments":"{}"}}]}}]}
            data: [DONE]

            """);

        Assert.Equal("call_1", Assert.IsType<BackendEvent.ToolUse>(events[0]).Id);
        Assert.Equal(StopReason.ToolUse, Assert.IsType<BackendEvent.TurnComplete>(events[^1]).Reason);
    }

    // End to end through the loop: the OpenAI framing drives a real tool call and its result goes
    // back out as one tool message per result.
    [Fact]
    public async Task AToolCallOverTheOpenAiWire_RunsTheToolAndAnswersInToolMessages()
    {
        var backend = new FakeOpenAiBackend(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"alpha","arguments":"{}"}}]}}]}
            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}
            data: [DONE]

            """,
            """
            data: {"choices":[{"delta":{"content":"all done"},"finish_reason":"stop"}]}
            data: [DONE]

            """);

        var tool = new StubTool("alpha");
        var loop = new AssistantAgentLoop(
            backend,
            new AgentDefinition("test", "You are a test agent.", new[] { "alpha" }, ModelTier.Chat),
            AssistantToolset.Create(new IAssistantTool[] { tool }, new[] { "alpha" }));

        var conversation = new List<AssistantMessage> { new AssistantMessage.User("go") };
        var events = new List<AssistantEvent>();
        await foreach (var e in loop.RunAsync(conversation, null, new FakeApprovals(), CancellationToken.None))
            events.Add(e);

        Assert.Equal(1, tool.Invocations);
        Assert.IsType<AssistantEvent.Completed>(events[^1]);
        Assert.Single(conversation.OfType<AssistantMessage.ToolResults>());

        using var second = JsonDocument.Parse(backend.Bodies[1]);
        var toolMessages = second.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "tool")
            .ToArray();
        Assert.Equal("call_1", Assert.Single(toolMessages).GetProperty("tool_call_id").GetString());
    }

    // A self-hosted endpoint answers unauthenticated, but a gateway put in front of it does not — so
    // a key given for one signs the request on the same header a hosted provider's key uses. Until
    // the card offered the field, this branch was unreachable.
    [Fact]
    public async Task AKeyGivenForASelfHostedEndpointSignsTheRequestAsABearerToken()
    {
        var sent = await PostAsync(AssistantConnection.For(
            AssistantProviders.Ollama, baseUrl: "https://gw.internal/v1", apiKey: "gateway-token"));

        Assert.Equal("https://gw.internal/v1/chat/completions", sent.Url);
        Assert.Equal("Bearer gateway-token", sent.Authorization);
    }

    // And the same endpoint without one still posts — unsigned, which is what it was always for.
    [Fact]
    public async Task ASelfHostedEndpointWithNoKeyStillPostsAndSendsNoAuthorization()
    {
        var sent = await PostAsync(AssistantConnection.For(AssistantProviders.Ollama));

        Assert.Equal("http://localhost:11434/v1/chat/completions", sent.Url);
        Assert.Null(sent.Authorization);
    }

    private static async Task<RecordingHandler.Sent> PostAsync(AssistantConnection connection)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var backend = new OpenAiCompatibleBackend(http, () => connection);
        var turn = new AssistantTurn(
            ModelTier.Chat, "system prompt", new AssistantMessage[] { new AssistantMessage.User("go") });

        await foreach (var _ in backend.SendAsync(turn, Array.Empty<IAssistantTool>(), CancellationToken.None))
        {
        }

        return Assert.Single(handler.Requests);
    }
}

/// Answers every request with an empty event stream, keeping what was asked — the headers are the
/// point, so they are copied out before the request message is disposed.
internal sealed class RecordingHandler : HttpMessageHandler
{
    internal readonly record struct Sent(string Url, string? Authorization);

    public List<Sent> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(new Sent(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
    }
}
