namespace GitBench.Infrastructure;

/// <summary>
/// The order things were opened in, as one number every kind of tab can be sorted by.
/// </summary>
/// <remarks>
/// A shell and a file are opened through stores that know nothing about each other, and the content
/// panel shows them in a single run: something has to be able to say which of the two came first,
/// and the only thing both sides share is the moment. Process-wide rather than per repository — a
/// tab is only ever ordered against its own repository's, so a counter each would be a lifetime to
/// manage for an answer that never differs.
/// </remarks>
internal static class OpenOrder
{
    private static long _last;

    public static long Next() => Interlocked.Increment(ref _last);
}
