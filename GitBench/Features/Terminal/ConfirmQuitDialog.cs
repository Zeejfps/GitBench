using GitBench.App;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Terminal;

/// <summary>
/// Confirmation modal shown when the application is asked to end while a shell is still running —
/// a quit, or a restart into a staged update. Either kills every shell, and a shell mid-build or
/// mid-deploy is not something to lose to a mistyped Cmd+Q or an update banner.
/// </summary>
/// <remarks>
/// Names the repositories rather than only counting them: "3 sessions" tells the reader to stop
/// without telling them what they would be stopping, which is the one thing that decides the answer.
/// </remarks>
internal sealed record ConfirmQuitDialog : Widget
{
    public required IReadOnlyList<Guid> RepoIds { get; init; }
    public required Action OnClose { get; init; }

    /// <summary>What agreeing leads to, which decides the wording: the app ending, or ending and
    /// coming back on the staged update.</summary>
    public AppExitKind Kind { get; init; } = AppExitKind.Quit;

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
        var (title, body, action) = Kind == AppExitKind.UpdateRestart
            ? (s.TerminalUpdateRestartTitle(count), s.TerminalUpdateRestartBody(count), s.TerminalUpdateRestartAction(count))
            : (s.TerminalQuitTitle(count), s.TerminalQuitBody(count), s.TerminalQuitAction(count));

        return new Dialog
        {
            Title = title,
            OnClose = OnClose,
            Width = DialogFrame.WidthCompact,
            CancelLabel = s.TerminalQuitCancel,
            Action = (action, DialogButtonRole.Destructive, () =>
            {
                OnClose();
                OnConfirm();
            }),
            ConfirmKeys = true,
            Body =
            [
                new Text
                {
                    Value = body,
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
