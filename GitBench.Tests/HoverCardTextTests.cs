using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using Xunit;

namespace GitBench.Tests;

/// <summary>What one hover card says when the line has problems, a type, both, or neither.</summary>
public sealed class HoverCardTextTests
{
    private static Diagnostic Problem(
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        string? source = null,
        string? code = null) =>
        new(
            new LspRange(
                new LspPosition(new LspLine(0), new LspCharacter(0)),
                new LspPosition(new LspLine(0), new LspCharacter(1))),
            severity,
            message,
            source,
            code);

    [Fact]
    public void WithNothingWrongTheCardIsTheServersAnswerUntouched()
    {
        var hover = new HoverText("`fn main()`");

        Assert.Same(hover, HoverCardText.Compose([], hover));
    }

    [Fact]
    public void WithNothingWrongAndNoAnswerThereIsNoCard()
    {
        Assert.Null(HoverCardText.Compose([], null));
    }

    // The squiggle has to be readable on its own: a server that says nothing about the symbol still
    // said something about the line.
    [Fact]
    public void AProblemAloneIsStillACard()
    {
        var card = HoverCardText.Compose([Problem("cannot find value `x`")], null);

        Assert.Contains("cannot find value `x`", card!.Markdown);
        Assert.Contains("Error", card.Markdown);
    }

    [Fact]
    public void TheProblemComesFirstAndIsSeparatedFromTheType()
    {
        var card = HoverCardText.Compose([Problem("mismatched types")], new HoverText("`i64`"))!;

        Assert.True(card.Markdown.IndexOf("mismatched types", StringComparison.Ordinal)
            < card.Markdown.IndexOf("`i64`", StringComparison.Ordinal));
        Assert.Contains("---", card.Markdown);
    }

    [Fact]
    public void EverySeverityIsNamedInWordsRatherThanANumber()
    {
        Assert.Contains("Warning", HoverCardText.Compose(
            [Problem("unused", DiagnosticSeverity.Warning)], null)!.Markdown);
        Assert.Contains("Info", HoverCardText.Compose(
            [Problem("note", DiagnosticSeverity.Information)], null)!.Markdown);
        Assert.Contains("Hint", HoverCardText.Compose(
            [Problem("try this", DiagnosticSeverity.Hint)], null)!.Markdown);
    }

    // A server that left the severity out is not saying the problem is mild.
    [Fact]
    public void ASeverityTheServerLeftOutReadsAsAnError()
    {
        var card = HoverCardText.Compose([Problem("boom", DiagnosticSeverity.Unspecified)], null)!;

        Assert.Contains("Error", card.Markdown);
    }

    [Fact]
    public void WhoSaidItAndItsCodeAreKeptWhenTheServerGaveThem()
    {
        var card = HoverCardText.Compose(
            [Problem("cannot find value `x`", source: "rustc", code: "E0425")], null)!;

        Assert.Contains("rustc E0425", card.Markdown);
    }

    [Fact]
    public void AProblemWithNeitherSourceNorCodeGetsNoEmptyBrackets()
    {
        var card = HoverCardText.Compose([Problem("boom")], null)!;

        Assert.DoesNotContain("()", card.Markdown);
    }

    [Fact]
    public void EveryProblemOnTheLineIsOnTheCard()
    {
        var card = HoverCardText.Compose([Problem("first"), Problem("second")], null)!;

        Assert.Contains("first", card.Markdown);
        Assert.Contains("second", card.Markdown);
    }
}
