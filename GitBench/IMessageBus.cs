namespace GitGui;

public interface IMessageBus
{
    void Broadcast<T>(T message = default) where T : struct;
    void Subscribe<T>(Action<T> handler) where T : struct;
    void Unsubscribe<T>(Action<T> handler) where T : struct;
}