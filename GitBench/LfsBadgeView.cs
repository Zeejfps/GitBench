using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench;

/// <summary>
/// Small pill that reports a binary file's storage: "Git LFS" when the blob is tracked by
/// Git LFS, "Not in LFS" when it's committed inline. It hides entirely for non-binary files
/// (status <see cref="LfsBadge.None"/>). Shared by the embedded diff header and the pop-out
/// window toolbar — call <see cref="SetStatus"/> from each owner's <c>Bind</c>.
/// </summary>
internal sealed class LfsBadgeView : RectView
{
    private readonly State<LfsBadge> _state = new(LfsBadge.None);

    public LfsBadgeView()
    {
        var label = new TextView
        {
            FontSize = 10f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        label.BindText(_state, s => s switch
        {
            LfsBadge.Tracked => "Git LFS",
            LfsBadge.NotTracked => "Not in LFS",
            _ => string.Empty,
        });
        label.BindThemedTextColor(s => _state.Value == LfsBadge.Tracked
            ? s.DiffView.LfsBadgeTrackedText
            : s.DiffView.LfsBadgeUntrackedText);

        Height = 16f;
        BorderRadius = BorderRadiusStyle.All(8);
        Padding = new PaddingStyle { Left = 7, Right = 7 };
        Children.Add(label);

        this.BindThemedBackgroundColor(s => _state.Value == LfsBadge.Tracked
            ? s.DiffView.LfsBadgeTrackedBackground
            : s.DiffView.LfsBadgeUntrackedBackground);
        this.BindIsVisible(_state, s => s != LfsBadge.None);
    }

    public void SetStatus(LfsBadge status) => _state.Value = status;
}
