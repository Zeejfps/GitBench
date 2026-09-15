using Xunit;

namespace GitBench.Lsp.Documents.Tests;

/// <summary>
/// Hover content leaves here as the one thing the popup renders, and a definition target has to be
/// placed inside or outside the repository: most jumps in Rust and Go land in a standard library
/// or a package cache, and the pane reaches those a different way.
/// </summary>
public sealed class ResultShapeTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static readonly LspPosition Somewhere = new(new LspLine(7), new LspCharacter(11));

    private static DocumentUri FileAt(string absolute) =>
        DocumentUri.OfFile(absolute.Replace('/', Path.DirectorySeparatorChar));

    private static DocumentUri InRepo(string relative) =>
        FileAt(Root.Replace('\\', '/') + "/" + relative);

    private static RepoBoundary Repo => RepoBoundary.At(Root);

    private static string? Markdown(MarkupKind kind, string value) =>
        HoverText.Of(new Hover.Text(kind, value, null))?.Markdown;

    [Fact]
    public void MarkdownIsPassedThroughUntouched()
    {
        Assert.Equal("**total**: `i32`", Markdown(MarkupKind.Markdown, "**total**: `i32`"));
    }

    // A type signature is full of angle brackets, underscores and asterisks. Handed to a markdown
    // renderer unfenced it comes out italicised and half-eaten.
    [Fact]
    public void PlainTextIsFencedRatherThanRendered()
    {
        Assert.Equal("```\nVec<_>\n```", Markdown(MarkupKind.PlainText, "Vec<_>"));
    }

    [Fact]
    public void HoverContentWithNothingInItIsNoHoverAtAll()
    {
        Assert.Null(HoverText.Of(new Hover.None()));
        Assert.Null(Markdown(MarkupKind.Markdown, string.Empty));
        Assert.Null(Markdown(MarkupKind.PlainText, "  \n "));
    }

    [Fact]
    public void ALocationInsideTheRepositoryIsNamedRelativeToIt()
    {
        var target = Assert.IsType<DefinitionTarget.InRepo>(Repo.Classify(InRepo("src/main.rs"), Somewhere));
        Assert.Equal("src/main.rs", target.RelativePath);
        Assert.Equal(Somewhere, target.Position);
    }

    [Fact]
    public void ATargetOutsideTheRepositoryKeepsItsWholePath()
    {
        var outside = OperatingSystem.IsWindows()
            ? @"C:\Users\me\.cargo\registry\src\lib.rs"
            : "/home/me/.cargo/registry/src/lib.rs";

        var target = Assert.IsType<DefinitionTarget.OutsideRepo>(Repo.Classify(FileAt(outside), Somewhere));
        Assert.Equal(outside, target.AbsolutePath);
    }

    // "/repo-extra" starts with "/repo" and is a different project.
    [Fact]
    public void ADirectorySharingTheRepositoryNameIsNotInsideIt()
    {
        var sibling = FileAt(Root.Replace('\\', '/') + "-extra/src/main.rs");

        Assert.IsType<DefinitionTarget.OutsideRepo>(Repo.Classify(sibling, Somewhere));
    }

    [Fact]
    public void APathDifferingOnlyInCaseIsInsideTheRepositoryWhereTheFilesystemIgnoresCase()
    {
        var boundary = RepoBoundary.At(Root, PathComparison.CaseInsensitive);
        var upper = FileAt(Root.Replace('\\', '/').ToUpperInvariant() + "/src/main.rs");

        Assert.IsType<DefinitionTarget.InRepo>(boundary.Classify(upper, Somewhere));
    }

    [Fact]
    public void APathDifferingOnlyInCaseIsOutsideTheRepositoryWhereCaseMatters()
    {
        var boundary = RepoBoundary.At(Root, PathComparison.CaseSensitive);
        var upper = FileAt(Root.Replace('\\', '/').ToUpperInvariant() + "/src/main.rs");

        Assert.IsType<DefinitionTarget.OutsideRepo>(boundary.Classify(upper, Somewhere));
    }

    [Fact]
    public void ARepositoryNamedTwoWaysOwnsItsFilesUnderEitherName()
    {
        var resolved = OperatingSystem.IsWindows() ? @"C:\private\repo" : "/private/repo";
        var boundary = RepoBoundary.At([Root, resolved], PathComparison.CaseSensitive);

        var target = Assert.IsType<DefinitionTarget.InRepo>(
            boundary.Classify(FileAt(resolved.Replace('\\', '/') + "/src/main.rs"), Somewhere));
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

        Assert.IsType<DefinitionTarget.OutsideRepo>(boundary.Classify(FileAt(outside), Somewhere));
    }

    // The uri is how a server names a file, and a repository full of spaces and non-ASCII names is
    // ordinary. A path that does not survive the trip is a jump into nothing.
    [Fact]
    public void APathWithSpacesAndNonAsciiSurvivesTheTripThroughAUri()
    {
        var target = Assert.IsType<DefinitionTarget.InRepo>(Repo.Classify(InRepo("src/a file 名前.rs"), Somewhere));
        Assert.Equal("src/a file 名前.rs", target.RelativePath);
    }
}
