using GitBench.Lsp.Configuration;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// Owns every language server process the app runs: starts one when a file first needs it, keeps
/// at most the configured number alive, restarts what crashes until that stops being worth doing,
/// and stops what nothing is looking at.
/// </summary>
public sealed class LanguageServerSupervisor : IDisposable
{
    readonly ILanguageServerLauncher _launcher;
    readonly IClock _clock;
    readonly SupervisorPolicy _policy;
    readonly Dictionary<(RepositoryId Repo, LanguageId Language), ServerRecord> _servers = [];

    readonly Dictionary<(RepositoryId Repo, LanguageId Language), string> _lastTrigger = [];
    readonly List<Stopping> _stopping = [];

    LanguageServerConfig _config = LanguageServerConfig.Empty;
    Repository? _active;
    bool _disposed;

    public LanguageServerSupervisor(ILanguageServerLauncher launcher, IClock clock, SupervisorPolicy? policy = null)
    {
        _launcher = launcher;
        _clock = clock;
        _policy = policy ?? SupervisorPolicy.Default;
    }

    /// <summary>Raised whenever any server's state changes, including when it disappears.</summary>
    public event Action<ServerStatus>? StatusChanged;

    /// <summary>Every server the supervisor is tracking, running or not.</summary>
    public IReadOnlyList<ServerStatus> Status =>
        _servers.Values.Select(r => new ServerStatus(r.Repo, r.Language, r.State)).ToList();

    /// <summary>
    /// Takes a freshly read config. Servers whose launch no longer matches restart, servers that
    /// left the config stop, and servers that only had a timeout changed keep running.
    /// </summary>
    public void ApplyConfig(LanguageServerConfig config)
    {
        _config = config;

        foreach (var record in _servers.Values.ToList())
        {
            var entry = config.ServerFor(record.Language);
            if (entry is null)
            {
                Discard(record);
                continue;
            }

            if (record.State is ServerState.Failed)
            {
                Discard(record);
                continue;
            }

            if (entry.SameLaunchAs(record.Entry))
            {
                record.Entry = entry;
                continue;
            }

            record.Entry = entry;
            Relaunch(record);
        }

        EnforceCap(null);
    }

    /// <summary>
    /// Switches which repository may run servers. The one being left keeps its servers until they
    /// go idle, so flicking between two repositories does not throw away a warm index.
    /// </summary>
    public void SetActiveRepository(Repository? repository) => _active = repository;

    /// <summary>Stops everything belonging to a repository the app no longer has open.</summary>
    public void CloseRepository(RepositoryId repository)
    {
        if (_active?.Id == repository) _active = null;
        foreach (var record in _servers.Values.Where(r => r.Repo == repository).ToList())
            Discard(record);
    }

    /// <summary>
    /// The Files pane previewing a file. Starts the server for that language if this is the first
    /// such file, and reports where that server stands.
    /// </summary>
    public ServerState OpenFile(string filePath)
    {
        var entry = _config.ServerFor(filePath);
        if (entry is null) return new ServerState.NotConfigured();
        if (_active is not { } active) return new ServerState.Stopped();

        if (_servers.TryGetValue((active.Id, entry.Language), out var existing))
        {
            existing.LastTouched = _clock.Now;
            return existing.State;
        }

        if (ProjectRoot.Find(active.RootPath, filePath, entry.RootMarkers) is not { } root)
            return new ServerState.Stopped();

        _lastTrigger[(active.Id, entry.Language)] = filePath;

        return Start(active, entry, root, filePath).State;
    }

    /// <summary>Where a file's server stands, without starting anything.</summary>
    public ServerState StateFor(string filePath)
    {
        var entry = _config.ServerFor(filePath);
        if (entry is null) return new ServerState.NotConfigured();
        if (_active is not { } active) return new ServerState.Stopped();
        return StateFor(active.Id, entry.Language);
    }

    /// <summary>
    /// The process currently attached to a server, or null when nothing is running for it — it is
    /// stopped, waiting to be restarted, or given up on. Null is the whole point: a caller cannot
    /// hold a handle to a server the supervisor has since replaced.
    /// </summary>
    public ILanguageServerProcess? ProcessFor(RepositoryId repository, LanguageId language) =>
        _servers.TryGetValue((repository, language), out var record) ? record.Link?.Process : null;

    public ServerState StateFor(RepositoryId repository, LanguageId language) =>
        _servers.TryGetValue((repository, language), out var record)
            ? record.State
            : new ServerState.Stopped();

    /// <summary>
    /// The user asking again after a failure. The only way a given-up server comes back, which is
    /// what makes giving up safe.
    /// </summary>
    public ServerState Retry(string filePath)
    {
        if (_config.ServerFor(filePath) is { } entry &&
            _active is { } active &&
            _servers.TryGetValue((active.Id, entry.Language), out var record) &&
            record.State is ServerState.Failed)
            Discard(record);

        return OpenFile(filePath);
    }

    /// <summary>
    /// Stops one server on request. It is not banned: the next file of its language starts it
    /// again, the same as if it had never run.
    /// </summary>
    public void StopServer(RepositoryId repository, LanguageId language)
    {
        if (_servers.TryGetValue((repository, language), out var record)) Discard(record);
    }

    /// <summary>
    /// Starts a server again from where it was last wanted — the user's answer to one that has
    /// failed, or that they stopped. Nothing comes back on its own after being given up on.
    /// </summary>
    public ServerState RestartServer(RepositoryId repository, LanguageId language)
    {
        if (_servers.TryGetValue((repository, language), out var record))
        {
            var known = record.Entry;
            var knownRoot = record.Root;
            var knownTrigger = record.TriggerFile;
            Discard(record);

            if (_active is not { } running || running.Id != repository) return new ServerState.Stopped();
            return Start(running, known, knownRoot, knownTrigger).State;
        }

        if (_active is not { } active || active.Id != repository) return new ServerState.Stopped();

        if (_lastTrigger.TryGetValue((repository, language), out var trigger)) return OpenFile(trigger);

        if (_config.ServerFor(language) is not { } entry) return new ServerState.Stopped();
        return Start(active, entry, active.RootPath, active.RootPath).State;
    }

    /// <summary>
    /// Moves time forward: restarts what is due, stops what has gone idle, kills what ignored a
    /// shutdown, and gives up on what has gone silent.
    /// </summary>
    public void Tick()
    {
        var now = _clock.Now;

        foreach (var record in _servers.Values.ToList())
        {
            if (IsIdle(record, now))
            {
                Discard(record);
                continue;
            }

            if (record.Link is not null &&
                record.State is ServerState.Starting or ServerState.Indexing &&
                now - record.LastSignal >= _policy.ReadySilence)
            {
                Detach(record);
                SetState(record, new ServerState.Failed(
                    $"{record.Entry.Command} started but never became usable."));
            }
        }

        foreach (var stopping in _stopping.ToList())
        {
            if (stopping.Deadline > now) continue;
            stopping.Process.Kill();
            Forget(stopping);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var record in _servers.Values.ToList())
            Discard(record);

        foreach (var stopping in _stopping.ToList())
            Forget(stopping);
    }

    bool IsIdle(ServerRecord record, DateTimeOffset now) =>
        record.Repo != _active?.Id &&
        record.State is not ServerState.Failed &&
        now - record.LastTouched >= record.Entry.IdleShutdown;

    ServerRecord Start(Repository repository, LanguageServerEntry entry, string root, string triggerFile)
    {
        var record = new ServerRecord
        {
            Repo = repository.Id,
            RepoRoot = repository.RootPath,
            Language = entry.Language,
            Entry = entry,
            Root = root,
            TriggerFile = triggerFile,
            LastTouched = _clock.Now,
            State = new ServerState.Stopped(),
        };
        _servers[(repository.Id, entry.Language)] = record;

        EnforceCap(record);
        Launch(record);
        return record;
    }

    void Relaunch(ServerRecord record)
    {
        Detach(record);
        record.Root = ProjectRoot.Find(record.RepoRoot, record.TriggerFile, record.Entry.RootMarkers) ?? record.Root;
        Launch(record);
    }

    void Launch(ServerRecord record)
    {
        record.LastSignal = _clock.Now;

        switch (_launcher.Launch(new ServerLaunchRequest(record.Entry, record.Root, record.RepoRoot)))
        {
            case LaunchResult.Started started:
                // Wired up and marked starting before anything is listened to. A process that has
                // already failed — a binary that exits at once, a handshake refused in the first
                // millisecond — replays its exit the instant a handler is added, and the handler
                // guards on the record already pointing at that process. Subscribing first left
                // the guard reading a link that had not been assigned yet, so the exit was
                // dropped and the server sat "starting" until the silence timer gave up on it
                // with a reason far less useful than the one it had thrown away. Even assigned,
                // a Starting written afterwards would have painted over the failure.
                var link = Attach(record, started.Process);
                record.Link = link;
                SetState(record, new ServerState.Starting());
                link.Listen();
                break;

            case LaunchResult.Failed failed:
                SetState(record, new ServerState.Failed(failed.Reason));
                break;
        }
    }

    Attachment Attach(ServerRecord record, ILanguageServerProcess process)
    {
        var link = new Attachment { Process = process };

        link.OnReadiness = readiness =>
        {
            if (!ReferenceEquals(record.Link?.Process, process)) return;
            record.LastSignal = _clock.Now;
            SetState(record, readiness switch
            {
                ServerReadiness.Ready => new ServerState.Ready(),
                ServerReadiness.Indexing indexing => new ServerState.Indexing(indexing.PercentComplete),
                _ => record.State is ServerState.Ready or ServerState.Indexing
                    ? record.State
                    : new ServerState.Starting(),
            });
        };

        link.OnExited = exit =>
        {
            if (!ReferenceEquals(record.Link?.Process, process)) return;
            record.Link = null;
            process.Dispose();
            OnCrashed(record, exit);
        };

        return link;
    }

    /// <summary>
    /// The process ended. That is the end of it: a server stops for a reason that starting it again
    /// does not change, so the reason is reported and the server stays stopped until the reader
    /// asks for it back. <see cref="Retry"/> is that ask.
    /// </summary>
    void OnCrashed(ServerRecord record, ServerExit exit) =>
        SetState(record, new ServerState.Failed(
            $"{record.Entry.Command} stopped{Code(exit)}.{Detail(exit)}"));

    static string Code(ServerExit exit) => exit.ExitCode is { } code ? $" (exit code {code})" : string.Empty;

    static string Detail(ServerExit exit) =>
        exit.Detail is { Length: > 0 } detail ? $" {detail}" : string.Empty;

    void EnforceCap(ServerRecord? incoming)
    {
        var max = Math.Max(1, _config.MaxConcurrentServers);
        while (true)
        {
            var live = _servers.Values.Where(IsLive).ToList();
            if (live.Count + (incoming is not null && !IsLive(incoming) ? 1 : 0) <= max) return;

            var victim = live
                .Where(r => !ReferenceEquals(r, incoming))
                .OrderBy(r => r.Repo == _active?.Id)
                .ThenBy(r => r.LastTouched)
                .FirstOrDefault();

            if (victim is null) return;
            Discard(victim);
        }
    }

    static bool IsLive(ServerRecord record) => record.Link is not null;

    void SetState(ServerRecord record, ServerState state)
    {
        if (record.State == state) return;
        record.State = state;
        StatusChanged?.Invoke(new ServerStatus(record.Repo, record.Language, state));
    }

    /// <summary>Stops a server's process, if any, and forgets the server itself.</summary>
    void Discard(ServerRecord record)
    {
        Detach(record);
        if (_servers.Remove((record.Repo, record.Language)))
            StatusChanged?.Invoke(new ServerStatus(record.Repo, record.Language, new ServerState.Stopped()));
    }

    /// <summary>Hands the process to the polite-shutdown queue and leaves the record behind.</summary>
    void Detach(ServerRecord record)
    {
        if (record.Link is not { } link) return;
        record.Link = null;
        link.Detach();
        BeginShutdown(link.Process);
    }

    void BeginShutdown(ILanguageServerProcess process)
    {
        var stopping = new Stopping
        {
            Process = process,
            Deadline = _clock.Now + _policy.ShutdownGrace,
        };
        stopping.OnExited = _ => Forget(stopping);
        process.Exited += stopping.OnExited;
        _stopping.Add(stopping);

        process.RequestShutdown();
    }

    void Forget(Stopping stopping)
    {
        if (!_stopping.Remove(stopping)) return;
        stopping.Process.Exited -= stopping.OnExited;
        stopping.Process.Dispose();
    }

    sealed class ServerRecord
    {
        public RepositoryId Repo;
        public LanguageId Language;
        public LanguageServerEntry Entry = null!;
        public string RepoRoot = string.Empty;
        public string Root = string.Empty;
        public string TriggerFile = string.Empty;
        public Attachment? Link;
        public ServerState State = new ServerState.Stopped();
        public DateTimeOffset LastTouched;
        public DateTimeOffset LastSignal;
    }

    sealed class Attachment
    {
        public ILanguageServerProcess Process = null!;
        public Action<ServerReadiness> OnReadiness = null!;
        public Action<ServerExit> OnExited = null!;

        /// <summary>Starts listening. Separate from building the attachment because a process that
        /// has already ended announces it during the subscription itself, and everything the
        /// handlers read has to be in place before that can happen.</summary>
        public void Listen()
        {
            Process.ReadinessChanged += OnReadiness;
            Process.Exited += OnExited;
        }

        public void Detach()
        {
            Process.ReadinessChanged -= OnReadiness;
            Process.Exited -= OnExited;
        }
    }

    sealed class Stopping
    {
        public ILanguageServerProcess Process = null!;
        public Action<ServerExit> OnExited = null!;
        public DateTimeOffset Deadline;
    }
}
