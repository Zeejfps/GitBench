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
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// The bottom strip of the Local Changes view: commit title input, growing description
/// field, amend checkbox, commit button, and an inline error banner. The controls are
/// wired two-way to a <see cref="LocalChangesViewModel"/>; there are no pass-through
/// properties or events.
/// </summary>
internal sealed class CommitBarView : ContainerView
{
    private const int Padding = 10;
    private const float CommitButtonWidth = 120f;
    private const float DescriptionMinHeight = 0f;
    private const float DescriptionMaxHeight = 240f;

    private readonly TextInputView _titleInput;
    private readonly TextInputViewKbmController _titleController;
    private readonly GrowingDescriptionField _descriptionField;
    private readonly State<bool> _amend = new(false);
    private readonly DialogButton _commitButton;
    private readonly ErrorBarView _errorBar;
    private readonly LocalChangesViewModel _vm;

    public CommitBarView(Context ctx, LocalChangesViewModel vm)
    {
        _vm = vm;
        var theme = ctx.Theme();
        var input = ctx.Require<InputSystem>();

        _titleInput = new TextInputView(ctx.Canvas)
        {
            TextWrap = TextWrap.NoWrap,
            PlaceholderText = "Commit title",
        };
        _titleInput.BindThemed(theme, s =>
        {
            _titleInput.BackgroundColor = s.TextInput.Background;
            _titleInput.TextColor = s.TextInput.Text;
            _titleInput.CaretColor = s.TextInput.Caret;
            _titleInput.SelectionRectColor = s.TextInput.Selection;
            _titleInput.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        _titleController = new TextInputViewKbmController(_titleInput, input, ctx.Get<IClipboard>());
        _titleInput.UseController(input, _titleController);

        // No PreferredHeight — let the box size to one line of text plus padding/border.
        // The input itself reports MeasureHeight = lineHeight (single line, NoWrap); the
        // PaddingView inset and the RectView border add to that.
        var titleBox = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
                    Children = { _titleInput },
                },
            },
        };
        titleBox.BindThemedBackgroundColor(theme, s => s.TextInput.Background);
        titleBox.BindThemedBorderColor(theme, s => BorderColorStyle.All(s.TextInput.Border));

        _descriptionField = new GrowingDescriptionField(ctx, DescriptionMinHeight, DescriptionMaxHeight)
        {
            PlaceholderText = "Commit description",
        };

        _commitButton = new DialogButton(ctx, "Commit", OnCommitClicked)
        {
            // MinWidthConstraint, not a fixed Width: a set Width is a hard override in
            // View.MeasureWidth, pinning the button at 120px while the centered content row
            // overflows. The busy state ("Committing" + loader icon) is wider than "Commit",
            // so a fixed width spills text past the button bounds. MinWidth keeps the resting
            // size but lets it grow to contain the busy label.
            MinWidthConstraint = CommitButtonWidth,
            Height = 28,
        };

        var amendCheckbox = new CheckboxWidget { Label = "Amend", Checked = _amend }.WithController<KbmController>().BuildView(ctx);

        var buttonRow = new FlexRowView
        {
            MainAxisAlignment = MainAxisAlignment.SpaceBetween,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { amendCheckbox, _commitButton },
        };

        _errorBar = new ErrorBarView(ctx);
        var column = new ColumnView
        {
            Gap = 8,
            Children = { _errorBar, titleBox, _descriptionField, buttonRow },
        };

        var bar = new RectView
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle
                    {
                        Left = Padding,
                        Right = Padding,
                        Top = Padding,
                        Bottom = Padding,
                    },
                    Children = { column },
                },
            },
        };
        bar.BindThemedBackgroundColor(theme, s => s.CommitBar.Background);
        bar.BindThemedBorderColor(theme, s => new BorderColorStyle { Top = s.CommitBar.TopBorder });
        AddChildToSelf(bar);

        _titleInput.BindTwoWay(vm.Title, vm.SetTitle);
        _descriptionField.BindTwoWay(vm.Description, vm.SetDescription);

        // Amend checkbox is two-way against vm.Amend; record equality stops the loop.
        this.Bind(vm.Amend, b => _amend.Value = b);
        _amend.Changed += b => vm.SetAmend(b);

        this.Bind(vm.CommitEnabled, b => _commitButton.IsEnabled.Value = b);
        this.Bind(vm.OpError, m => _errorBar.Message.Value = m);

        this.Bind(vm.CommitBusy, _ => UpdateCommitButton());
        this.Bind(vm.IsMerging, _ => UpdateCommitButton());
        this.Bind(vm.CommitRotation, r => _commitButton.IconRotation = r);
    }

    // Adds the commit title and description to the shared focus ring after the unstaged
    // file list, ahead of the commit button.
    public void RegisterFocusStops(FocusRing ring)
    {
        var titleStop = ring.Add(_titleController.BeginEditing, _titleController.EndEditing);
        _titleController.OnTab = () => ring.Next(titleStop);
        _titleController.OnShiftTab = () => ring.Previous(titleStop);

        var descriptionStop = ring.Add(_descriptionField.BeginEditing, _descriptionField.EndEditing);
        _descriptionField.OnTab = () => ring.Next(descriptionStop);
        _descriptionField.OnShiftTab = () => ring.Previous(descriptionStop);
    }

    // Registered separately (after the file list) so the commit button is the last stop in
    // the cycle. It only participates while enabled, and shows its hover chrome while
    // focused; Enter commits.
    public void RegisterCommitButtonStop(FocusRing ring)
    {
        var commitStop = ring.Add(
            _commitButton.FocusSelf,
            _commitButton.Blur,
            canFocus: () => _commitButton.IsEnabled.Value);
        _commitButton.OnTab = () => ring.Next(commitStop);
        _commitButton.OnShiftTab = () => ring.Previous(commitStop);
    }

    private void UpdateCommitButton()
    {
        var busy = _vm.CommitBusy.Value;
        var merging = _vm.IsMerging.Value;
        _commitButton.Icon = busy ? LucideIcons.Loader : string.Empty;
        _commitButton.Label = busy
            ? (merging ? "Committing merge" : "Committing")
            : (merging ? "Commit merge" : "Commit");
        if (!busy) _commitButton.IconRotation = 0f;
    }

    private void OnCommitClicked() => _vm.Commit();
}
