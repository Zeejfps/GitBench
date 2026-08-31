using System.Collections.Concurrent;

using TreeSitter;

namespace GitBench.Features.CodeIntel;

internal sealed class ParseSession : IDisposable
{
    public ParseSession(Language language)
    {
        Parser = new Parser(language);
        try
        {
            Cursor = new QueryCursor();
        }
        catch
        {
            Parser.Dispose();
            throw;
        }
    }

    public Parser Parser { get; }

    public QueryCursor Cursor { get; }

    public void Dispose()
    {
        Cursor.Dispose();
        Parser.Dispose();
    }
}

internal sealed class ParseSessionPool : IDisposable
{
    private readonly Language _language;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<ParseSession> _idle = [];

    public ParseSessionPool(Language language, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _language = language;
        _slots = new SemaphoreSlim(capacity, capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public TResult Use<TState, TResult>(TState state, Func<ParseSession, TState, TResult> work)
    {
        _slots.Wait();
        ParseSession? session = null;
        try
        {
            if (!_idle.TryTake(out session))
            {
                session = new ParseSession(_language);
            }

            var result = work(session, state);
            _idle.Add(session);
            session = null;
            return result;
        }
        finally
        {
            session?.Dispose();
            _slots.Release();
        }
    }

    public void Dispose()
    {
        while (_idle.TryTake(out var session))
        {
            session.Dispose();
        }

        _slots.Dispose();
    }
}
