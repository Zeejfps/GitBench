using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Lsp.Tests.Fakes;

/// <summary>
/// A language server that never existed: it reports whatever the test scripts it to report, and
/// misbehaves on request. Scripted rather than asserted on, so a test says what the server did
/// rather than which method the supervisor called.
/// </summary>
public sealed class FakeLanguageServer : ILanguageServerProcess
{
    bool _running = true;

    public event Action<ServerReadiness>? ReadinessChanged;
    public event Action<ServerExit>? Exited;

    public required ServerLaunchRequest Request { get; init; }

    /// <summary>A server that takes the shutdown request and keeps running, as some do.</summary>
    public bool IgnoresShutdown { get; set; }

    public bool IsRunning => _running;
    public int ShutdownRequests { get; private set; }
    public bool WasKilled { get; private set; }
    public bool WasDisposed { get; private set; }

    public LanguageId Language => Request.Entry.Language;

    public void CompleteHandshake() => Report(new ServerReadiness.Handshaked());

    public void ReportIndexing(int? percentComplete = null) => Report(new ServerReadiness.Indexing(percentComplete));

    public void BecomeReady() => Report(new ServerReadiness.Ready());

    /// <summary>Ends without being asked to.</summary>
    public void Crash(int exitCode = 101) => End(new ServerExit(exitCode, "crashed"));

    public void RequestShutdown()
    {
        ShutdownRequests++;
        if (!IgnoresShutdown) End(new ServerExit(0));
    }

    public void Kill()
    {
        WasKilled = true;
        End(new ServerExit());
    }

    public void Dispose() => WasDisposed = true;

    void Report(ServerReadiness readiness)
    {
        if (!_running) return;
        ReadinessChanged?.Invoke(readiness);
    }

    void End(ServerExit exit)
    {
        if (!_running) return;
        _running = false;
        Exited?.Invoke(exit);
    }
}

/// <summary>Hands out <see cref="FakeLanguageServer"/>s and remembers what it was asked to launch.</summary>
public sealed class FakeLauncher : ILanguageServerLauncher
{
    readonly List<FakeLanguageServer> _started = [];
    readonly List<ServerLaunchRequest> _requests = [];
    readonly Queue<string> _scriptedFailures = new();

    string? _failEverything;

    public IReadOnlyList<ServerLaunchRequest> Requests => _requests;

    /// <summary>Every server ever started, in order.</summary>
    public IReadOnlyList<FakeLanguageServer> Started => _started;

    public IReadOnlyList<FakeLanguageServer> Running => _started.Where(s => s.IsRunning).ToList();

    public FakeLanguageServer Last => _started[^1];

    /// <summary>Configures the servers this launcher hands out before they are asked for.</summary>
    public Action<FakeLanguageServer>? Configure { get; set; }

    public void FailNextLaunch(string reason) => _scriptedFailures.Enqueue(reason);

    public void FailEveryLaunch(string reason) => _failEverything = reason;

    public void StopFailing()
    {
        _failEverything = null;
        _scriptedFailures.Clear();
    }

    /// <summary>The most recent server started for a language, running or not.</summary>
    public FakeLanguageServer For(string language) =>
        _started.Last(s => s.Language.Value == language);

    public LaunchResult Launch(ServerLaunchRequest request)
    {
        _requests.Add(request);

        if (_failEverything is { } always) return new LaunchResult.Failed(always);
        if (_scriptedFailures.Count > 0) return new LaunchResult.Failed(_scriptedFailures.Dequeue());

        var server = new FakeLanguageServer { Request = request };
        Configure?.Invoke(server);
        _started.Add(server);
        return new LaunchResult.Started(server);
    }
}

/// <summary>Time that only moves when a test moves it.</summary>
public sealed class TestClock : IClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan amount) => Now += amount;
}
