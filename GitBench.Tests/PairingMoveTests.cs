using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>What the agent is told of each thing the user did in a session: the turn it gets in
/// place of waiting on them.</summary>
public sealed class PairingMoveTests
{
    private static string? Relative(string path) =>
        path.StartsWith("C:/repo/", StringComparison.Ordinal) ? path["C:/repo/".Length..] : null;

    [Fact]
    public void Done_CarriesTheDiffAndWhatTheUserDidWithTheCode()
    {
        var told = PairingInstructions.Move(
            new PairingAction.Done(3, "+ retry()", DraftOutcome.AcceptedThenEdited, []), Relative);

        Assert.StartsWith("DiffDino pairing: the user finished stop 3;", told);
        Assert.Contains("changed it", told);
        Assert.Contains("```diff\n+ retry()\n```", told);
    }

    [Fact]
    public void ADiffWithAFenceInIt_CannotCloseTheFenceItIsIn()
    {
        var told = PairingInstructions.Move(
            new PairingAction.Done(1, "+ ```code```", DraftOutcome.NotAccepted, []), Relative);

        Assert.Contains("````diff\n+ ```code```\n````", told);
    }

    [Fact]
    public void Done_WithNothingChanged_SaysSo_AndListsItsProblems()
    {
        var told = PairingInstructions.Move(
            new PairingAction.Done(2, "", DraftOutcome.NotAccepted, ["src/A.cs could not be saved"]), Relative);

        Assert.Contains("(no changes)", told);
        Assert.EndsWith("Problems:\n- src/A.cs could not be saved", told);
    }

    [Fact]
    public void AMessage_SaysWhereTheUserWas()
    {
        var caret = new EditorCaret("C:/repo/src/Client.cs", TextPosition.At(12, 3), "retries");

        var told = PairingInstructions.Move(new PairingAction.Message(4, "Is this right?", caret), Relative);

        Assert.StartsWith("DiffDino pairing: the user says, at stop 4 with the caret at src/Client.cs:12:4 and this selected:", told);
        Assert.EndsWith(":\n\nIs this right?", told);
    }

    [Fact]
    public void AMessageBetweenStops_NamesNoStop()
    {
        var told = PairingInstructions.Move(new PairingAction.Message(0, "Why?", null), Relative);

        Assert.Equal("DiffDino pairing: the user says:\n\nWhy?", told);
    }
}
