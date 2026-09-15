using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Features.Editor;
using GitBench.Features.Terminal;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Repos;

/// <summary>
/// Confirmation modal shown when the user picks "Remove repo" on a RepoBar row. Removal
/// only drops the repository from GitBench's sidebar — the files on disk are untouched —
/// but it discards worktree/submodule rows and any hotkey, so it's gated behind this prompt.
/// </summary>
internal sealed record RemoveRepoDialog : Widget
{
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var registry = ctx.Require<IRepoRegistry>();
        var s = ctx.Localization().Strings.Value;

        // Removal drops the repo from the registry, and dropping it disposes its terminal — so a
        // shell running here dies with it. Said before the fact rather than discovered after.
        var terminals = ctx.Require<ITerminalSessionStore>();
        var endsAShell = terminals.HasLiveShell(Repo.Id);

        var losing = RepoRemoval.UnsavedFiles(registry, ctx.Require<IDocumentStore>(), Repo.Id);

        return new Dialog
        {
            Title = s.ReposRepoRemoveTitle,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            Action = (s.CommonRemove, DialogButtonRole.Destructive, () =>
            {
                registry.RemoveRepo(Repo.Id);
                OnClose();
            }),
            ConfirmKeys = true,
            Body =
            [
                new DialogBodyText { Value = s.ReposRepoRemoveBody(Repo.DisplayName, AppIdentity.DisplayName) },
                .. endsAShell
                    ?
                    [
                        new Text
                        {
                            Value = s.TerminalRepoRemoveWarning,
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(t => t.DialogFrame.WarningText),
                        },
                    ]
                    : Array.Empty<IWidget>(),
                .. losing.Count > 0
                    ?
                    [
                        new Text
                        {
                            Value = s.ReposRepoRemoveUnsavedWarning(string.Join(", ", losing)),
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(t => t.DialogFrame.WarningText),
                        },
                    ]
                    : Array.Empty<IWidget>(),
            ],
        };
    }
}

/// <summary>What removing a repository row costs that is written down nowhere else.</summary>
internal static class RepoRemoval
{
    public static IReadOnlyList<string> UnsavedFiles(
        IRepoRegistry registry, IDocumentStore documents, Guid repoId)
    {
        var cascade = registry.RemovalCascade(repoId);
        if (cascade.Count == 0) return [];

        var ids = cascade.Select(r => r.Id).ToHashSet();
        return documents.Unsaved()
            .Where(f => ids.Contains(f.RepoId))
            .Select(f => UnsavedFileName.Of(registry, f))
            .ToArray();
    }
}
