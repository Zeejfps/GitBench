using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Modal shown when a git operation fails. Surfaces git's stderr block verbatim (so it
/// matches what the user would see running the command in a terminal) inside a vertically
/// scrollable monospace body — long blocks like "your local changes to the following
/// files would be overwritten by merge" plus a file list stay fully readable instead of
/// being clipped to the first line.
///
/// The body wraps at the dialog's content width and only scrolls vertically; that keeps
/// long lines visible without letting wide content stretch the dialog horizontally.
/// </summary>
internal sealed record OperationErrorDialog : Widget<DialogState>
{
    private const float CloseButtonSize = 28f;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public required Action OnClose { get; init; }

    /// <summary>Caller-supplied in-place fix (e.g. unlock worktree). Falls back to stale-lock removal.</summary>
    public OperationErrorRecovery? Recovery { get; init; }

    protected override DialogState CreateState(Context ctx) => new(OnClose);

    // The one recovery this dialog offers, if any; null once it has run successfully so the same
    // fix can't be applied twice. _recoveryStatus carries the outcome line shown beside the button.
    private OperationErrorRecovery? _recovery;
    private readonly State<bool> _recoveryDone = new(false);
    private readonly State<string?> _recoveryStatus = new(null);

    protected override IWidget Build(Context ctx, DialogState state)
    {
        var s = ctx.Localization().Strings.Value;
        _recovery = Recovery ?? DetectLockRecovery(s);
        return new Box
        {
            Width = DialogFrame.WidthWide,
            Height = 360,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DialogFrame.DefaultBorderRadius),
            Background = Theme.Color(t => t.DialogFrame.Background),
            BorderColor = Theme.BorderColor(t => BorderColorStyle.All(t.DialogFrame.Border)),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(DialogFrame.DefaultPadding),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Lg,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                BuildHeader(s),
                                new Box { Height = 1, Background = Theme.Color(t => t.DialogFrame.HeaderSeparator) },
                                new Grow { Child = BuildScrollHost() },
                                new Row
                                {
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Gap = Spacing.Sm,
                                    Children =
                                    [
                                        new SecondaryDialogButton
                                        {
                                            Label = _recovery?.ButtonLabel ?? string.Empty,
                                            Command = new Command(RunRecovery),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                            Visible = Prop.Bind(() => _recovery != null && !_recoveryDone.Value),
                                        }.WithController<KbmController>(),
                                        new Text
                                        {
                                            Value = Prop.Bind(() => _recoveryStatus.Value ?? string.Empty),
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                                            Visible = Prop.Bind(() => _recoveryStatus.Value != null),
                                        },
                                        new Spacer(),
                                        new ActionDialogButton
                                        {
                                            Label = s.CommonOk,
                                            Role = DialogButtonRole.Primary,
                                            Command = new Command(OnClose),
                                            Height = DialogFrame.DefaultButtonHeight,
                                            MinWidth = DialogFrame.DefaultButtonMinWidth,
                                        }.WithController<KbmController>(),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private void RunRecovery()
    {
        if (_recovery is not { } recovery) return;

        var failure = recovery.Fix();
        _recoveryStatus.Value = failure ?? recovery.SuccessText;
        if (failure is null) _recoveryDone.Value = true;
    }

    // The always-available fix: git leaves an absolute *.lock path in its failure text when a crashed
    // process left one behind, so this recovery is a pure function of the message — no repo needed.
    private OperationErrorRecovery? DetectLockRecovery(Strings s)
    {
        if (GitLockFile.Detect(Message) is not { } lockPath) return null;
        return new OperationErrorRecovery(s.OperationsLockRemove, s.OperationsLockRemoved, () => GitLockFile.Remove(lockPath));
    }

    // Symmetric left spacer keeps the title centered: it matches the combined width of the two
    // right-side buttons (close + copy), so the grown, center-aligned title lands in the middle.
    private IWidget BuildHeader(Strings s) => new Row
    {
        Height = Sizes.ControlHeight,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Box { Width = CloseButtonSize * 2 },
            new Grow
            {
                Child = new Text
                {
                    Value = Title,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogFrame.TitleText),
                },
            },
            new DialogCopyButton { GetText = () => Message, Tooltip = s.OperationsErrorCopyTooltip },
            new DialogCloseButton { OnClose = OnClose },
        ],
    };

    // The error body scrolls vertically only: the region forces its content to viewport width, which
    // gives the wrapping text the bound it needs instead of one long line stretching the dialog.
    private IWidget BuildScrollHost() => new DialogInsetCard
    {
        Children =
        [
            new ScrollRegion
            {
                FillParent = true,
                Content = new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Md, Top = Spacing.Sm, Bottom = Spacing.Sm },
                    Children =
                    [
                        new Text
                        {
                            Value = Message,
                            FontFamily = MonoFonts.Regular,
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(s => s.DialogBody.BodyText),
                        },
                    ],
                },
            },
        ],
    };
}
