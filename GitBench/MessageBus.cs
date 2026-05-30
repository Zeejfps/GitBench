namespace GitGui;

public sealed class MessageBus : IMessageBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public void Broadcast<T>(T message = default) where T : struct
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;

        var snapshot = list.ToArray();
        foreach (var handler in snapshot)
        {
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
