using GitBench.Features.CodeIntel;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>A stop lands on its declaration by name — through the outline, then by the name as a
/// whole word — and a declaration still to be written lands after the one it names.</summary>
public sealed class StopResolverTests
{
    private const string Path = "C:/repo/src/Client.cs";

    private const string Text =
        "namespace App;\n"
        + "class Client\n"
        + "{\n"
        + "    void Fetch(int tries)\n"
        + "    {\n"
        + "    }\n"
        + "\n"
        + "    void Send() { }\n"
        + "}\n";

    private static OutlineNode Node(string name, SymbolKind kind, int start, int end, int column, params OutlineNode[] children) =>
        new(name, kind, null, start, end, start, new FileLine(start), new RawColumn(column), children);

    private static readonly FileOutline Outline = new(
    [
        Node("App", SymbolKind.Namespace, 1, 9, 10,
            Node("Client", SymbolKind.Class, 2, 9, 6,
                Node("Fetch", SymbolKind.Method, 4, 6, 9),
                Node("Send", SymbolKind.Method, 8, 8, 9))),
    ]);

    private static StopLocation Placed(StopPlacement placement) => Assert.IsType<StopPlacement.Placed>(placement).Location;

    [Theory]
    [InlineData("Fetch")]
    [InlineData("Client.Fetch")]
    [InlineData("Client.Fetch(int)")]
    [InlineData("client.fetch")]
    public void Symbol_LandsOnItsName(string symbol)
    {
        var at = Assert.IsType<StopLocation.OnSymbol>(Placed(StopResolver.Resolve(Path, Text, Outline, new StopTarget("src/Client.cs", symbol, null))));

        Assert.Equal(TextPosition.At(4, 9), at.At);
        Assert.Equal("    void Fetch(int tries)", at.LineText);
    }

    [Fact]
    public void WithoutAnOutline_TheNameIsFoundAsAWholeWord()
    {
        var at = Assert.IsType<StopLocation.OnSymbol>(Placed(StopResolver.Resolve(Path, Text, null, new StopTarget("src/Client.cs", "Send", null))));

        Assert.Equal(TextPosition.At(8, 9), at.At);
    }

    [Fact]
    public void ANewSymbol_StartsAtTheEndOfTheOneItFollows()
    {
        var at = Assert.IsType<StopLocation.Insertion>(Placed(StopResolver.Resolve(Path, Text, Outline, new StopTarget("src/Client.cs", "Retry", "Fetch"))));

        Assert.Equal(TextPosition.At(6, 5), at.At);
    }

    [Fact]
    public void AMissingSymbol_WithoutAfter_ListsWhatIsDeclared()
    {
        var miss = Assert.IsType<StopMiss.NoSuchSymbol>(
            Assert.IsType<StopPlacement.Missed>(StopResolver.Resolve(Path, Text, Outline, new StopTarget("src/Client.cs", "Retry", null))).Miss);

        Assert.Equal(["Client", "Client.Fetch", "Client.Send"], miss.Known);
    }

    [Fact]
    public void AMissingAfter_IsAMiss()
    {
        Assert.IsType<StopMiss.NoSuchAfter>(
            Assert.IsType<StopPlacement.Missed>(StopResolver.Resolve(Path, Text, Outline, new StopTarget("src/Client.cs", "Retry", "Nope"))).Miss);
    }

    [Fact]
    public void NoFile_IsANewFile()
    {
        Assert.IsType<StopLocation.NewFile>(Placed(StopResolver.Resolve(Path, null, null, new StopTarget("src/Client.cs", "Client", null))));
        Assert.IsType<StopLocation.NewFile>(Placed(StopResolver.Resolve(Path, "\n", null, new StopTarget("src/Client.cs", "Client", null))));
    }

    [Fact]
    public void ANewSymbol_AlreadyCalledInTheFile_StillGoesAfterItsAnchor()
    {
        var calling = Text.Replace("void Send() { }", "void Send() { Retry(); }");

        var at = Assert.IsType<StopLocation.Insertion>(Placed(StopResolver.Resolve(Path, calling, Outline, new StopTarget("src/Client.cs", "Retry", "Fetch"))));

        Assert.Equal(TextPosition.At(6, 5), at.At);
    }
}
