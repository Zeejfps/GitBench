using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GitBench.Features.Assistant;
using GitBench.Features.Assistant.Backend;
using GitBench.Features.Assistant.Tools;

namespace GitBench.Tests;

// Replays scripted turns so the agent loop can be exercised without a network call. One script
// entry per model turn; a turn past the end of the script completes immediately.
internal sealed class FakeAssistantBackend : IAssistantBackend
{
    private readonly Queue<IReadOnlyList<BackendEvent>> _turns;

    public FakeAssistantBackend(params IReadOnlyList<BackendEvent>[] turns) =>
        _turns = new Queue<IReadOnlyList<BackendEvent>>(turns);

    public List<AssistantTurn> Requests { get; } = new();

    public async IAsyncEnumerable<BackendEvent> SendAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(turn);
        var script = _turns.Count > 0
            ? _turns.Dequeue()
            : new BackendEvent[] { new BackendEvent.TurnComplete(StopReason.EndTurn) };

        foreach (var backendEvent in script)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return backendEvent;
        }
    }
}

// The same idea one wire format down: replays raw /v1/chat/completions event streams through the
// real reader, so the OpenAI framing — JSON-encoded tool arguments, [DONE], finish reasons — is what
// the loop is actually driven by, and the request each turn produced is kept for inspection.
internal sealed class FakeOpenAiBackend : IAssistantBackend
{
    private readonly Queue<string> _streams;
    private readonly AssistantConnection _connection;

    public FakeOpenAiBackend(params string[] streams)
        : this(AssistantConnection.For(AssistantProviders.Ollama), streams)
    {
    }

    public FakeOpenAiBackend(AssistantConnection connection, params string[] streams)
    {
        _connection = connection;
        _streams = new Queue<string>(streams);
    }

    /// <summary>The request bodies sent, in order, as they went on the wire.</summary>
    public List<string> Bodies { get; } = new();

    public async IAsyncEnumerable<BackendEvent> SendAsync(
        AssistantTurn turn,
        IReadOnlyList<IAssistantTool> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Bodies.Add(Encoding.UTF8.GetString(OpenAiRequestWriter.Write(turn, tools, _connection)));

        var stream = _streams.Count > 0 ? _streams.Dequeue() : "data: [DONE]\n";
        using var reader = new StringReader(stream);
        await foreach (var backendEvent in OpenAiStreamReader.ReadAsync(reader, ct))
            yield return backendEvent;
    }
}

internal sealed class StubTool : IAssistantTool
{
    private readonly Func<JsonElement, ToolInvocation> _run;

    public StubTool(string name, Func<JsonElement, ToolInvocation>? run = null)
    {
        Name = name;
        _run = run ?? (_ => ToolInvocation.Ok(name + "-result"));
    }

    public string Name { get; }
    public string Description => $"stub tool {Name}";
    public string JsonSchema => """{"type":"object","properties":{},"additionalProperties":false}""";
    public bool IsWrite { get; init; }

    /// <summary>How many times the loop actually ran it — the assertion for "not before approval".</summary>
    public int Invocations { get; private set; }

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        Invocations++;
        return Task.FromResult(_run(args));
    }
}

// Answers every approval the same way, and records what it was asked so a test can assert the loop
// showed the tool's real arguments.
internal sealed class FakeApprovals : IToolApprovalGate
{
    private readonly Func<string, bool> _answer;

    public FakeApprovals(bool approve = true) : this(_ => approve) { }

    public FakeApprovals(Func<string, bool> answer) => _answer = answer;

    public List<string> Asked { get; } = new();

    public List<string> Arguments { get; } = new();

    public Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        Asked.Add(toolName);
        Arguments.Add(ToolJson.Describe(arguments));
        return Task.FromResult(_answer(toolName));
    }
}

// Never answers: the turn stays suspended on it until it is cancelled.
internal sealed class BlockingApprovals : IToolApprovalGate
{
    private readonly TaskCompletionSource<bool> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action? _onAsked;

    public BlockingApprovals(Action? onAsked = null) => _onAsked = onAsked;

    public int Asked { get; private set; }

    public Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        Asked++;
        _onAsked?.Invoke();
        return _never.Task.WaitAsync(ct);
    }
}

internal static class AssistantTestJson
{
    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement Empty => Element("{}");
}
