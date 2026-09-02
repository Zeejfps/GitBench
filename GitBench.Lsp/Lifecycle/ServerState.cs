using GitBench.Lsp.Configuration;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// What a language server is doing, as the pane shows it. A sum type rather than flags: "running
/// but not answering" and "crashed but coming back" are states someone has to draw, and a bag of
/// booleans lets them be drawn as neither.
/// </summary>
public abstract record ServerState
{
    ServerState() { }

    /// <summary>No configured server claims this file.</summary>
    public sealed record NotConfigured : ServerState;

    /// <summary>A server is configured for this file and none is running.</summary>
    public sealed record Stopped : ServerState;

    /// <summary>The process exists. It has answered nothing yet — the handshake may not be done.</summary>
    public sealed record Starting : ServerState;

    /// <param name="PercentComplete">Null when the server reports progress without a percentage.</param>
    public sealed record Indexing(int? PercentComplete) : ServerState;

    /// <summary>The server has answered something real. Only this state means questions get answers.</summary>
    public sealed record Ready : ServerState;

    /// <summary>
    /// Stopped, for a reason worth reading. Nothing starts it again on its own: a server stops
    /// because its command is wrong, its toolchain is missing or its project defeated it, and none
    /// of those are fixed by running it a second time — they are fixed by the reader, who needs to
    /// be told rather than made to wait through a retry that was never going to work.
    /// </summary>
    public sealed record Failed(string Reason) : ServerState;
}

/// <summary>
/// What a running server tells the supervisor about itself. Deliberately not the same type as
/// <see cref="ServerState"/>: a server can report a finished handshake, and a finished handshake is
/// not a state the pane may ever show as ready.
/// </summary>
public abstract record ServerReadiness
{
    ServerReadiness() { }

    /// <summary>
    /// The initialize/initialized exchange completed. rust-analyzer does this in 15 ms and is then
    /// half a minute from answering anything, so this reports progress and nothing more.
    /// </summary>
    public sealed record Handshaked : ServerReadiness;

    /// <summary>The server is working towards being able to answer.</summary>
    public sealed record Indexing(int? PercentComplete) : ServerReadiness;

    /// <summary>The server answered a real request. Reported by whoever saw the answer.</summary>
    public sealed record Ready : ServerReadiness;
}

/// <param name="ExitCode">Null when the process ended without one — killed, or lost.</param>
public sealed record ServerExit(int? ExitCode = null, string? Detail = null);

/// <summary>Where the supervisor stands on one language for one repository.</summary>
public sealed record ServerStatus(RepositoryId Repository, LanguageId Language, ServerState State);
