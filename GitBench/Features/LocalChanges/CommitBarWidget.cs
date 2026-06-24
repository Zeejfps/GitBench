using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The commit bar at the bottom of the Local Changes view: edits the pending commit's title,
/// description, and amend flag and triggers the commit, all bound two-way to the
/// <see cref="LocalChangesViewModel"/>. Adds its editable fields and the commit button to the
/// supplied <see cref="FocusRing"/> as it builds, so Tab reaches them after the file list.
/// </summary>
internal sealed record CommitBarWidget : Widget
{
    private const int Pad = 10;
    private const float CommitButtonWidth = 120f;
    private const float DescriptionMinHeight = 0f;
    private const float DescriptionMaxHeight = 240f;

    public required FocusRing FocusRing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<LocalChangesViewModel>();
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var loc = ctx.Localization();

        var titleInput = new TextInputView(ctx.Canvas)
        {
            TextWrap = TextWrap.NoWrap,
        };
        titleInput.Bind(loc.Strings, s => titleInput.PlaceholderText = s.LocalchangesCommitTitlePlaceholder);
        titleInput.BindThemed(theme, s =>
        {
            titleInput.BackgroundColor = s.TextInput.Background;
            titleInput.TextColor = s.TextInput.Text;
            titleInput.CaretColor = s.TextInput.Caret;
            titleInput.SelectionRectColor = s.TextInput.Selection;
            titleInput.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        var titleController = new TextInputViewKbmController(titleInput, input, ctx.Get<IClipboard>());
        titleInput.UseController(input, titleController);
        titleInput.BindTwoWay(vm.Title, vm.SetTitle);

        var descriptionField = new GrowingDescriptionField(ctx, DescriptionMinHeight, DescriptionMaxHeight);
        descriptionField.Bind(loc.Strings, s => descriptionField.PlaceholderText = s.LocalchangesCommitDescriptionPlaceholder);
        descriptionField.BindTwoWay(vm.Description, vm.SetDescription);

        var commitWidget = new SecondaryDialogButton
        {
            Label = L.T(s =>
            {
                var merging = vm.IsMerging.Value;
                if (vm.CommitBusy.Value) return merging ? s.LocalchangesCommitMergeButtonBusy : s.LocalchangesCommitButtonBusy;
                return merging ? s.LocalchangesCommitMergeButton : s.LocalchangesCommitButton;
            }),
            Icon = Prop.Bind<string?>(() => vm.CommitBusy.Value ? LucideIcons.Loader : null),
            IconRotation = Prop.Bind(vm.CommitRotation),
            Command = new Command(() => vm.Commit(), vm.CommitEnabled),
            // MinWidth, not a fixed Width: the busy label ("Committing") is wider than "Commit", so a
            // fixed width would clip it; MinWidth keeps the resting size but lets it grow to fit.
            MinWidth = CommitButtonWidth,
            Height = Sizes.ControlHeight,
        };
        var commitButton = commitWidget.BuildView(ctx);
        var commitController = new KbmController(commitWidget.State);
        commitButton.UseController(input, commitController);

        var amend = new State<bool>(false);

        // Amend checkbox is two-way against vm.Amend; record equality stops the loop. Anchored on the
        // commit button (a persistent child) so it releases on unmount.
        commitButton.Bind(vm.Amend, b => amend.Value = b);
        amend.Changed += b => vm.SetAmend(b);

        RegisterFocusStops();

        return new Box
        {
            Background = Theme.Color(s => s.CommitBar.Background),
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.CommitBar.TopBorder }),
            BorderSize = new BorderSizeStyle { Top = 1 },
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Pad, Right = Pad, Top = Pad, Bottom = Pad },
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new ErrorBarView { Message = vm.OpError },
                                TitleBox(titleInput),
                                new Raw { View = descriptionField },
                                new Row
                                {
                                    MainAxis = MainAxisAlignment.SpaceBetween,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new CheckboxWidget { Label = L.T(s => s.LocalchangesAmendCheckbox), Checked = amend }
                                            .WithController<KbmController>(),
                                        new Raw { View = commitButton },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        // Commit title and description join the ring after the unstaged file list, with the
        // commit button last so it's the final stop in the cycle. The button only participates
        // while enabled, and shows its hover chrome while focused; Enter commits.
        void RegisterFocusStops()
        {
            var titleStop = FocusRing.Add(titleController.BeginEditing, titleController.EndEditing);
            titleController.OnTab = () => FocusRing.Next(titleStop);
            titleController.OnShiftTab = () => FocusRing.Previous(titleStop);

            var descriptionStop = FocusRing.Add(descriptionField.BeginEditing, descriptionField.EndEditing);
            descriptionField.OnTab = () => FocusRing.Next(descriptionStop);
            descriptionField.OnShiftTab = () => FocusRing.Previous(descriptionStop);

            var commitStop = FocusRing.Add(
                () => input.StealFocus(commitController),
                () => input.Blur(commitController),
                canFocus: () => commitWidget.State.Enabled.Value);
            commitController.OnTab = () => FocusRing.Next(commitStop);
            commitController.OnShiftTab = () => FocusRing.Previous(commitStop);
        }
    }

    // Single-line title input boxed with the text-input chrome. No PreferredHeight — the box
    // sizes to one line of text plus padding and border (the input reports a single line's
    // height for NoWrap, and the padding/border add to it).
    private static IWidget TitleBox(TextInputView titleInput) => new Box
    {
        Background = Theme.Color(s => s.TextInput.Background),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.TextInput.Border)),
        BorderSize = BorderSizeStyle.All(1),
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Xs, Bottom = Spacing.Xs },
                Children = [new Raw { View = titleInput }],
            },
        ],
    };
}
