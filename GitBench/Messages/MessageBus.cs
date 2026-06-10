namespace GitBench.Messages;

public sealed class MessageBus : IMessageBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public void Broadcast<T>(T message = default) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;

        // Snapshot so a handler can (un)subscribe during delivery without throwing, and re-check
        // membership so a handler removed earlier in this same broadcast isn't invoked after it's
        // been torn down. The lists hold a handful of subscribers, so the Contains scan is cheap.
        var snapshot = list.ToArray();
        foreach (var handler in snapshot)
        {
            if (!list.Contains(handler)) continue;
            ((Action<T>)handler)(message);
        }
    }

    public void Subscribe<T>(Action<T> handler) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
        {
            list = [];
            _handlers[typeof(T)] = list;
        }
        list.Add(handler);
    }

    public void Unsubscribe<T>(Action<T> handler) where T : struct
    {
        if (_handlers.TryGetValue(typeof(T), out var list))
            list.Remove(handler);
    }
}
