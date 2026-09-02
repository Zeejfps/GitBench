using GitBench.Features.FileBrowser;
using GitBench.Localization;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public sealed class FileBrowserTabLabelTests : IDisposable
{
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));

    public void Dispose() => _loc.Dispose();

    [Fact]
    public void ATabWithANameOfItsOwnJustSaysIt()
    {
        var tabs = Tabs("src/Auth.cs", "src/deep/Token.cs");

        Assert.Equal(["Auth.cs", "Token.cs"], Labels(tabs));
    }

    [Fact]
    public void TabsSharingANameSayWhichDirectoryTheyAreIn()
    {
        var tabs = Tabs("src/auth/index.ts", "src/store/index.ts");

        Assert.Equal(["index.ts (auth)", "index.ts (store)"], Labels(tabs));
    }

    // As much of the path as it takes and no more: the directory beside the file is the same one on
    // both, so the qualifier grows by one and stops.
    [Fact]
    public void OnlyAsMuchOfThePathAsItTakesToTellThemApart()
    {
        var tabs = Tabs("packages/web/src/index.ts", "packages/api/src/index.ts");

        Assert.Equal(["index.ts (web/src)", "index.ts (api/src)"], Labels(tabs));
    }

    [Fact]
    public void OnlyTheTabsThatClashAreQualified()
    {
        var tabs = Tabs("src/auth/index.ts", "src/store/index.ts", "src/Auth.cs");

        Assert.Equal(["index.ts (auth)", "index.ts (store)", "Auth.cs"], Labels(tabs));
    }

    // The qualifier belongs to the strip, not to the file: it means "the one that is not the other".
    [Fact]
    public void ClosingTheTabItWasBeingToldApartFromPutsThePlainNameBack()
    {
        var tabs = Tabs("src/auth/index.ts", "src/store/index.ts");
        var kept = tabs[0];

        Assert.Equal("index.ts", FileBrowserTabLabels.For(_loc.Strings.Value, [kept], kept));
    }

    // A file open from outside the working tree is qualified like any other: the strip has no
    // notion of which of them is the repository's.
    [Fact]
    public void AFileFromSomewhereElseEntirelyIsToldApartTheSameWay()
    {
        var tabs = Tabs("src/auth/index.ts");
        tabs.Add(new FileBrowserTab(
            Path.Combine(Path.GetTempPath(), "vendor", "index.ts"), transient: false));

        Assert.Equal(["index.ts (auth)", "index.ts (vendor)"], Labels(tabs));
    }

    private static List<FileBrowserTab> Tabs(params string[] relatives) =>
        [.. relatives.Select(r => new FileBrowserTab(
            Path.GetFullPath(r.Replace('/', Path.DirectorySeparatorChar)), transient: false))];

    private IReadOnlyList<string> Labels(IReadOnlyList<FileBrowserTab> tabs) =>
        [.. tabs.Select(tab => FileBrowserTabLabels.For(_loc.Strings.Value, tabs, tab))];
}
