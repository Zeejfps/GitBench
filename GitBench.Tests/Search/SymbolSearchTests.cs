using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Search;
using GitBench.Lsp;
using Xunit;

namespace GitBench.Tests.Search;

/// <summary>Ranking declarations by name: the ladder, the matched letters, containers and tie-breaks.</summary>
public class SymbolSearchTests
{
    private static SymbolRow Row(string name, SymbolKind kind = SymbolKind.Class, string? container = null, string path = "src/A.cs", int line = 1) =>
        new(name, kind, container, null, path, new FileLine(line), new RawColumn(0));

    [Theory]
    [InlineData("FileBrowserViewModel", "FileBrowserViewModel", 1040)]
    [InlineData("filebrowserviewmodel", "FileBrowserViewModel", 1000)]
    [InlineData("FileBrowser", "FileBrowserViewModel", 840)]
    [InlineData("filebrowser", "FileBrowserViewModel", 800)]
    [InlineData("FBVM", "FileBrowserViewModel", 700)]
    [InlineData("fbvm", "FileBrowserViewModel", 700)]
    [InlineData("FilBrVM", "FileBrowserViewModel", 700)]
    [InlineData("VM", "FileBrowserViewModel", 650)]
    [InlineData("rowser", "FileBrowserViewModel", 640)]
    [InlineData("fwm", "FileBrowserViewModel", 300)]
    [InlineData("HTTPS", "HTTPServer", 840)]
    [InlineData("HS", "HTTPServer", 700)]
    public void TheLadder(string query, string name, int score)
    {
        Assert.Equal(score, SymbolSearch.Match(query, name)?.Score);
    }

    [Theory]
    [InlineData("xyz", "FileBrowserViewModel")]
    [InlineData("MVF", "FileBrowserViewModel")]
    [InlineData("FileBrowserViewModels", "FileBrowserViewModel")]
    [InlineData("", "FileBrowserViewModel")]
    public void NoMatch(string query, string name)
    {
        Assert.Null(SymbolSearch.Match(query, name));
    }

    [Theory]
    [InlineData("FBVM", "FileBrowserViewModel", new[] { 0, 4, 11, 15 })]
    [InlineData("FilBrVM", "FileBrowserViewModel", new[] { 0, 1, 2, 4, 5, 11, 15 })]
    [InlineData("rowser", "FileBrowserViewModel", new[] { 5, 6, 7, 8, 9, 10 })]
    [InlineData("File", "FileBrowserViewModel", new[] { 0, 1, 2, 3 })]
    public void TheMatchedLetters(string query, string name, int[] highlights)
    {
        Assert.Equal(highlights, SymbolSearch.Match(query, name)!.Value.Highlights);
    }

    [Theory]
    [InlineData("Auth.Login", "Auth", "Login")]
    [InlineData("Auth.", "Auth", "")]
    [InlineData("A.B.Login", "A.B", "Login")]
    [InlineData(".Login", null, "Login")]
    [InlineData("  Login ", null, "Login")]
    public void ADotSplitsContainerFromName(string text, string? container, string name)
    {
        Assert.Equal(new SymbolQuery(container, name), SymbolQuery.Parse(text));
    }

    [Fact]
    public void AContainerQuery_MatchesOnlyMembersOfAMatchingContainer()
    {
        var rows = new[]
        {
            Row("Login", SymbolKind.Method, container: "AuthService"),
            Row("Login", SymbolKind.Method, container: "UserController"),
            Row("Login", SymbolKind.Class),
        };

        var ranked = SymbolSearch.Rank(rows, SymbolQuery.Parse("Auth.Login"), _ => true, 10);

        Assert.Equal("AuthService", Assert.Single(ranked).Row.Container);
    }

    [Fact]
    public void ABareContainer_ListsEverythingInIt()
    {
        var rows = new[]
        {
            Row("Login", SymbolKind.Method, container: "AuthService"),
            Row("Logout", SymbolKind.Method, container: "AuthService"),
            Row("Other", SymbolKind.Method, container: "Else"),
        };

        var ranked = SymbolSearch.Rank(rows, SymbolQuery.Parse("AuthService."), _ => true, 10);

        Assert.Equal(["Login", "Logout"], ranked.Select(r => r.Row.Name));
    }

    [Fact]
    public void BetterRungsRankFirst_ThenTypesBeforeMembers_ThenShorterNames()
    {
        var rows = new[]
        {
            Row("ParseMore", SymbolKind.Method),
            Row("Parse", SymbolKind.Method),
            Row("Parse", SymbolKind.Class),
            Row("ParserHelper", SymbolKind.Class),
            Row("SomeParse", SymbolKind.Class),
        };

        var ranked = SymbolSearch.Rank(rows, SymbolQuery.Parse("Parse"), _ => true, 10);

        Assert.Equal(
            [("Parse", SymbolKind.Class), ("Parse", SymbolKind.Method), ("ParserHelper", SymbolKind.Class), ("ParseMore", SymbolKind.Method), ("SomeParse", SymbolKind.Class)],
            ranked.Select(r => (r.Row.Name, r.Row.Kind)));
    }

    [Fact]
    public void TheFilterAndTheLimitApply()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Row($"Item{i}", i % 2 == 0 ? SymbolKind.Class : SymbolKind.Method)).ToArray();

        var types = SymbolSearch.Rank(rows, SymbolQuery.Parse("Item"), r => r.IsType, 3);

        Assert.Equal(3, types.Count);
        Assert.All(types, r => Assert.Equal(SymbolKind.Class, r.Row.Kind));
    }
}

/// <summary>Merging the index's rows with language servers' answers: a pure function of the two.</summary>
public class SymbolMergeTests
{
    private static readonly LanguageId CSharp = LanguageId.Of("csharp");
    private static readonly LanguageId Rust = LanguageId.Of("rust");

    private static SymbolRow Row(string name, string path, int line, SymbolKind kind = SymbolKind.Class) =>
        new(name, kind, null, null, path, new FileLine(line), new RawColumn(0));

    private static LanguageId? LanguageOf(string path) =>
        path.EndsWith(".cs") ? CSharp : path.EndsWith(".rs") ? Rust : null;

    private static IReadOnlyList<RankedSymbol> Index(SymbolQuery query, params SymbolRow[] rows) =>
        SymbolSearch.Rank(rows, query, _ => true, 100);

    private static IReadOnlyList<SymbolHit> Merge(
        SymbolQuery query, IReadOnlyList<RankedSymbol> index, Dictionary<LanguageId, IReadOnlyList<SymbolRow>> answers) =>
        SymbolMerge.Merge(index, answers, query, LanguageOf, _ => true, 100);

    [Fact]
    public void WithNoAnswers_TheIndexStandsAlone()
    {
        var query = SymbolQuery.Parse("Parser");
        var index = Index(query, Row("Parser", "a.cs", 3), Row("ParserX", "b.rs", 3));

        var merged = Merge(query, index, new());

        Assert.Equal(["Parser", "ParserX"], merged.Select(h => h.Row.Name));
        Assert.All(merged, h => Assert.Equal(SymbolSource.Index, h.Source));
    }

    [Fact]
    public void AServersRowsLeadForItsLanguage_AndReplaceTheIndexRowsItReturned()
    {
        var query = SymbolQuery.Parse("Parser");
        var index = Index(query, Row("Parser", "a.cs", 3), Row("Parser", "lib.rs", 10));
        var answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>
        {
            [CSharp] = [Row("Parser", "a.cs", 4)],
        };

        var merged = Merge(query, index, answers);

        Assert.Equal(
            [("a.cs", SymbolSource.Server), ("lib.rs", SymbolSource.Index)],
            merged.Select(h => (h.Row.Path, h.Source)));
    }

    [Fact]
    public void IndexRowsTheServerDidNotReturn_StayBelowTheFirstRows()
    {
        var query = SymbolQuery.Parse("Parser");
        var index = Index(query, Row("Parser", "a.cs", 3), Row("Parser", "b.cs", 3), Row("ParserZ", "c.rs", 1));
        var answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>
        {
            [CSharp] = [Row("Parser", "a.cs", 3)],
        };

        var merged = Merge(query, index, answers);

        Assert.Equal(
            [("a.cs", SymbolSource.Server), ("c.rs", SymbolSource.Index), ("b.cs", SymbolSource.Index)],
            merged.Select(h => (h.Row.Path, h.Source)));
    }

    [Fact]
    public void ARowTwoLinesAway_IsNotTheSameDeclaration()
    {
        var query = SymbolQuery.Parse("Parser");
        var index = Index(query, Row("Parser", "a.cs", 3));
        var answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>
        {
            [CSharp] = [Row("Parser", "a.cs", 5)],
        };

        var merged = Merge(query, index, answers);

        Assert.Equal([SymbolSource.Server, SymbolSource.Index], merged.Select(h => h.Source));
    }

    [Fact]
    public void AServerRowTheAppWouldNotHaveMatched_IsKept()
    {
        var query = SymbolQuery.Parse("prs");
        var answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>
        {
            [CSharp] = [Row("Tokenizer", "a.cs", 1)],
        };

        var merged = Merge(query, [], answers);

        Assert.Equal("Tokenizer", Assert.Single(merged).Row.Name);
    }

    [Fact]
    public void TheOrderAnswersArriveIn_DoesNotChangeTheResult()
    {
        var query = SymbolQuery.Parse("Parser");
        var index = Index(query, Row("Parser", "a.cs", 3), Row("Parser", "b.rs", 3));
        var csharp = new KeyValuePair<LanguageId, IReadOnlyList<SymbolRow>>(CSharp, [Row("Parser", "a.cs", 3)]);
        var rust = new KeyValuePair<LanguageId, IReadOnlyList<SymbolRow>>(Rust, [Row("Parser", "b.rs", 3), Row("ParserExt", "c.rs", 1)]);

        var one = Merge(query, index, new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>([csharp, rust]));
        var other = Merge(query, index, new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>([rust, csharp]));

        Assert.Equal(one.Select(h => (h.Row, h.Source)), other.Select(h => (h.Row, h.Source)));
    }

    [Fact]
    public void TheFilterAppliesToServerRowsToo()
    {
        var query = SymbolQuery.Parse("Parse");
        var answers = new Dictionary<LanguageId, IReadOnlyList<SymbolRow>>
        {
            [CSharp] = [Row("Parse", "a.cs", 1, SymbolKind.Method), Row("Parser", "a.cs", 9, SymbolKind.Class)],
        };

        var types = SymbolMerge.Merge([], answers, query, LanguageOf, r => r.IsType, 100);

        Assert.Equal("Parser", Assert.Single(types).Row.Name);
    }
}
