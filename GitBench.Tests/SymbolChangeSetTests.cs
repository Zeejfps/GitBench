using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// What a diff did to a file's declarations. The matcher works off two parsed outlines and the
/// changed line numbers, so these parse real sources and hand it a real hunk: the shape of an
/// outline is the thing under test, and a hand-built one would only test the test.
/// </summary>
[Collection(nameof(CodeIntelCollection))]
public class SymbolChangeSetTests(CodeIntelFixture fixture)
{
    [Fact]
    public void AChangedMethodIsModifiedAndItsClassIsNot()
    {
        var changes = Changes(
            before: Class("        Check(user);"),
            after: Class("        Verify(user);"),
            Rem(6, "        Check(user);"),
            Add(6, "        Verify(user);"));

        var cls = Assert.Single(changes);
        Assert.Equal("AuthService", cls.Path);
        Assert.Equal(SymbolChangeKind.Unchanged, cls.Change);

        var method = Assert.Single(cls.Children);
        Assert.Equal("AuthService.Login(string)", method.Path);
        // The path tells two same-named methods apart; the name is what a summary has room to show.
        Assert.Equal("Login(string)", method.Name);
        Assert.Equal(SymbolChangeKind.Modified, method.Change);
    }

    // Editing inside the class but outside every member it declares is the class changing, not any
    // of its members. The exclusion of children's ranges is what tells those two apart.
    [Fact]
    public void EditingTheClassOutsideItsMembersModifiesTheClass()
    {
        var changes = Changes(
            before: Class("        Check(user);", note: "    // tries twice"),
            after: Class("        Check(user);", note: "    // tries three times"),
            Rem(3, "    // tries twice"),
            Add(3, "    // tries three times"));

        var cls = Assert.Single(changes);
        Assert.Equal(SymbolChangeKind.Modified, cls.Change);
        Assert.Empty(cls.Children);
    }

    [Fact]
    public void ANewDeclarationIsAddedAndAVanishedOneIsRemoved()
    {
        var changes = Changes(
            before: """
                class AuthService
                {
                    void Legacy()
                    {
                    }
                }
                """,
            after: """
                class AuthService
                {
                    void Modern()
                    {
                    }
                }
                """,
            Rem(3, "    void Legacy()"),
            Add(3, "    void Modern()"));

        var cls = Assert.Single(changes);
        Assert.Equal(
            [("AuthService.Modern()", SymbolChangeKind.Added), ("AuthService.Legacy()", SymbolChangeKind.Removed)],
            cls.Children.Select(c => (c.Path, c.Change)).ToArray());
    }

    // The whole reason the key carries parameter types: without them an added overload reads as a
    // modification of the one beside it, and the summary reports a change that never happened.
    [Fact]
    public void AnAddedOverloadIsAddedRatherThanAModificationOfItsSibling()
    {
        var changes = Changes(
            before: """
                class AuthService
                {
                    void Login(string user)
                    {
                    }
                }
                """,
            after: """
                class AuthService
                {
                    void Login(string user)
                    {
                    }

                    void Login(string user, int attempt)
                    {
                    }
                }
                """,
            Add(7, "    void Login(string user, int attempt)"));

        var cls = Assert.Single(changes);
        var added = Assert.Single(cls.Children);
        Assert.Equal("AuthService.Login(string, int)", added.Path);
        Assert.Equal(SymbolChangeKind.Added, added.Change);
    }

    [Fact]
    public void UntouchedDeclarationsArePrunedAway()
    {
        var changes = Changes(
            before: Class("        Check(user);"),
            after: Class("        Check(user);"));

        Assert.Empty(changes);
    }

    // A diff that only adds lines never fetches the old blob, so there is no before to compare
    // against. Reporting every declaration as Added would be a summary of the file, not of the
    // change — so with one outline it only reports what the change reached inside of.
    [Fact]
    public void WithOnlyTheNewOutlineNothingIsClaimedToBeAdded()
    {
        var after = Class("        Check(user);");
        var changes = SymbolChangeSet.Build(
            DiffOf(Hunk(1, 8, 1, 8, null, Add(6, "        Check(user);"))),
            new DiffAnnotations(null, fixture.Outline(after), null));

        var cls = Assert.Single(changes);
        Assert.Equal(SymbolChangeKind.Unchanged, cls.Change);
        var method = Assert.Single(cls.Children);
        Assert.Equal("AuthService.Login(string)", method.Path);
        Assert.Equal(SymbolChangeKind.Modified, method.Change);
    }

    // Every declaration in a file shares its namespace, so naming it separates nothing — and a
    // namespace with no name of its own to contribute must not surface as a blank entry.
    [Fact]
    public void ANamespaceContributesNoEntryAndNoSegment()
    {
        var before = string.Join(NEWLINE, "namespace App;", "", Class("        Check(user);"));
        var after = string.Join(NEWLINE, "namespace App;", "", Class("        Verify(user);"));

        var changes = SymbolChangeSet.Build(
            DiffOf(Hunk(1, 10, 1, 10, null, Rem(8, "        Check(user);"), Add(8, "        Verify(user);"))),
            new DiffAnnotations(null, fixture.Outline(after), fixture.Outline(before)));

        var cls = Assert.Single(changes);
        Assert.Equal("AuthService", cls.Path);
        Assert.Equal("AuthService.Login(string)", Assert.Single(cls.Children).Path);
    }

    [Fact]
    public void WithNoOutlineOnEitherSideThereIsNothingToSay()
    {
        var diff = DiffOf(Hunk(1, 1, 1, 1, null, Add(1, "x")));

        Assert.Empty(SymbolChangeSet.Build(diff, new DiffAnnotations(null, null, null)));
        Assert.Empty(SymbolChangeSet.Build(diff, null));
    }

    // Eight lines, so the hunk numbering in each test lines up with the source it describes.
    private static string Class(string body, string note = "    // a note") => string.Join(
        "\n",
        "class AuthService",
        "{",
        note,
        "    void Login(string user)",
        "    {",
        body,
        "    }",
        "}");

    private IReadOnlyList<SymbolChange> Changes(string before, string after, params DiffLine[] lines) =>
        SymbolChangeSet.Build(
            DiffOf(Hunk(1, 8, 1, 8, null, lines)),
            new DiffAnnotations(null, fixture.Outline(after), fixture.Outline(before)));

    private static DiffResult DiffOf(params DiffHunk[] hunks) => new(
        RepoId: Guid.Empty,
        Path: "AuthService.cs",
        OldPath: null,
        Side: DiffSide.Unstaged,
        IsBinary: false,
        IsModeOnly: false,
        OldMode: null,
        NewMode: null,
        Hunks: hunks,
        Truncated: false,
        ErrorMessage: null);

    private static DiffHunk Hunk(
        int oldStart, int oldLines, int newStart, int newLines, string? header, params DiffLine[] lines)
        => new(oldStart, oldLines, newStart, newLines, header, lines);

    private const string NEWLINE = "\n";

    private static DiffLine Rem(int oldLine, string text) => new(DiffLineKind.Removed, oldLine, null, text);
    private static DiffLine Add(int newLine, string text) => new(DiffLineKind.Added, null, newLine, text);
}
