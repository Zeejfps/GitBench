using ZGF.Geometry;

namespace GitBench;

public interface ITooltipService
{
    void Show(object owner, string text, RectF anchorRect);
    void Hide(object owner);
}
