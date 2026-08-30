using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Terminal;

/// <summary>
/// Confirmation modal shown when the application is asked to close while a shell is still running.
/// Closing kills every shell, and a shell mid-build or mid-deploy is not something to lose to a
/// mistyped Cmd+Q.
/// </summary>
/// <remarks>
/// Names the repositories rather than only counting them: "3 sessions" tells the reader to stop
/// without telling them what they would be stopping, which is the one thing that decides the answer.
/// </remarks>
internal sealed record ConfirmQuitDialog : Widget
{
    public required IReadOnlyList<Guid> RepoIds { get; init; }
    public required Action OnClose { get; init; }

    /// <summary>Runs when the user agrees to close. Kept a callback so this stays a dialog rather
    /// than something that knows how an application ends.</summary>
    public required Action OnConfirm { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var registry = ctx.Require<IRepoRegistry>();
        var s = ctx.Localization().Strings.Value;

        // Registry order, not the order the ids arrived in: this list is read against the repo bar.
        var names = registry.Repos
            .Where(repo => RepoIds.Contains(repo.Id))
            .Select(repo => repo.DisplayName)
            .ToArray();
        var count = RepoIds.Count;

        return new Dialog
        {
            Title = s.TerminalQuitTitle(count),
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            CancelLabel = s.TerminalQuitCancel,
            Action = (s.TerminalQuitAction(count), DialogButtonRole.Destructive, () =>
            {
                OnClose();
                OnConfirm();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = s.TerminalQuitBody(count),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = string.Join(", ", names),
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.RowText),
                },
            ],
        };
    }
}
