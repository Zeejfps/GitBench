using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Notifications;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// Modal shown when the user picks "Create Tag" on a commit in the history. Mirrors Fork's
/// "Create Tag" dialog: the target commit, a tag name, an optional annotation message, and a
/// "push to all remotes" toggle. A non-empty message yields an annotated tag, otherwise a
/// lightweight one — see <see cref="IGitTagOperations.CreateTag"/>.
/// </summary>
internal sealed record CreateTagDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Summary { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var sha = Sha;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitTagOperations>();
        var bus = ctx.Require<IMessageBus>();
        var loc = ctx.Require<ILocalizationService>();
        var s = loc.Strings.Value;

        var name = new State<string>(string.Empty);
        var message = new State<string>(string.Empty);
        var pushToAllRemotes = new State<bool>(true);

        // Validate the trimmed value: Create trims before handing the name to git, so a
        // surrounding space shouldn't read as an error the user can't see the cause of.
        var nameStatus = new Derived<FieldStatus?>(() =>
        {
            var strings = loc.Strings.Value;
            return RefNameRules.Validate(name.Value.Trim(), strings, strings.RefnameNounTag);
        });
        var gate = new Derived<bool>(() =>
            name.Value.Trim().Length > 0 && RefNameRules.IsValid(name.Value.Trim()));

        var create = AsyncCommand.ForOutcome(
            ctx.Require<IUiDispatcher>(),
            work: () => gitService.CreateTag(repo, name.Value.Trim(), message.Value, sha, pushToAllRemotes.Value),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                bus.Broadcast(new ShowToastMessage(ToastIntent.Success(loc.Strings.Value.ToastTagCreated)));
                onClose();
            },
            gate: gate);

        // The message field keeps its own multi-line controller so Enter inserts a newline there.
        var messageField = new GrowingDescriptionField(ctx, 72f, 200f) { PlaceholderText = s.CommitsCreateTagMessagePlaceholder };
        messageField.BindTwoWay(message, v => message.Value = v);

        return new Dialog
        {
            Title = s.CommitsCreateTagTitle,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (s.CommonCreate, DialogButtonRole.Primary),
            Command = create,
            // Reflect the toggle in the primary button's label, like Fork ("Create and Push").
            BindActionLabel = new Derived<string>(() => pushToAllRemotes.Value ? s.CommitsCreateTagPushAction : s.CommonCreate),
            Body =
            [
                new Text
                {
                    Value = s.CommitsCreateTagDesc,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledRow { Label = s.CommitsCreateTagLocationLabel, Value = CommitValue(ctx, ShortSha, Summary) },
                new LabeledInput
                {
                    Label = s.CommitsCreateTagNameLabel,
                    Value = name,
                    Placeholder = s.CommitsCreateTagNamePlaceholder,
                    Status = nameStatus,
                },
                new Column
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Text
                        {
                            Value = s.CommonMessage,
                            Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
                        },
                        new Raw { View = messageField },
                    ],
                },
                new CheckboxWidget { Label = s.CommitsCreateTagPushCheckbox, Checked = pushToAllRemotes, Height = Sizes.RowHeight }.WithController<KbmController>(),
            ],
        };
    }

    private static IWidget CommitValue(Context ctx, string shortSha, string summary)
    {
        // Ellipsis (…) on overflow rather than NoWrap-in-a-ClippingView: the clip let the
        // single line run past the dialog's right edge instead of truncating it. Ellipsis
        // measures against the laid-out Grow width and cuts the text with a trailing "…".
        var theme = ctx.Theme();
        var summaryLabel = new TextView(ctx.Canvas)
        {
            Text = summary,
            VerticalTextAlignment = TextAlignment.Center,
            TextOverflow = TextOverflow.Ellipsis,
        };
        summaryLabel.BindTextColor(() => theme.Styles.Value.DialogBody.BodyText);

        return new Row
        {
            Gap = Spacing.Md,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Text
                {
                    Value = "●",
                    FontSize = FontSize.Caption,
                    Width = Sizes.Icon,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogBody.BodyText),
                },
                new Text
                {
                    Value = shortSha,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogFrame.TitleText),
                },
                new Grow { Child = new Raw { View = summaryLabel } },
            ],
        };
    }
}
