using Xunit;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// Hover content and definition results each arrive in several shapes and have to leave here as
/// one. For definitions that also means deciding whether the target is in the repository at all:
/// most jumps in Rust and Go land in a standard library or a package cache, and the pane reaches
/// those a different way.
/// </summary>
public sealed class ResultShapeTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static readonly LspPosition Asked = new(new LspLine(7), new LspCharacter(11));

    private static DocumentUri FileAt(string absolute) =>
        DocumentUri.OfFile(absolute.Replace('/', Path.DirectorySeparatorChar));

    private static DocumentUri InRepo(string relative) =>
        FileAt(Root.Replace('\\', '/') + "/" + relative);

    private static RepoBoundary Repo => RepoBoundary.At(Root);

    private static LspRange StartingAt(int line, int character) =>
        LspRange.Empty(new LspPosition(new LspLine(line), new LspCharacter(character)));

    private static HoverAnswer Answer(HoverPayload payload) =>
        HoverAnswer.For(new HoverReply(payload, OptionalRange.Absent), Asked);

    private static string Markdown(HoverPayload payload) =>
        Assert.IsType<HoverAnswer.Content>(Answer(payload)).Text.Markdown;

    [Fact]
    public void ABareStringHoverIsAlreadyTheMarkdownToShow()
    {
        Assert.Equal("fn main()", Markdown(new HoverPayload.PlainText("fn main()")));
    }

    [Fact]
    public void ALanguageTaggedHoverBecomesAFencedBlockInThatLanguage()
    {
        Assert.Equal("```rust\nfn main()\n```", Markdown(new HoverPayload.CodeBlock("rust", "fn main()")));
    }

    // A type signature is full of angle brackets, underscores and asterisks. Handed to a markdown
    // renderer unfenced it comes out italicised and half-eaten.
    [Fact]
    public void PlainTextMarkupIsFencedRatherThanRendered()
    {
        Assert.Equal("```\nVec<_>\n```", Markdown(new HoverPayload.Markup(MarkupKind.PlainText, "Vec<_>")));
    }

    [Fact]
    public void MarkdownMarkupIsPassedThroughUntouched()
    {
        Assert.Equal("**total**: `i32`", Markdown(new HoverPayload.Markup(MarkupKind.Markdown, "**total**: `i32`")));
    }

    [Fact]
    public void SectionsKeepTheirOrderAndStaySeparate()
    {
        var payload = new HoverPayload.Sections(
        [
            new HoverPayload.CodeBlock("rust", "fn main()"),
            new HoverPayload.Markup(MarkupKind.Markdown, "Entry point."),
        ]);

        Assert.Equal("```rust\nfn main()\n```\n\n---\n\nEntry point.", Markdown(payload));
    }

    [Fact]
    public void HoverContentWithNothingInItIsNoHoverAtAll()
    {
        Assert.IsType<HoverAnswer.Empty>(Answer(HoverPayload.Nothing));
        Assert.IsType<HoverAnswer.Empty>(Answer(new HoverPayload.PlainText(string.Empty)));
        Assert.IsType<HoverAnswer.Empty>(Answer(new HoverPayload.PlainText("  \n ")));
        Assert.IsType<HoverAnswer.Empty>(Answer(new HoverPayload.Sections([])));
        Assert.IsType<HoverAnswer.Empty>(Answer(new HoverPayload.Sections([new HoverPayload.PlainText("")])));
    }

    // The popup has to point at something, and a server that omitted the range still meant the
    // symbol under the cursor.
    [Fact]
    public void AHoverWithNoRangeOfItsOwnAnchorsToThePositionThatWasAsked()
    {
        var content = Assert.IsType<HoverAnswer.Content>(Answer(new HoverPayload.PlainText("i32")));

        Assert.Equal(LspRange.Empty(Asked), content.Range);
    }

    [Fact]
    public void AHoverThatNamesItsOwnRangeKeepsIt()
    {
        var range = new LspRange(
            new LspPosition(new LspLine(7), new LspCharacter(4)),
            new LspPosition(new LspLine(7), new LspCharacter(9)));

        var reply = new HoverReply(new HoverPayload.PlainText("i32"), OptionalRange.Of(range));

        Assert.Equal(range, Assert.IsType<HoverAnswer.Content>(HoverAnswer.For(reply, Asked)).Range);
    }

    [Fact]
    public void ASingleLocationBecomesOneTargetInsideTheRepository()
    {
        var payload = new DefinitionPayload.Single(new Location(InRepo("src/main.rs"), StartingAt(12, 4)));

        var target = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
        Assert.Equal("src/main.rs", target.RelativePath);
        Assert.Equal(new LspPosition(new LspLine(12), new LspCharacter(4)), target.Position);
    }

    [Fact]
    public void SeveralLocationsKeepTheOrderTheyArrivedIn()
    {
        var payload = new DefinitionPayload.Many(
        [
            new Location(InRepo("src/a.rs"), StartingAt(1, 0)),
            new Location(InRepo("src/b.rs"), StartingAt(2, 0)),
        ]);

        Assert.Equal(
            new[] { "src/a.rs", "src/b.rs" },
            DefinitionTargets.From(payload, Repo).Cast<DefinitionTarget.InRepo>().Select(t => t.RelativePath));
    }

    // The target range covers the doc comment and attributes above a declaration; the selection
    // range is the name. Landing on the name is what the user asked for.
    [Fact]
    public void ALocationLinkLandsOnTheNameRatherThanTheWholeDeclaration()
    {
        var payload = new DefinitionPayload.Links(
        [
            new LocationLink(
                InRepo("src/lib.rs"), StartingAt(10, 0), OptionalRange.Of(StartingAt(13, 7)),
                OptionalRange.Absent),
        ]);

        var target = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
        Assert.Equal(new LspPosition(new LspLine(13), new LspCharacter(7)), target.Position);
    }

    [Fact]
    public void ALocationLinkWithNoSelectionRangeFallsBackToTheDeclaration()
    {
        var payload = new DefinitionPayload.Links(
        [
            new LocationLink(
                InRepo("src/lib.rs"), StartingAt(10, 0), OptionalRange.Absent, OptionalRange.Absent),
        ]);

        var target = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
        Assert.Equal(new LspPosition(new LspLine(10), new LspCharacter(0)), target.Position);
    }

    // The span the answer resolved back in the asking file, read off the first link: several links
    // to one symbol are alternative declarations of the same span, and a reader points at one thing.
    [Fact]
    public void TheResolvedSpanIsReadOffTheFirstLink()
    {
        var payload = new DefinitionPayload.Links(
        [
            new LocationLink(
                InRepo("src/lib.rs"), StartingAt(10, 0), OptionalRange.Absent,
                OptionalRange.Of(StartingAt(3, 12))),
            new LocationLink(
                InRepo("src/other.rs"), StartingAt(4, 0), OptionalRange.Absent,
                OptionalRange.Of(StartingAt(9, 1))),
        ]);

        var origin = Assert.IsType<OptionalRange.Present>(DefinitionTargets.OriginOf(payload));
        Assert.Equal(new LspPosition(new LspLine(3), new LspCharacter(12)), origin.Range.Start);
    }

    [Fact]
    public void AnAnswerWithNoLinksResolvedNoSpan()
    {
        var payload = new DefinitionPayload.Single(new Location(InRepo("src/lib.rs"), StartingAt(3, 1)));

        Assert.IsType<OptionalRange.NotGiven>(DefinitionTargets.OriginOf(payload));
    }

    [Fact]
    public void ATargetOutsideTheRepositoryKeepsItsWholePath()
    {
        var outside = OperatingSystem.IsWindows()
            ? @"C:\Users\me\.cargo\registry\src\lib.rs"
            : "/home/me/.cargo/registry/src/lib.rs";
        var payload = new DefinitionPayload.Single(new Location(FileAt(outside), StartingAt(3, 1)));

        var target = Assert.IsType<DefinitionTarget.OutsideRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
        Assert.Equal(outside, target.AbsolutePath);
    }

    // "/repo-extra" starts with "/repo" and is a different project.
    [Fact]
    public void ADirectorySharingTheRepositoryNameIsNotInsideIt()
    {
        var sibling = FileAt(Root.Replace('\\', '/') + "-extra/src/main.rs");
        var payload = new DefinitionPayload.Single(new Location(sibling, StartingAt(0, 0)));

        Assert.IsType<DefinitionTarget.OutsideRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
    }

    [Fact]
    public void APathDifferingOnlyInCaseIsInsideTheRepositoryWhereTheFilesystemIgnoresCase()
    {
        var boundary = RepoBoundary.At(Root, PathComparison.CaseInsensitive);
        var payload = new DefinitionPayload.Single(
            new Location(FileAt(Root.Replace('\\', '/').ToUpperInvariant() + "/src/main.rs"), StartingAt(0, 0)));

        Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(DefinitionTargets.From(payload, boundary)));
    }

    [Fact]
    public void APathDifferingOnlyInCaseIsOutsideTheRepositoryWhereCaseMatters()
    {
        var boundary = RepoBoundary.At(Root, PathComparison.CaseSensitive);
        var payload = new DefinitionPayload.Single(
            new Location(FileAt(Root.Replace('\\', '/').ToUpperInvariant() + "/src/main.rs"), StartingAt(0, 0)));

        Assert.IsType<DefinitionTarget.OutsideRepo>(Assert.Single(DefinitionTargets.From(payload, boundary)));
    }

    [Fact]
    public void NoLocationAtAllIsNoTarget()
    {
        Assert.Empty(DefinitionTargets.From(DefinitionPayload.Nothing, Repo));
        Assert.Empty(DefinitionTargets.From(new DefinitionPayload.Many([]), Repo));
        Assert.Empty(DefinitionTargets.From(new DefinitionPayload.Links([]), Repo));
    }

    [Fact]
    public void ARepositoryNamedTwoWaysOwnsItsFilesUnderEitherName()
    {
        var resolved = OperatingSystem.IsWindows() ? @"C:\private\repo" : "/private/repo";
        var boundary = RepoBoundary.At([Root, resolved], PathComparison.CaseSensitive);
        var payload = new DefinitionPayload.Single(
            new Location(FileAt(resolved.Replace('\\', '/') + "/src/main.rs"), StartingAt(4, 0)));

        var target = Assert.IsType<DefinitionTarget.InRepo>(
            Assert.Single(DefinitionTargets.From(payload, boundary)));
        Assert.Equal("src/main.rs", target.RelativePath);
    }

    [Fact]
    public void ASecondNameForTheRepositoryDoesNotWidenItToEverythingElse()
    {
        var resolved = OperatingSystem.IsWindows() ? @"C:\private\repo" : "/private/repo";
        var boundary = RepoBoundary.At([Root, resolved], PathComparison.CaseSensitive);
        var outside = OperatingSystem.IsWindows()
            ? @"C:\private\other\lib.rs"
            : "/private/other/lib.rs";
        var payload = new DefinitionPayload.Single(new Location(FileAt(outside), StartingAt(0, 0)));

        Assert.IsType<DefinitionTarget.OutsideRepo>(Assert.Single(DefinitionTargets.From(payload, boundary)));
    }

    // The uri is how a server names a file, and a repository full of spaces and non-ASCII names is
    // ordinary. A path that does not survive the trip is a jump into nothing.
    [Fact]
    public void APathWithSpacesAndNonAsciiSurvivesTheTripThroughAUri()
    {
        var uri = InRepo("src/a file 名前.rs");
        var payload = new DefinitionPayload.Single(new Location(uri, StartingAt(0, 0)));

        var target = Assert.IsType<DefinitionTarget.InRepo>(Assert.Single(DefinitionTargets.From(payload, Repo)));
        Assert.Equal("src/a file 名前.rs", target.RelativePath);
    }
}
