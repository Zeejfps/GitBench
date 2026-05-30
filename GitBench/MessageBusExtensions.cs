namespace GitGui;

internal static class MessageBusExtensions
{
    public static IDisposable SubscribeScoped<T>(this IMessageBus bus, Action<T> handler) where T : struct
    {
        bus.Subscribe(handler);
        return new Subscription<T>(bus, handler);
    }

    private sealed class Subscription<T> : IDisposable where T : struct
    {
        private IMessageBus? _bus;
        private Action<T>? _handler;

        public Subscription(IMessageBus bus, Action<T> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_bus != null && _handler != null) _bus.Unsubscribe(_handler);
            _bus = null;
            _handler = null;
        }
    }
}
