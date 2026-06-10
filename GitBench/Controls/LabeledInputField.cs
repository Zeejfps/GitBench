using GitBench.Controls.Dialogs;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A single-line text input with a label above it, an optional hint line, and an optional
/// validation message below it. Replaces the hand-assembled
/// <c>Label</c> + <c>WrapInput(TextInput)</c> + <c>Hint</c> trio that the dialogs repeat.
///
/// The inner <see cref="TextInputView"/> is exposed via <see cref="Input"/> so callers keep
/// wiring it exactly as before (<c>BindTwoWay</c>, <c>UseController</c>, <c>BeginEditing</c>);
/// the submit/close controller stays in the dialog because only it knows the submit command.
///
/// Validation is driven through <see cref="BindStatus"/>: a non-null <see cref="FieldStatus"/>
/// recolors the box border and reveals the message line (both keyed off
/// <see cref="FieldSeverity"/>); a null status is neutral and collapses the message line so the
/// dialog reflows tightly. Border/message colors are recomputed whenever either the theme or
/// the status changes — the last-seen <see cref="ThemeStyles"/> is cached for that reason.
/// </summary>
internal sealed class LabeledInputField : MultiChildView
{
    // Tall enough that the input's content area (BoxHeight - border - padding) is at least the
    // font's full line height; otherwise descenders (g, j, p, y) get scissored by the box clip.
    private const float BoxHeight = 32f;

    private readonly TextInputView _input;
    private readonly RectView _box;
    private readonly FlexRowView _boxRow;
    private readonly TextView _hint;
    private readonly TextView _message;

    private ThemeStyles? _styles;
    private FieldStatus? _status;
    private View? _accessory;

    /// <summary>The inner text input. Wire it as you would a bare <c>DialogFrame.TextInput()</c>.</summary>
    public TextInputView Input => _input;

    public string? Placeholder
    {
        get => _input.PlaceholderText;
        set => _input.PlaceholderText = value;
    }

    /// <summary>Optional helper line shown beneath the box, above any validation message.</summary>
    public string? Hint
    {
        get => _hint.Text;
        set
        {
            _hint.Text = value ?? string.Empty;
            _hint.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>Optional trailing view placed to the right of the box (e.g. a Browse… button
    /// or a scheme dropdown). The box keeps the remaining width.</summary>
    public View? Accessory
    {
        get => _accessory;
        set
        {
            if (_accessory != null) _boxRow.Children.Remove(_accessory);
            _accessory = value;
            if (value != null) _boxRow.Children.Add(value);
        }
    }

    public LabeledInputField(string label)
    {
        var labelView = DialogFrame.Label(label);

        _input = DialogFrame.TextInput();

        _box = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
            Children = { _input },
        };
        _box.BindThemedBackgroundColor(s => s.TextInput.Background);
        // Own the border binding (rather than DialogFrame.WrapInput's fixed one) so it can be
        // swapped to the error/warning color when a status is set. Cache the styles for the
        // status-driven recompute.
        _box.BindThemed(s =>
        {
            _styles = s;
            Refresh();
        });

        _boxRow = new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Gap = 8,
            Height = BoxHeight,
            Children = { new FlexItem { Grow = 1, Child = _box } },
        };

        _hint = DialogFrame.Hint(string.Empty, TextWrap.Wrap);
        _hint.IsVisible = false;

        _message = new TextView { Text = string.Empty, TextWrap = TextWrap.Wrap, IsVisible = false };

        AddChildToSelf(new FlexColumnView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = { labelView, _boxRow, _hint, _message },
        });
    }

    /// <summary>
    /// Binds the field's error/warning state to an observable. <c>null</c> = neutral. Fires
    /// immediately with the source's current value. The subscription lives as long as the
    /// source (a VM-owned <c>Derived</c>/<c>State</c>), matching how the dialogs subscribe to
    /// their view models elsewhere.
    /// </summary>
    public void BindStatus(IReadable<FieldStatus?> source)
    {
        source.Subscribe(s =>
        {
            _status = s;
            Refresh();
        });
    }

    private void Refresh()
    {
        // Theme hasn't been applied yet (status arrived first); the themed callback will
        // re-run Refresh once _styles is set.
        if (_styles is null) return;

        var (borderColor, messageColor) = _status?.Severity switch
        {
            FieldSeverity.Error => (_styles.DialogFrame.ErrorText, _styles.DialogFrame.ErrorText),
            FieldSeverity.Warning => (_styles.DialogFrame.WarningText, _styles.DialogFrame.WarningText),
            _ => (_styles.TextInput.Border, _styles.DialogFrame.ErrorText),
        };

        _box.BorderColor = BorderColorStyle.All(borderColor);

        var hasStatus = _status != null;
        _message.IsVisible = hasStatus;
        if (hasStatus)
        {
            _message.Text = _status!.Message;
            _message.TextColor = messageColor;
        }
    }
}
