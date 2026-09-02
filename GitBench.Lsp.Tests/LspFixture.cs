using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// A client wired to a scripted server over an in-memory pipe, with a clock the test owns. This is the
/// arrangement every connection test starts from; nothing here waits for a duration.
/// </summary>
internal sealed class LspFixture : IAsyncDisposable
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    public static readonly DocumentUri SomeFile = DocumentUri.Parse("file:///repo/src/main.rs");

    public LspFixture(LspFrameLimits? limits = null) =>
        Connection = LspConnection.Start(Server.ClientChannel, Client, Clock, limits);

    public ScriptedLspServer Server { get; } = new();

    public RecordingClient Client { get; } = new();

    public TestTimeProvider Clock { get; } = new();

    public LspConnection Connection { get; }

    public Task<LspResponse<Hover>> AskHover(CancellationToken ct = default) => AskHoverAtLine(3, Budget, ct);

    public Task<LspResponse<Hover>> AskHoverAtLine(int line, TimeSpan? budget = null, CancellationToken ct = default) =>
        Connection.Send(LspRequests.Hover(SomeFile, LspPosition.At(line, 0)), budget ?? Budget, ct);

    public Task<LspResponse<Definition>> AskDefinition() =>
        Connection.Send(LspRequests.Definition(SomeFile, LspPosition.At(3, 7)), Budget);

    /// <summary>One hover per line, all in flight at once.</summary>
    public IReadOnlyList<Task<LspResponse<Hover>>> AskHoversAtLines(int count) =>
        Enumerable.Range(0, count).Select(line => AskHoverAtLine(line)).ToArray();

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await Server.DisposeAsync();
    }
}

internal static class Wire
{
    /// <summary>A minimal well-formed hover result carrying one piece of text.</summary>
    public static string HoverJson(string text) => $$"""{"contents":{{Quote(text)}}}""";

    public static string Quote(string value) => $"\"{JsonEncodedText.Encode(value)}\"";

    public static string TextOf(LspResponse<Hover> response) =>
        Assert.IsType<Hover.Text>(Assert.IsType<LspResponse<Hover>.Ok>(response).Value).Value;

    public static Hover HoverOf(LspResponse<Hover> response) =>
        Assert.IsType<LspResponse<Hover>.Ok>(response).Value;

    public static Definition DefinitionOf(LspResponse<Definition> response) =>
        Assert.IsType<LspResponse<Definition>.Ok>(response).Value;

    /// <summary>
    /// Awaits an answer that the contract says must arrive. A connection that leaks a pending request
    /// fails here by name instead of wedging the run.
    /// </summary>
    public static Task<T> Answered<T>(Task<T> pending) => pending.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>The line a hover request asked about, read back off the wire.</summary>
    public static int LineOf(ClientMessage.Request request) =>
        request.Params.GetProperty("position").GetProperty("line").GetInt32();
}
