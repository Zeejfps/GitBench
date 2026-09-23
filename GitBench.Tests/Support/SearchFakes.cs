using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Features.Search;
using GitBench.Git;
using GitBench.Lsp;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>A symbol index whose snapshot the test sets.</summary>
internal sealed class FixedSymbolIndex : ISymbolIndexStore
{
    public State<SymbolIndexSnapshot> Snapshot { get; } = new(SymbolIndexSnapshot.None);

    public IReadable<SymbolIndexSnapshot> Active => Snapshot;
}

/// <summary>Language servers that answer when the test says so: each question stays open until
/// <see cref="Answer"/> is called for its language.</summary>
internal sealed class ScriptedWorkspaceSymbols : IWorkspaceSymbolSource
{
    private readonly Dictionary<LanguageId, TaskCompletionSource<IReadOnlyList<SymbolRow>?>> _open = new();

    /// <summary>The languages with a server running, and the file extension each answers for.</summary>
    public Dictionary<LanguageId, string> Running { get; } = new();

    public List<string> Asked { get; } = new();

    public IReadOnlyList<ServerSymbolQuestion> AskWorkspaceSymbols(string query, TimeSpan limit, CancellationToken cancel)
    {
        Asked.Add(query);
        var questions = new List<ServerSymbolQuestion>();
        foreach (var language in Running.Keys)
        {
            var answer = new TaskCompletionSource<IReadOnlyList<SymbolRow>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _open[language] = answer;
            questions.Add(new ServerSymbolQuestion(language, answer.Task));
        }

        return questions;
    }

    public void Answer(LanguageId language, IReadOnlyList<SymbolRow>? rows) => _open[language].TrySetResult(rows);

    public LanguageId? LanguageOf(string absolutePath)
    {
        foreach (var (language, extension) in Running)
            if (absolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return language;
        return null;
    }
}

internal static class SearchFixtures
{
    /// <summary>Search over nothing, for surfaces that need one to exist and never open it.</summary>
    public static SearchEverywhereViewModel Idle(IRepoRegistry registry, IFileBrowserStore browsers) =>
        new(registry,
            new GitService(new NullActivityTracker()),
            new FixedSymbolIndex(),
            new ScriptedWorkspaceSymbols(),
            browsers,
            new ImmediateDispatcher(),
            TimeProvider.System);
}
