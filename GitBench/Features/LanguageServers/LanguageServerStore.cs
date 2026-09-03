using GitBench.App;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal enum StarterConfigOutcome
{
    Written,
    AlreadyExists,
    NotWritten,
}

internal interface ILanguageServerStore : IHoverSource, IDefinitionSource, IReferenceSource
{
    IReadable<LanguageServerSnapshot> Active { get; }

    IReadable<FileDiagnostics> Diagnostics { get; }

    string ConfigPath { get; }

    void FileShown(string absolutePath);

    void ReloadConfig();

    void RetryServer(LanguageId language);

    void StopServer(LanguageId language);

    StarterConfigOutcome WriteStarterConfig();
}

internal sealed class LanguageServerStore : ILanguageServerStore, IHostedService, IDisposable
{
    public const string ConfigFileName = "language-servers.json";

    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IRepoRegistry _registry;
    private readonly IFileSystemReader _files;
    private readonly IUiDispatcher _dispatcher;
    private readonly LanguageServerSupervisor _supervisor;
    private readonly State<LanguageServerSnapshot> _active = new(LanguageServerSnapshot.Nothing);
    private readonly State<FileDiagnostics> _diagnostics = new(FileDiagnostics.None);
    private readonly Dictionary<Guid, IReadOnlyList<StarterServer>> _suggestions = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly IMessageBus _bus;
    private readonly ILocalizationService _loc;

    private LanguageServerConfig _config = LanguageServerConfig.Empty;
    private IReadOnlyList<ConfigProblem> _problems = [];
    private bool _configFileExists;
    private IDisposable? _activeSub;
    private IDisposable? _reposSub;
    private bool _started;
    private bool _pumping;
    private bool _disposed;
    private LanguageServerConnection? _watched;
    private string _watchedPath = string.Empty;

    public LanguageServerStore(
        IRepoRegistry registry,
        IFileSystemReader files,
        IUiDispatcher dispatcher,
        IMessageBus bus,
        ILocalizationService loc,
        ILanguageServerLauncher? launcher = null,
        IClock? clock = null,
        string? configPath = null)
    {
        _registry = registry;
        _files = files;
        _dispatcher = dispatcher;
        _bus = bus;
        _loc = loc;
        ConfigPath = configPath ?? AppPaths.AppDataPath(ConfigFileName);
        _supervisor = new LanguageServerSupervisor(
            launcher ?? new LanguageServerLauncher(
                new ProcessLanguageServerLauncher(
                    new MapServerEnvironment(LoginShellEnvironment.ForChildProcess),
                    dispatcher.Post),
                HandshakeTimeout,
                dispatcher.Post),
            clock ?? SystemClock.Instance);
        _supervisor.StatusChanged += OnStatusChanged;
        LoadConfig();
    }

    public IReadable<LanguageServerSnapshot> Active => _active;

    public IReadable<FileDiagnostics> Diagnostics => _diagnostics;

    public string ConfigPath { get; }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
    }

    public bool Handles(string absolutePath) => _config.ServerFor(absolutePath) is not null;

    public async Task<HoverText?> HoverAsync(
        string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await ConnectionFor(absolutePath).ConfigureAwait(false) is not { } connection) return null;
        return await connection.HoverAsync(absolutePath, line, column, cancel).ConfigureAwait(false);
    }

    public bool CanDefine(string absolutePath)
    {
        if (_disposed) return false;
        if (_config.ServerFor(absolutePath) is not { } entry) return false;
        if (_registry.Active.Value is not { } repo) return false;

        return _supervisor.ProcessFor(new RepositoryId(repo.Id), entry.Language)
            is not LanguageServerConnection connection || connection.AnswersDefinitions;
    }

    public async Task<DefinitionReply> DefineAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await ConnectionFor(absolutePath).ConfigureAwait(false) is not { } connection)
            return DefinitionReply.Nothing;
        return await connection.DefinitionAsync(absolutePath, line, column, cancel).ConfigureAwait(false);
    }

    public bool CanReference(string absolutePath)
    {
        if (_disposed) return false;
        if (_config.ServerFor(absolutePath) is not { } entry) return false;
        if (_registry.Active.Value is not { } repo) return false;

        return _supervisor.ProcessFor(new RepositoryId(repo.Id), entry.Language)
            is not LanguageServerConnection connection || connection.AnswersReferences;
    }

    public async Task<ReferenceReply> ReferencesAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await ConnectionFor(absolutePath).ConfigureAwait(false) is not { } connection)
            return ReferenceReply.Unavailable.Instance;
        return await connection.ReferencesAsync(absolutePath, line, column, cancel).ConfigureAwait(false);
    }

    public void ReloadConfig()
    {
        if (_disposed) return;
        LoadConfig();
        _suggestions.Clear();
        Publish();
        if (_registry.Active.Value is { } repo) RefreshSuggestions(repo);
    }

    public void RetryServer(LanguageId language)
    {
        if (_disposed || _registry.Active.Value is not { } repo) return;
        _supervisor.RestartServer(new RepositoryId(repo.Id), language);
        EnsurePump();
        Publish();

        // A server nobody asks anything never reports itself ready, because ready means answered.
        // Started by hand, there is no file being opened to do the asking — so the file already on
        // screen does it, if this is the server that would serve it.
        if (_watchedPath.Length > 0 && _config.ServerFor(_watchedPath)?.Language.Equals(language) == true)
            Prepare(_watchedPath);
    }

    public void StopServer(LanguageId language)
    {
        if (_disposed || _registry.Active.Value is not { } repo) return;
        _supervisor.StopServer(new RepositoryId(repo.Id), language);
        Publish();
    }

    public StarterConfigOutcome WriteStarterConfig()
    {
        if (_disposed) return StarterConfigOutcome.NotWritten;

        var path = ConfigPath;
        if (File.Exists(path)) return StarterConfigOutcome.AlreadyExists;

        var suggestions = _active.Value.Suggestions;
        if (suggestions.Count == 0) return StarterConfigOutcome.NotWritten;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StarterServers.ConfigText(suggestions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StarterConfigOutcome.NotWritten;
        }

        ReloadConfig();
        return StarterConfigOutcome.Written;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watched is not null) _watched.DocumentChanged -= OnDocumentChanged;
        _watched = null;
        _stopping.Cancel();
        _activeSub?.Dispose();
        _reposSub?.Dispose();
        _supervisor.Dispose();
        _stopping.Dispose();
    }

    private Task<LanguageServerConnection?> ConnectionFor(string absolutePath)
    {
        var answer = new TaskCompletionSource<LanguageServerConnection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.Post(() =>
        {
            try
            {
                answer.SetResult(Start(absolutePath));
            }
            catch (Exception ex)
            {
                answer.SetException(ex);
            }
        });

        return answer.Task;

        LanguageServerConnection? Start(string path)
        {
            if (_disposed) return null;
            if (_config.ServerFor(path) is not { } entry) return null;
            if (_registry.Active.Value is not { } repo) return null;

            _supervisor.OpenFile(path);
            EnsurePump();
            Publish();
            return _supervisor.ProcessFor(new RepositoryId(repo.Id), entry.Language) as LanguageServerConnection;
        }
    }

    public void FileShown(string absolutePath)
    {
        if (_disposed) return;
        if (_config.ServerFor(absolutePath) is null) return;
        if (_registry.Active.Value is null) return;

        _supervisor.OpenFile(absolutePath);
        EnsurePump();
        Publish();

        Prepare(absolutePath);
    }

    private void Prepare(string absolutePath)
    {
        if (_config.ServerFor(absolutePath) is { } entry &&
            _registry.Active.Value is { } active &&
            _supervisor.ProcessFor(new RepositoryId(active.Id), entry.Language) is LanguageServerConnection connection)
        {
            Watch(connection, absolutePath);
            _ = connection.PrepareAsync(absolutePath, CancellationToken.None);
        }
        else
        {
            Watch(null, absolutePath);
        }
    }

    private void Watch(LanguageServerConnection? connection, string absolutePath)
    {
        if (ReferenceEquals(_watched, connection) && _watchedPath == absolutePath) return;

        if (_watched is not null) _watched.DocumentChanged -= OnDocumentChanged;
        _watched = connection;
        _watchedPath = absolutePath;
        if (_watched is not null) _watched.DocumentChanged += OnDocumentChanged;

        _diagnostics.Value = connection is null
            ? FileDiagnostics.None
            : new FileDiagnostics(absolutePath, connection.Document);
    }

    /// <summary>
    /// A document changes on whatever thread read the file or received the wave, and what reads
    /// <see cref="Diagnostics"/> is the view tree. Everything else reaching this store is already
    /// marshalled by the launcher; this is the one path that is not, so it marshals here.
    /// </summary>
    private void OnDocumentChanged(DocumentState state) => _dispatcher.Post(() =>
    {
        if (_disposed || _watched is null) return;
        _diagnostics.Value = new FileDiagnostics(_watchedPath, state);
    });

    private void OnActiveChanged()
    {
        if (_disposed) return;

        var repo = _registry.Active.Value;
        _supervisor.SetActiveRepository(
            repo is null ? null : new Repository(new RepositoryId(repo.Id), repo.Path));
        Publish();
        if (repo is not null) RefreshSuggestions(repo);
    }

    private void DropClosedRepos()
    {
        if (_disposed) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        foreach (var status in _supervisor.Status)
            if (!open.Contains(status.Repository.Value))
                _supervisor.CloseRepository(status.Repository);

        foreach (var id in _suggestions.Keys.Where(id => !open.Contains(id)).ToArray())
            _suggestions.Remove(id);
    }

    private void RefreshSuggestions(Repo repo)
    {
        if (_suggestions.ContainsKey(repo.Id))
        {
            Publish();
            return;
        }

        var (id, path) = (repo.Id, repo.Path);
        var token = _stopping.Token;
        Task.Run(
            () =>
            {
                var names = _files.List(path, token) is DirectoryListing.Listed listed
                    ? listed.Entries.Select(entry => entry.Name).ToArray()
                    : [];

                _dispatcher.Post(() =>
                {
                    if (_disposed) return;
                    _suggestions[id] = StarterServers.SuggestFor(names, _config);
                    Publish();
                });
            },
            token);
    }

    private void LoadConfig()
    {
        var path = ConfigPath;
        _configFileExists = File.Exists(path);
        if (!_configFileExists)
        {
            _config = LanguageServerConfig.Empty;
            _problems = [];
            _supervisor.ApplyConfig(_config);
            Publish();
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _config = LanguageServerConfig.Empty;
            _problems = [new ConfigProblem(ConfigFileName, ex.Message)];
            _supervisor.ApplyConfig(_config);
            Publish();
            return;
        }

        switch (LanguageServerConfig.Parse(text))
        {
            case ConfigParse.Loaded(var config, var problems):
                _config = config;
                _problems = problems;
                break;
            case ConfigParse.NotUnderstood(var error):
                _config = LanguageServerConfig.Empty;
                _problems = [new ConfigProblem(ConfigFileName, error.Line is { } line
                    ? $"line {line}: {error.Message}"
                    : error.Message)];
                break;
            case ConfigParse.Unsupported(var fileVersion, var supported):
                _config = LanguageServerConfig.Empty;
                _problems = [new ConfigProblem(
                    ConfigFileName,
                    $"written for version {fileVersion}; this build understands {supported}.")];
                break;
        }

        _supervisor.ApplyConfig(_config);
        Publish();
    }

    /// <summary>
    /// A server's state moved. Publishing it repaints the chip and the settings dialog; a move into
    /// <see cref="ServerState.Failed"/> also raises the same operation-error dialog a failed git
    /// command raises, because the reason is the one thing that makes the failure actionable and a
    /// tooltip on a small dot is not somewhere a reader thinks to look.
    /// </summary>
    /// <remarks>
    /// Once per failure, not once per attempt: the supervisor only reaches Failed after it has run
    /// out of restarts, and a failed server stays failed until it is explicitly retried, so this
    /// cannot become a loop of dialogs behind a server that will not start.
    /// </remarks>
    private void OnStatusChanged(ServerStatus status)
    {
        Publish();
        if (_disposed || status.State is not ServerState.Failed failed) return;

        _bus.Broadcast(new ShowOperationErrorMessage(
            _loc.Strings.Value.LanguageServersFailedTitle(status.Language.Value),
            failed.Reason));
    }

    private void Publish()
    {
        if (_disposed) return;

        var repo = _registry.Active.Value;
        _active.Value = repo is null
            ? LanguageServerSnapshot.Nothing with { Config = _config, Problems = _problems, ConfigFileExists = _configFileExists }
            : new LanguageServerSnapshot(
                _config,
                _supervisor.Status.Where(status => status.Repository.Value == repo.Id).ToArray(),
                _problems,
                _suggestions.TryGetValue(repo.Id, out var suggestions) ? suggestions : [],
                _configFileExists);
    }

    private void EnsurePump()
    {
        if (_pumping || _disposed) return;
        _pumping = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(PumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                _dispatcher.Post(Tick);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick()
    {
        if (_disposed) return;
        _supervisor.Tick();
    }
}

internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    private SystemClock() { }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

internal sealed class LanguageServerLauncher(
    ILanguageServerLauncher processes, TimeSpan handshakeTimeout, Action<Action>? post = null)
    : ILanguageServerLauncher
{
    public LaunchResult Launch(ServerLaunchRequest request)
    {
        var launched = processes.Launch(request);
        return launched is LaunchResult.Started { Process: ILanguageServerSession session }
            ? new LaunchResult.Started(new LanguageServerConnection(
                session, request, handshakeTimeout, post: post))
            : launched;
    }
}
