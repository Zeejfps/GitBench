using GitBench.Features.Diff;
using ZGF.Geometry;
using Xunit;

namespace GitBench.Tests;

// The sheet is measured by the host that reserves the slot and arranged by the surface that draws
// into it, from two separate calls — so a disagreement between them shows up as an icon spilling
// out of its card, not as a failure. These pin the two together and the placement inside the area.
public class IconSheetLayoutTests
{
    private static readonly RectF Area = new(0f, 0f, 400f, 600f);

    [Fact]
    public void KeepsEveryFrameInsideTheArea()
    {
        var cells = Arrange(Ladder(), Area);

        Assert.Equal(6, cells.Count);
        foreach (var cell in cells)
        {
            Assert.True(cell.Image.Left >= Area.Left - 1f && cell.Image.Right <= Area.Right + 1f,
                $"image {cell.FrameIndex} escaped horizontally: {cell.Image}");
            Assert.True(cell.Label.Bottom >= Area.Bottom - 1f && cell.Image.Top <= Area.Top + 1f,
                $"cell {cell.FrameIndex} escaped vertically: {cell.Image} / {cell.Label}");
        }
    }

    [Fact]
    public void DrawsFramesAtTheirOwnPixelSizeWhenTheyFit()
    {
        var cells = Arrange(Ladder(), Area);

        Assert.Equal(256f, cells[0].Image.Width);
        Assert.Equal(256f, cells[0].Image.Height);
        Assert.Equal(16f, cells[^1].Image.Width);
    }

    [Fact]
    public void MeasuresWhatItArranges()
    {
        var frames = Ladder();
        var height = IconSheetLayout.Measure(frames, Area.Width, Area.Height);

        var cells = Arrange(frames, new RectF(Area.Left, Area.Bottom, Area.Width, height));

        var top = cells.Max(c => c.Image.Top);
        var bottom = cells.Min(c => c.Label.Bottom);
        Assert.InRange(top - bottom, height - 1.5f, height + 1.5f);
    }

    [Fact]
    public void WrapsRowsThatRunPastTheWidth()
    {
        var frames = new[] { Frame(128), Frame(128), Frame(128) };

        var cells = Arrange(frames, new RectF(0f, 0f, 300f, 600f));

        // Two fit across 300px, the third drops to its own row below them.
        Assert.Equal(cells[0].Image.Bottom, cells[1].Image.Bottom);
        Assert.True(cells[2].Image.Top < cells[0].Image.Bottom);
    }

    [Fact]
    public void ShrinksTheSheetToFitAShortSlot()
    {
        var frames = Ladder();
        var slot = new RectF(0f, 0f, 400f, 120f);

        var height = IconSheetLayout.Measure(frames, slot.Width, slot.Height);
        var cells = Arrange(frames, slot);

        Assert.True(height <= slot.Height, $"measured {height} for a {slot.Height} slot");
        Assert.True(cells[0].Image.Height < 256f, "the largest frame was not scaled down");
        foreach (var cell in cells)
            Assert.True(cell.Label.Bottom >= slot.Bottom - 1f && cell.Image.Top <= slot.Top + 1f,
                $"cell {cell.FrameIndex} escaped the slot: {cell.Image} / {cell.Label}");
    }

    [Fact]
    public void FitsALoneFrameWiderThanTheSheet()
    {
        var cells = Arrange([Frame(256)], new RectF(0f, 0f, 100f, 600f));

        Assert.Equal(100f, cells[0].Image.Width);
        Assert.Equal(100f, cells[0].Image.Height);
    }

    private static List<IconSheetCell> Arrange(IReadOnlyList<ImageFrame> frames, RectF area)
    {
        var cells = new List<IconSheetCell>();
        IconSheetLayout.Arrange(frames, area, cells);
        return cells;
    }

    // What an icon generator emits, largest first — the order the decoder hands over.
    private static ImageFrame[] Ladder() =>
        [Frame(256), Frame(128), Frame(64), Frame(48), Frame(32), Frame(16)];

    private static ImageFrame Frame(int size) => new(size, size, []);
}
