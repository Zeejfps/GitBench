using System.Collections.Concurrent;
using GitBench.App;
using GitBench.Features.Terminal;
using GitBench.Messages;
using Xunit;
using ZGF.Observable;

namespace GitBench.Tests;

public class AppExitGateTests
{
    [Fact]
    public void ExitsImmediatelyWhenNoShellIsRunning()
    {
        var (gate, dispatcher, bus, terminals) = Build();
        terminals.Running = [];
        var exited = false;

        var exiting = gate.RequestExit(AppExitKind.Quit, () => exited = true);
        dispatcher.Drain();

        Assert.True(exiting);
        Assert.True(exited);
        Assert.Equal(0, bus.DialogsShown);
    }

    [Fact]
    public void AsksBeforeExitingWhileAShellIsRunning()
    {
        var (gate, dispatcher, bus, terminals) = Build();
        terminals.Running = [Guid.NewGuid()];
        var exited = false;

        var exiting = gate.RequestExit(AppExitKind.Quit, () => exited = true);
        dispatcher.Drain();

        Assert.False(exiting);
        Assert.False(exited);
        Assert.Equal(1, bus.DialogsShown);
    }

    private static (AppExitGate Gate, QueuedDispatcher Dispatcher, CountingBus Bus, FakeTerminals Terminals) Build()
    {
        var terminals = new FakeTerminals();
        var dispatcher = new QueuedDispatcher();
        var bus = new CountingBus();
        return (new AppExitGate(terminals, dispatcher, bus), dispatcher, bus, terminals);
    }

    private sealed class FakeTerminals : ITerminalSessionStore
    {
        private readonly State<TerminalTabs?> _tabs = new(null);

        public IReadOnlyList<Guid> Running = [];

        public IReadable<TerminalTabs?> Tabs => _tabs;
        public bool HasLiveShell(Guid repoId) => Running.Contains(repoId);
        public IReadOnlyList<Guid> ReposWithLiveShells() => Running;
    }

    private sealed class CountingBus : IMessageBus
    {
        public int DialogsShown;

        public void Broadcast<T>(T message = default) where T : struct
        {
            if (message is ShowDialogMessage) DialogsShown++;
        }

        public void Subscribe<T>(Action<T> handler) where T : struct { }
        public void Unsubscribe<T>(Action<T> handler) where T : struct { }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }
}
