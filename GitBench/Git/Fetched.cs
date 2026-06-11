using GitBench.Infrastructure;

namespace GitBench.Git;

// Result of a git read: the loaded value or why the read failed. A value can no longer
// coexist with an error the way the old ErrorMessage-in-snapshot fields allowed.
public abstract record Fetched<T> : IOutcome<Fetched<T>>
{
    private Fetched() { }

    public static Fetched<T> Fail(string message) => new Failed(message);

    public string? FailureMessage => (this as Failed)?.Message;

    public static implicit operator Fetched<T>(T value) => new Ok(value);

    public Fetched<TOut> Map<TOut>(Func<T, TOut> map) => this switch
    {
        Ok ok => new Fetched<TOut>.Ok(map(ok.Value)),
        Failed failed => new Fetched<TOut>.Failed(failed.Message, failed.Detail),
        _ => throw new System.Diagnostics.UnreachableException(),
    };

    public sealed record Ok(T Value) : Fetched<T>;

    // Detail carries the full multi-line git error block (stderr+stdout) when one exists —
    // the UI shows Message (one line) inline and offers Detail in a scrollable dialog.
    public sealed record Failed(string Message, string? Detail = null) : Fetched<T>;
}
