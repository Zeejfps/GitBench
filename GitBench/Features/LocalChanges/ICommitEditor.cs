using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The commit box the user is looking at. Writing through it is what makes the text land in the
/// real commit bar — its bindings update the way they do when the text is typed.
/// </summary>
internal interface ICommitEditor
{
    IReadable<string> Title { get; }

    IReadable<string> Description { get; }

    void SetTitle(string value);

    void SetDescription(string value);
}
