using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Operations;
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
/// Edits the pending commit's title, description, and (normally) amend flag and triggers the commit,
/// all bound two-way to the <see cref="LocalChangesViewModel"/>. Two instances exist, both in the Local
/// Changes footer: the normal bar, and — with <see cref="ShowOperationChrome"/> set — the merge bar that
/// takes its place during a merge, trading the amend toggle for the operation's status header and an
/// abort button. Editable fields and the commit button join the supplied <see cref="FocusRing"/> while
/// <see cref="Active"/>.
/// </summary>
internal sealed record CommitBarWidget : Widget
{
    private const float CommitButtonWidth = 120f;
    private const float DescriptionMinHeight = 0f;
    private const float DescriptionMaxHeight = 240f;

    public required FocusRing FocusRing { get; init; }
    public required LocalChangesViewModel Vm { get; init; }

    /// <summary>Whether this bar is the live editing surface — gates focus traversal into its fields.
    /// The caller binds the bar's visibility to the same condition.</summary>
    public required IReadable<bool> Active { get; init; }

    /// <summary>Replaces the amend toggle with the operation status header and an abort button — set on
    /// the workspace-footer instance that owns an in-progress merge / unmerged-paths commit.</summary>
    public bool ShowOperationChrome { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = Vm;
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();
        var loc = ctx.Localization();
        var operation = ctx.Require<OperationViewModel>();

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

        // The amend toggle is two-way against vm.Amend (record equality stops the loop); only the
        // normal bar carries it — the merge bar can't amend into an in-progress merge.
        var amend = new State<bool>(false);
        if (!ShowOperationChrome)
        {
            commitButton.Bind(vm.Amend, b => amend.Value = b);
            amend.Changed += b => vm.SetAmend(b);
        }

        RegisterFocusStops();

        return new FooterPanel { Children = ColumnRows() };

        IWidget[] ColumnRows()
        {
            var rows = new List<IWidget>(6);
            if (ShowOperationChrome) rows.Add(new OperationStatusHeader());
            // Staging progress heads the normal bar in the Review layout; the merge bar's own status
            // header owns that slot instead.
            else rows.Add(new StagingProgressRow());
            rows.Add(new ErrorBarView { Message = vm.OpError });
            rows.Add(TitleBox(titleInput));
            rows.Add(new Raw { View = descriptionField });
            rows.Add(ButtonRow());
            return rows.ToArray();
        }

        // Commit button always anchors the right edge (via the grow spacer); the normal bar puts the
        // amend toggle at the left, the merge bar puts the abort button to the commit's right.
        IWidget ButtonRow()
        {
            var children = new List<IWidget>(4);
            if (!ShowOperationChrome)
                children.Add(new CheckboxWidget { Label = L.T(s => s.LocalchangesAmendCheckbox), Checked = amend }
                    .WithController<KbmController>());
            children.Add(new Grow { Child = Empty.Widget });
            children.Add(new Raw { View = commitButton });
            if (ShowOperationChrome)
                children.Add(AbortButton(operation));
            return new Row { Gap = Spacing.Sm, CrossAxis = CrossAxisAlignment.Center, Children = children.ToArray() };
        }

        // Commit title and description join the ring after the unstaged file list, with the
        // commit button last so it's the final stop in the cycle. Stops are live only while this
        // bar is the active surface; the button additionally needs to be enabled. Enter commits.
        void RegisterFocusStops()
        {
            var titleStop = FocusRing.Add(titleController.BeginEditing, titleController.EndEditing,
                canFocus: () => Active.Value);
            titleController.OnTab = () => FocusRing.Next(titleStop);
            titleController.OnShiftTab = () => FocusRing.Previous(titleStop);

            var descriptionStop = FocusRing.Add(descriptionField.BeginEditing, descriptionField.EndEditing,
                canFocus: () => Active.Value);
            descriptionField.OnTab = () => FocusRing.Next(descriptionStop);
            descriptionField.OnShiftTab = () => FocusRing.Previous(descriptionStop);

            var commitStop = FocusRing.Add(
                () => input.StealFocus(commitController),
                () => input.Blur(commitController),
                canFocus: () => Active.Value && commitWidget.State.Enabled.Value);
            commitController.OnTab = () => FocusRing.Next(commitStop);
            commitController.OnShiftTab = () => FocusRing.Previous(commitStop);
        }
    }

    // Aborts the in-progress merge / unmerged-paths operation — the same outline-danger button the
    // standalone operation panel uses, surfaced here so the merge has one panel instead of two.
    private static IWidget AbortButton(OperationViewModel operation) => new ButtonWidget
    {
        Style = ButtonStyle.Outline(s => s.Status.DangerBar),
        Command = operation.Abort,
        Children =
        [
            new ButtonIcon { Value = LucideIcons.X },
            new ButtonLabel { Value = L.T(s => s.CommonAbort) },
        ],
    }.WithController<KbmController>();

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
