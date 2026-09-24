using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.LanguageServers;
using GitBench.Git;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using GitBench.Theming;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// What a language server makes of the names in the open stop's code, asked before the code is in
/// the file — asked again when the server turns ready, and twice more a while after the first
/// answer it gave ready. A server that has not finished reading the project answers about the open
/// file alone even once it says ready: "nowhere", or the import line a name came in by.
/// Language servers run for the repository on screen only, so a stop waiting in another one is
/// asked about when it comes back. UI thread only.
/// </summary>
internal sealed class DraftNameChecker : IDisposable
{
    // A server that goes back to loading and ready again would otherwise ask on every round.
    private const int MaxAsks = 5;

    // After each answer given ready, how long until the next; none past the last.
    private static readonly TimeSpan[] ReadyRechecks = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    private readonly IDraftDefinitionSource _servers;
    private readonly IReadable<Repo?> _onScreen;
    private readonly Repo _repo;
    private readonly ProbeSlot _asking;
    private readonly ProbeSlot _settling;
    private readonly IDisposable _watchingServers;
    private readonly IDisposable _watchingRepos;

    private Draft? _draft;

    /// <param name="onScreen">The repository on screen, whose language servers are the running ones.</param>
    /// <param name="wait">The wait before a recheck; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test says.</param>
    public DraftNameChecker(
        IDraftDefinitionSource servers,
        IReadable<Repo?> onScreen,
        Repo repo,
        IUiDispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _servers = servers;
        _onScreen = onScreen;
        _repo = repo;
        _asking = new ProbeSlot(dispatcher);
        _settling = new ProbeSlot(dispatcher, wait);
        _watchingServers = servers.Active.Subscribe(_ => AskAgain());
        _watchingRepos = onScreen.Subscribe(_ => AskAgain());
    }

    /// <summary>Starts on a stop's code; <paramref name="answered"/> hears every answer that says
    /// something, until <see cref="Clear"/> or the next check.</summary>
    public void Check(
        string absolutePath,
        IReadOnlyList<string> file,
        DraftPlace place,
        IReadOnlyList<string> code,
        IReadOnlyList<IReadOnlyList<TokenSpan>> spans,
        Action<IReadOnlyList<DraftName>> answered)
    {
        _asking.Cancel();
        _settling.Cancel();
        var names = DraftNames.In(code, spans);
        _draft = names.Count == 0
            ? null
            : new Draft(absolutePath, DraftSplice.Of(file, place, code), names, answered);
        Ask();
    }

    public void Clear()
    {
        _asking.Cancel();
        _settling.Cancel();
        _draft = null;
    }

    private void AskAgain()
    {
        if (_draft is not { Asking: false } draft || draft.Asks >= MaxAsks) return;
        if (draft.Asks > 0 && (draft.AskedReady || StateOf(draft) is not ServerState.Ready)) return;
        Ask();
    }

    private void Ask()
    {
        if (_draft is not { } draft || _onScreen.Value?.Id != _repo.Id) return;

        draft.Asks++;
        draft.Asking = true;
        draft.AskedReady = StateOf(draft) is ServerState.Ready;
        var positions = draft.Names
            .Select(name => new TextPosition(new FileLine(draft.Splice.LineOfCode(name.Line)), name.Start))
            .ToArray();
        _asking.Ask(
            TimeSpan.Zero,
            token => _servers.DefineInDraftAsync(draft.Path, draft.Splice.Text, positions, token),
            reply => Answered(draft, reply));
    }

    private void Answered(Draft draft, DraftDefinitions reply)
    {
        draft.Asking = false;
        if (!ReferenceEquals(_draft, draft)) return;

        switch (reply)
        {
            case DraftDefinitions.Unavailable:
                // Asked again once the server moves on; nothing to show meanwhile.
                break;
            case DraftDefinitions.Answered(var answers):
                var names = DraftNames.Classify(draft.Names, answers, draft.Splice, draft.Path, _repo.Path, draft.AskedReady);
                draft.Then(names);
                if (draft.AskedReady && draft.ReadyAnswers < ReadyRechecks.Length && draft.Asks < MaxAsks)
                    _settling.Wait(ReadyRechecks[draft.ReadyAnswers++], () =>
                    {
                        if (ReferenceEquals(_draft, draft) && !draft.Asking) Ask();
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reply), reply, "Unknown draft definitions.");
        }
    }

    private ServerState StateOf(Draft draft) => _servers.Active.Value.StateFor(draft.Path);

    public void Dispose()
    {
        _asking.Dispose();
        _settling.Dispose();
        _watchingServers.Dispose();
        _watchingRepos.Dispose();
    }

    private sealed class Draft(
        string path, DraftSplice splice, IReadOnlyList<DraftNameAt> names, Action<IReadOnlyList<DraftName>> then)
    {
        public string Path { get; } = path;
        public DraftSplice Splice { get; } = splice;
        public IReadOnlyList<DraftNameAt> Names { get; } = names;
        public Action<IReadOnlyList<DraftName>> Then { get; } = then;
        public int Asks { get; set; }
        public bool Asking { get; set; }
        public bool AskedReady { get; set; }
        public int ReadyAnswers { get; set; }
    }
}
