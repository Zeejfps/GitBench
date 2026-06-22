using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.StatusBar;
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

        var titleInput = new TextInputView(ctx.Canvas)
        {
            TextWrap = TextWrap.NoWrap,
            PlaceholderText = "Commit title",
        };
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

        var descriptionField = new GrowingDescriptionField(ctx, DescriptionMinHeight, DescriptionMaxHeight)
        {
            PlaceholderText = "Commit description",
        };
        descriptionField.BindTwoWay(vm.Description, vm.SetDescription);

        var commitButton = new DialogButton(ctx, "Commit", () => vm.Commit())
        {
            // MinWidthConstraint, not a fixed Width: a set Width is a hard override in
            // View.MeasureWidth, pinning the button at 120px while the centered content row
            // overflows. The busy state ("Committing" + loader icon) is wider than "Commit",
            // so a fixed width spills text past the button bounds. MinWidth keeps the resting
            // size but lets it grow to contain the busy label.
            MinWidthConstraint = CommitButtonWidth,
            Height = 28,
        };

        var amend = new State<bool>(false);

        // The commit controls have no widget form, so they ride in as raw views. The VM-driven
        // bindings anchor on the commit button (a persistent child) so they release on unmount.
        commitButton.Bind(vm.CommitEnabled, b => commitButton.IsEnabled.Value = b);
        commitButton.Bind(vm.CommitBusy, _ => UpdateCommitButton());
        commitButton.Bind(vm.IsMerging, _ => UpdateCommitButton());
        commitButton.Bind(vm.CommitRotation, r => commitButton.IconRotation = r);

        // Amend checkbox is two-way against vm.Amend; record equality stops the loop.
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
                            Gap = 8,
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
                                        new CheckboxWidget { Label = "Amend", Checked = amend }
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

        void UpdateCommitButton()
        {
            var busy = vm.CommitBusy.Value;
            var merging = vm.IsMerging.Value;
            commitButton.Icon = busy ? LucideIcons.Loader : string.Empty;
            commitButton.Label = busy
                ? (merging ? "Committing merge" : "Committing")
                : (merging ? "Commit merge" : "Commit");
            if (!busy) commitButton.IconRotation = 0f;
        }

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
                commitButton.FocusSelf,
                commitButton.Blur,
                canFocus: () => commitButton.IsEnabled.Value);
            commitButton.OnTab = () => FocusRing.Next(commitStop);
            commitButton.OnShiftTab = () => FocusRing.Previous(commitStop);
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
        BorderRadius = BorderRadiusStyle.All(3),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
                Children = [new Raw { View = titleInput }],
            },
        ],
    };
}
