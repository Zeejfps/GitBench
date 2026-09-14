using ZGF.Geometry;

namespace GitBench.Controls;

/// <summary>Puts one tooltip up at a time beside its anchor. The anchor arrives in screen
/// coordinates — the caller's window placed it — so one service serves every window.</summary>
public interface ITooltipService
{
    void Show(object owner, string text, ScreenRect anchor);
    void Hide(object owner);
}
