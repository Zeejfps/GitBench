using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;

namespace GitBench.Features.Branches;

internal sealed class MoveBranchDialogViewModel
{
    private bool _fired;

    /// <summary>Force-moves the branch here and lands HEAD on it, then closes.</summary>
    public Action Move { get; }

    public MoveBranchDialogViewModel(
        Repo repo,
        string branchName,
        string sha,
        IGitBranchOperations gitService,
        IRepoHeadStore head,
        ILocalizationService loc,
        Action onClose)
    {
        Move = () =>
        {
            if (_fired) return;
            _fired = true;
            // Must close before RunMove: RunMove stacks a dialog a later Close() would dismiss instead.
            onClose();
            head.RunMove(
                repo,
                branchName,
                () => gitService.MoveBranch(repo, branchName, sha, checkout: true),
                loc.Strings.Value.BranchesMoveTitle);
        };
    }
}
