using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class OperationRow : HoverableButton
{
    private enum RowState { Idle, Success, Failure }

    private readonly TextView _label;
    private readonly TextView _phase;
    private readonly TextView _elapsed;
    private readonly ProgressBarView _bar;
    private readonly RectView _background;

    private OperationRowStyles _styles = ThemeStyles.Dark.OperationRow;
    private RowState _state = RowState.Idle;
    private string? _failureMessage;

    public OperationRow(string label, string icon, Action onToggleLog)
        : base(onToggleLog)
    {
        var iconView = new TextView
        {
            Text = icon,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 18,
        };
        iconView.BindThemedTextColor(s => s.OperationRow.IconText);

        _label = new TextView
        {
            Text = label,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _label.BindThemedTextColor(s => s.OperationRow.LabelText);

        _phase = new TextView
        {
            Text = string.Empty,
            VerticalTextAlignment = TextAlignment.Center,
        };

        _bar = new ProgressBarView { Width = 120f };

        _elapsed = new TextView
        {
            Text = "0s",
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.End,
            Width = 36,
        };
        _elapsed.BindThemedTextColor(s => s.OperationRow.ElapsedText);

        _background = new RectView
        {
            Padding = new PaddingStyle { Left = 12, Right = 12, Top = 6, Bottom = 6 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 8,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        iconView,
                        new FlexItem { Grow = 1, Child = _label },
                        _phase,
                        _bar,
                        _elapsed,
                    },
                },
            },
        };
        _background.BindThemedBackgroundColor(s =>
            IsHovered.Value ? s.OperationRow.BackgroundHover : s.OperationRow.BackgroundIdle);
        SetBackground(_background);

        this.BindThemed(s =>
        {
            _styles = s.OperationRow;
            ApplyState();
        });
    }

    public string Phase
    {
        set
        {
            _phase.Text = value ?? string.Empty;
            if (_state == RowState.Idle) _phase.TextColor = _styles.PhaseTextIdle;
        }
    }
    public float Percent { set => _bar.Percent = value; }
    public string Elapsed { set => _elapsed.Text = value; }

    public void MarkSuccess()
    {
        _bar.Percent = 1f;
        _state = RowState.Success;
        ApplyState();
    }

    public void MarkFailure(string? message)
    {
        _state = RowState.Failure;
        _failureMessage = message;
        ApplyState();
    }

    private void ApplyState()
    {
        switch (_state)
        {
            case RowState.Idle:
                _phase.TextColor = _styles.PhaseTextIdle;
                break;
            case RowState.Success:
                _bar.FillColor = _styles.SuccessBar;
                _phase.Text = "Done";
                _phase.TextColor = _styles.SuccessText;
                break;
            case RowState.Failure:
                _bar.FillColor = _styles.FailureBar;
                _phase.Text = string.IsNullOrEmpty(_failureMessage) ? "Failed" : _failureMessage;
                _phase.TextColor = _styles.FailureText;
                break;
        }
    }
}
