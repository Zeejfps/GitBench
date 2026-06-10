using ZGF.Geometry;

namespace GitBench.Controls;

public interface ITooltipService
{
    void Show(object owner, string text, RectF anchorRect);
    void Hide(object owner);
}
