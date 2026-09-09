using GitBench.Features.Editor;
using GitBench.Localization;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>What a file browser under test is given for its open documents.</summary>
internal static class TestDocuments
{
    private static readonly ILocalizationService Localization =
        new LocalizationService(new State<Locale>(Locale.En));

    public static RepoDocuments ForOneRepo() => new(Localization);

    public static void Discard(IReadOnlyList<string> files, Action close) => close();

    public static void KeepEdits(IReadOnlyList<string> files, Action reload) { }

    public sealed class Empty : IDocumentStore
    {
        private readonly Dictionary<Guid, RepoDocuments> _repos = new();

        public List<UnsavedFile> UnsavedFiles { get; } = [];

        public event Action<string>? Edited;

        public event Action<EditorBuffer>? Opened;

        public event Action<EditorBuffer>? Closed;

        public IRepoDocuments For(Guid repoId)
        {
            if (_repos.TryGetValue(repoId, out var existing)) return existing;
            var documents = ForOneRepo();
            documents.Edited += path => Edited?.Invoke(path);
            documents.Opened += buffer => Opened?.Invoke(buffer);
            documents.Closed += buffer => Closed?.Invoke(buffer);
            return _repos[repoId] = documents;
        }

        public string? UnsavedTextAt(string path)
        {
            foreach (var documents in _repos.Values)
                if (documents.UnsavedTextAt(path) is { } text) return text;
            return null;
        }

        public void MarkSaved(string path)
        {
            foreach (var documents in _repos.Values) documents.MarkSaved(path);
        }

        public IReadOnlyList<UnsavedFile> Unsaved() => UnsavedFiles;
    }
}

internal sealed class NoUnsavedEdits : IUnsavedEditsGuard
{
    public void Guard(OverwriteScope scope, Action proceed) => proceed();
}
