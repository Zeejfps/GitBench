namespace GitBench.Pty;

/// <summary>
/// What to spawn on a pseudo-terminal, and the terminal state it starts with.
/// </summary>
public sealed record PtySessionOptions
{
    /// <summary>Program to run. Resolved against PATH when it is not a full path.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed to <see cref="Executable"/>, already split into argv entries.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Directory the child starts in. Must exist.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Variables layered over the parent process environment. A null value removes an inherited
    /// variable. Terminal identity (TERM, COLORTERM) belongs here — the session sets none of it.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>();

    public PtySize Size { get; init; } = PtySize.Default;
}
