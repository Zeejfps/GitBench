namespace GitBench.Features.Editor;

/// <summary>The thread something was built on, which is the only one allowed to touch it. An
/// off-thread caller hops through the UI dispatcher first.</summary>
internal readonly struct OwningThread
{
    private readonly int _id;

    private OwningThread(int id) => _id = id;

    public static OwningThread Current() => new(Environment.CurrentManagedThreadId);

    public void Assert(string what)
    {
        if (Environment.CurrentManagedThreadId == _id) return;
        throw new InvalidOperationException(
            $"{what} belongs to the thread that built it (thread {_id}); this is thread " +
            $"{Environment.CurrentManagedThreadId}. An off-thread caller hops through the UI dispatcher first.");
    }
}
