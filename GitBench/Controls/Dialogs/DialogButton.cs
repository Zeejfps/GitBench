using GitBench.Infrastructure;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// Visual role of a dialog button. <see cref="Default"/> is the outline chrome used for
/// Cancel/secondary buttons; <see cref="Primary"/> and <see cref="Destructive"/> are filled
/// (accent / danger-red) so the dialog's commit action stands apart from Cancel.
/// </summary>
public enum DialogButtonRole
{
    Default,
    Primary,
    Destructive,
}

public sealed class DialogButton : HoverableButton
{
    private readonly TextView _iconView;
    private readonly TextView _labelView;
    private readonly FlexRowView _row;
    private SpinnerAnimation? _busySpinner;

    public string Label
    {
        get => _labelView.Text ?? string.Empty;
        set => _labelView.Text = value;
    }

    /// <summary>
    /// Icon shown to the left of the label. Empty string detaches it from the row so the
    /// button collapses to just the label — keeping the icon as a hidden child would still
    /// add the row's gap and offset the label centering.
    /// </summary>
    public string Icon
    {
        get => _iconView.Text ?? string.Empty;
        set
        {
            var hasIcon = !string.IsNullOrEmpty(value);
            _iconView.Text = value;
            var attached = _row.Children.Contains(_iconView);
            if (hasIcon && !attached) _row.Children.Insert(0, _iconView);
            else if (!hasIcon && attached) _row.Children.Remove(_iconView);
        }
    }

    public float IconRotation
    {
        get => _iconView.Rotation.Value;
        set => _iconView.Rotation = value;
    }

    public DialogButton(string label, Action? onClick = null, DialogButtonRole role = DialogButtonRole.Default) : base(onClick)
    {
        _iconView = new TextView(CompatUi.Canvas)
        {
            Text = string.Empty,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        _iconView.BindThemedTextColor(s => SelectText(s, role));

        _labelView = new TextView(CompatUi.Canvas)
        {
            Text = label,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindThemedTextColor(s => SelectText(s, role));

        _row = new FlexRowView
        {
            Gap = 6,
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _labelView },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(6),
            // Horizontal padding gives short labels breathing room and lets the button size
            // to its text (clamped up by MinWidthConstraint in DialogFrame.ButtonsRow) instead
            // of being pinned to a hand-tuned Width per dialog.
            Padding = new PaddingStyle { Left = 16, Right = 16 },
            Children = { _row },
        };
        if (role == DialogButtonRole.Default)
        {
            // Hover styling only when enabled — a disabled button shouldn't react to the pointer.
            // Focus-ring highlighting reuses the same chrome so a tabbed-to button looks hovered.
            BorderedButtonChrome.Bind(background,
                () => IsEnabled.Value && (IsHovered.Value || IsFocusHighlighted.Value));
        }
        else
        {
            background.BindThemedBackgroundColor(s => SelectFill(s, role));
            background.BindThemedBorderColor(s => BorderColorStyle.All(SelectFill(s, role)));
        }
        SetBackground(background);
    }

    private uint SelectFill(ThemeStyles s, DialogButtonRole role)
    {
        var a = s.DialogActionButton;
        if (!IsEnabled.Value) return a.DisabledFill;
        var hovered = IsHovered.Value || IsFocusHighlighted.Value;
        return role == DialogButtonRole.Destructive
            ? (hovered ? a.DestructiveFillHover : a.DestructiveFill)
            : (hovered ? a.PrimaryFillHover : a.PrimaryFill);
    }

    private uint SelectText(ThemeStyles s, DialogButtonRole role)
    {
        if (role == DialogButtonRole.Default)
            return IsEnabled.Value ? s.BorderedButton.Text : s.BorderedButton.TextDisabled;
        if (!IsEnabled.Value) return s.DialogActionButton.DisabledText;
        return role == DialogButtonRole.Destructive
            ? s.DialogActionButton.DestructiveText
            : s.DialogActionButton.PrimaryText;
    }

    /// <summary>
    /// Like <see cref="HoverableButton.BindCommand"/>, but additionally shows a spinning loader
    /// icon while the command runs. The spinner is owned by the button and driven entirely off
    /// <see cref="AsyncCommand.IsRunning"/>, so dialogs need no per-VM busy-state plumbing — the
    /// view model just exposes the command. Call from <c>Bind</c>, after the button is attached
    /// to a context (its dispatcher is resolved from <see cref="ContainerView.Context"/>).
    /// </summary>
    internal void BindBusyCommand(AsyncCommand command)
    {
        BindCommand(command);

        _busySpinner = this.Context?.Get<SpinnerAnimation>();
        _busySpinner?.Rotation.Subscribe(r => IconRotation = r);

        command.IsRunning.Subscribe(running =>
        {
            if (running)
            {
                _busySpinner?.Start();
                Icon = LucideIcons.Loader;
            }
            else
            {
                _busySpinner?.Stop();
                Icon = string.Empty;
                IconRotation = 0f;
            }
        });
    }
}
