using GitBench.Features.Repos;
using GitBench.Git;
using ZGF.Gui;

namespace GitBench.Tests;

internal sealed class NoStatusIngest : IRepoStatusIngest
{
    public int Reserve(Guid repoId) => 0;
    public void Publish(Guid repoId, int reservation, GitStatusSummary? summary) { }
    public void NoteLocalCommit(Guid repoId) { }
}

internal sealed class NoopClipboard : IClipboard
{
    public string? Text { get; private set; }
    public void SetText(string text) => Text = text;
    public string? GetText() => Text;
}
