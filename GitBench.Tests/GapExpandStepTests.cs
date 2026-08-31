using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// How far one gap-expander click reveals. The declaration-aware step is what replaces "twenty more
/// lines" with "the rest of the method", and it has to fall back cleanly everywhere the outline
/// cannot answer — an unparsed file, a hunk inside no declaration, a boundary already on screen.
/// </summary>
public class GapExpandStepTests
{
    // Lines 10-30 hidden between a hunk ending at 9 and one starting at 31.
    private static readonly DiffGap Gap = new(GapIndex: 1, NewStart: 10, NewEnd: 30, OldNewDelta: 0);

    private const int Remaining = 21;

    [Fact]
    public void WithNoOutlineTheStepIsTheFixedOne()
    {
        Assert.Equal(
            DiffOptions.ContextExpandStep,
            DiffGaps.ExpandStep(Gap, 0, 0, GapExpandDirection.Down, null, Remaining));
    }

    // The hunk above ends at 9, inside a method running 5-20, so one click finishes that method:
    // lines 10 through 20, eleven of them.
    [Fact]
    public void ExpandingDownFinishesTheDeclarationTheHunkAboveIsIn()
    {
        var outline = Outline(Method(start: 5, end: 20));

        Assert.Equal(11, DiffGaps.ExpandStep(Gap, 0, 0, GapExpandDirection.Down, outline, Remaining));
    }

    // The hunk below starts at 31, inside a method running 25-40, so one click reaches back to 25:
    // lines 30 down to 25, six of them.
    [Fact]
    public void ExpandingUpReachesTheStartOfTheDeclarationTheHunkBelowIsIn()
    {
        var outline = Outline(Method(start: 25, end: 40));

        Assert.Equal(6, DiffGaps.ExpandStep(Gap, 0, 0, GapExpandDirection.Up, outline, Remaining));
    }

    // Already revealed past the method's end, so there is nothing of it left to finish and the
    // click falls back to stepping — by the fixed step or the rest of the gap, whichever is less.
    [Fact]
    public void AlreadyPastTheBoundaryFallsBackToTheFixedStep()
    {
        var outline = Outline(Method(start: 5, end: 12));

        Assert.Equal(16, DiffGaps.ExpandStep(Gap, 5, 0, GapExpandDirection.Down, outline, 16));
    }

    [Fact]
    public void AHunkInsideNoDeclarationFallsBackToTheFixedStep()
    {
        var outline = Outline(Method(start: 40, end: 50));

        Assert.Equal(
            DiffOptions.ContextExpandStep,
            DiffGaps.ExpandStep(Gap, 0, 0, GapExpandDirection.Down, outline, Remaining));
    }

    // A declaration running well past the gap must not reveal lines the gap does not hold.
    [Fact]
    public void TheStepNeverExceedsWhatIsLeftHidden()
    {
        var outline = Outline(Method(start: 5, end: 500));

        Assert.Equal(Remaining, DiffGaps.ExpandStep(Gap, 0, 0, GapExpandDirection.Down, outline, Remaining));
    }

    private static FileOutline Outline(params OutlineNode[] roots) => new(roots);

    private static OutlineNode Method(int start, int end) =>
        new("Login", SymbolKind.Method, "string", start, end, SignatureEndLine: start, Children: []);
}
