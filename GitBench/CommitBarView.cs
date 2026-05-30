using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// The bottom strip of the Local Changes view: commit title input, growing description
/// field, amend checkbox, commit button, and an inline error banner. <see cref="Bind"/>
/// wires the controls two-way to a <see cref="LocalChangesViewModel"/>; there are no
/// pass-through properties or events.
/// </summary>
internal sealed class CommitBarView : MultiChildView, IBind<LocalChangesViewModel>
{
    private const int Padding = 10;
    private const float CommitButtonWidth = 120f;
    private const float DescriptionMinHeight = 0f;
    private const float DescriptionMaxHeight = 240f;

    private readonly TextInputView _titleInput;
    private readonly TextInputViewKbmController _titleController;
    private readonly GrowingDescriptionField _descriptionField;
    private readonly CheckboxView _amendCheckbox;
    private readonly DialogButton _commitButton;
    private readonly ErrorBarView _errorBar;

    public CommitBarView()
    {
        _titleInput = new TextInputView
        {
            TextWrap = TextWrap.NoWrap,
            PlaceholderText = "Commit title",
        };
        _titleInput.BindThemed(s =>
        {
            _titleInput.BackgroundColor = s.TextInput.Background;
            _titleInput.TextColor = s.TextInput.Text;
            _titleInput.CaretColor = s.TextInput.Caret;
            _titleInput.SelectionRectColor = s.TextInput.Selection;
            _titleInput.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        _titleController = new TextInputViewKbmController(_titleInput);
        _titleInput.UseController(_ => _titleController);

        // No PreferredHeight — let the box size to one line of text plus padding/border.
        // The input itself reports MeasureHeight = lineHeight (single line, NoWrap), and the
        // RectView adds its own padding+border on top.
        var titleBox = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
            Children = { _titleInput },
        };
        titleBox.BindThemedBackgroundColor(s => s.TextInput.Background);
        titleBox.BindThemedBorderColor(s => BorderColorStyle.All(s.TextInput.Border));

        _descriptionField = new GrowingDescriptionField(DescriptionMinHeight, DescriptionMaxHeight)
        {
            PlaceholderText = "Commit description",
        };

        _commitButton = new DialogButton("Commit", OnCommitClicked)
        {
            Width = CommitButtonWidth,
            Height = 28,
        };

        _amendCheckbox = new CheckboxView("Amend");

        var buttonRow = new FlexRowView
        {
            MainAxisAlignment = MainAxisAlignment.SpaceBetween,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _amendCheckbox, _commitButton },
        };

        _errorBar = new ErrorBarView();
        var column = new ColumnView
        {
            Gap = 8,
            Children = { _errorBar, titleBox, _descriptionField, buttonRow },
        };

        var bar = new RectView
        {
            BorderSize = new BorderSizeStyle { Top = 1 },
            Padding = new PaddingStyle
            {
                Left = Padding,
                Right = Padding,
                Top = Padding,
                Bottom = Padding,
            },
            Children = { column },
        };
        bar.BindThemedBackgroundColor(s => s.CommitBar.Background);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Top = s.CommitBar.TopBorder });
        AddChildToSelf(bar);
    }

    private LocalChangesViewModel? _vm;

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

    public void Bind(LocalChangesViewModel vm)
    {
        _vm = vm;

        // Title: VM → input (with feedback guard) and input → VM. The state's record
        // equality stops the feedback loop on its own — writing the same string back is a
        // no-op — but checking the span first skips the input Clear+Enter churn.
        vm.Title.Subscribe(s =>
        {
            if (_titleInput.Text.SequenceEqual(s.AsSpan())) return;
            _titleInput.Clear();
            if (s.Length > 0) _titleInput.Enter(s.AsSpan());
        });
        _titleInput.TextChanged += () => vm.SetTitle(_titleInput.Text.ToString());

        vm.Description.Subscribe(s =>
        {
            if (_descriptionField.Text.SequenceEqual(s.AsSpan())) return;
            _descriptionField.SetText(s.AsSpan());
        });
        _descriptionField.TextChanged += () => vm.SetDescription(_descriptionField.Text.ToString());

        // Amend checkbox is two-way against vm.Amend; record equality stops the loop.
        vm.Amend.Subscribe(b => _amendCheckbox.IsChecked.Value = b);
        _amendCheckbox.IsChecked.Changed += b => vm.SetAmend(b);

        vm.CommitEnabled.Subscribe(b => _commitButton.IsEnabled.Value = b);
        _errorBar.Message.BindTo(vm.OpError);

        vm.CommitBusy.Subscribe(b =>
        {
            _commitButton.Icon = b ? LucideIcons.Loader : string.Empty;
            _commitButton.Label = b ? "Committing" : "Commit";
            if (!b) _commitButton.IconRotation = 0f;
        });
        vm.CommitRotation.Subscribe(r => _commitButton.IconRotation = r);
    }

    private void OnCommitClicked() => _vm?.Commit();
}
