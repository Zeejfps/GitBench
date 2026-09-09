using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;

namespace GitBench.Features.Branches;

internal sealed class MoveBranchDialogViewModel : IDialogViewModel
{
    private bool _fired;

    /// <summary>Force-moves the branch here and lands HEAD on it, then closes.</summary>
    public Action Move { get; }

    public event Action? CloseRequested;

    public MoveBranchDialogViewModel(
        MoveBranchRequest request,
        IGitBranchOperations gitService,
        IRepoHeadStore head,
        ILocalizationService loc)
    {
        Move = () =>
        {
            if (_fired) return;
            _fired = true;
            // Must close before RunMove: RunMove stacks a dialog a later Close() would dismiss instead.
            CloseRequested?.Invoke();
            head.RunMove(
                request.Repo,
                request.BranchName,
                () => gitService.MoveBranch(request.Repo, request.BranchName, request.Sha, checkout: true),
                loc.Strings.Value.BranchesMoveTitle);
        };
    }

    public void Dispose() { }
}

internal readonly record struct MoveBranchRequest(Repo Repo, string BranchName, string Sha);
