using GitBench.Features.LocalChanges;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>The commit bar's stand-in: the two setters a write tool drives, and the text they land in.</summary>
internal sealed class FakeCommitEditor : ICommitEditor
{
    private readonly State<string> _title = new(string.Empty);
    private readonly State<string> _description = new(string.Empty);

    public IReadable<string> Title => _title;
    public IReadable<string> Description => _description;

    public void SetTitle(string value) => _title.Value = value;
    public void SetDescription(string value) => _description.Value = value;
}
