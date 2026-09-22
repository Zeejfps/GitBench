using GitBench.Features.Diff;
using GitBench.Lsp;
using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The server's word on what each type is, laid over the parser's colors. The cases worth having are
/// the ones where the text on screen is not the text the server read: the reader typed after asking.
/// </summary>
public class SemanticColorOverlayTests
{
    private const string Path = "/repo/Mover.cs";

    private static SemanticToken Token(int line, int start, int length, string type) =>
        new(new LspLine(line), new LspCharacter(start), length, new SemanticTokenType(type));

    private static SemanticColorOverlay OverlayOf(string text, params SemanticToken[] tokens) =>
        SemanticColorOverlay.Of(Path, text, new SemanticTokens(tokens));

    [Fact]
    public void AStructIsRecoloredOverTheParsersType_AndTheKeywordBesideItIsLeftAlone()
    {
        const string line = "void Move(int x, Vec by)";
        // The parser: void and int keywords, Vec a type. The server: int and Vec are both structs.
        TokenSpan[] parsed =
        [
            new(0, 4, TokenColorSlot.Keyword), new(10, 3, TokenColorSlot.Keyword), new(17, 3, TokenColorSlot.Type),
        ];
        var overlay = OverlayOf(line, Token(0, 10, 3, "struct"), Token(0, 17, 3, "struct"));

        var spans = overlay.Recolor(DiffLineText.Of(line), parsed);

        Assert.Equal(
            [
                new TokenSpan(0, 4, TokenColorSlot.Keyword),
                new TokenSpan(10, 3, TokenColorSlot.Keyword),
                new TokenSpan(17, 3, TokenColorSlot.Struct),
            ],
            spans);
    }

    [Fact]
    public void ANameTheParserLeftPlainIsColoredToo()
    {
        const string line = "IShape s = Make();";
        var overlay = OverlayOf(line, Token(0, 0, 6, "interface"));

        var spans = overlay.Recolor(DiffLineText.Of(line), parsed: null);

        Assert.Equal([new TokenSpan(0, 6, TokenColorSlot.Interface)], spans);
    }

    [Fact]
    public void KindsThatAreNotTypesKeepTheParsersColors()
    {
        const string line = "Run(count);";
        TokenSpan[] parsed = [new(0, 3, TokenColorSlot.Function), new(4, 5, TokenColorSlot.Variable)];
        var overlay = OverlayOf(line, Token(0, 0, 3, "method"), Token(0, 4, 5, "parameter"));

        Assert.True(overlay.IsEmpty);
        Assert.Same(parsed, overlay.Recolor(DiffLineText.Of(line), parsed));
    }

    [Fact]
    public void ALineThatMovedSinceTheServerReadItKeepsItsColors()
    {
        var overlay = OverlayOf("class A\n{\n    Vec at;\n}", Token(2, 4, 3, "struct"));

        // Two lines typed above it: the line now sits at a different number, and reads the same.
        var spans = overlay.Recolor(DiffLineText.Of("    Vec at;"), parsed: null);

        Assert.Equal([new TokenSpan(4, 3, TokenColorSlot.Struct)], spans);
    }

    [Fact]
    public void ALineEditedSinceTheServerReadItFallsBackToTheParser()
    {
        var overlay = OverlayOf("    Vec at;", Token(0, 4, 3, "struct"));
        TokenSpan[] parsed = [new(8, 3, TokenColorSlot.Type)];

        Assert.Same(parsed, overlay.Recolor(DiffLineText.Of("    VecX at;"), parsed));
    }

    [Fact]
    public void ColumnsAreTabExpandedTheWayThePainterDrawsThem()
    {
        var overlay = OverlayOf("\tVec at;", Token(0, 1, 3, "struct"));

        var spans = overlay.Recolor(DiffLineText.Of("\tVec at;"), parsed: null);

        Assert.Equal([new TokenSpan(DiffOptions.TabWidth, 3, TokenColorSlot.Struct)], spans);
    }

    [Fact]
    public void ATokenThatDoesNotFitItsLineIsDropped()
    {
        var overlay = OverlayOf("Vec", Token(0, 1, 5, "struct"), Token(4, 0, 1, "struct"));

        Assert.True(overlay.IsEmpty);
    }

    [Theory]
    [InlineData("struct", (int)TokenColorSlot.Struct)]
    [InlineData("struct name", (int)TokenColorSlot.Struct)]
    [InlineData("record struct name", (int)TokenColorSlot.Struct)]
    [InlineData("class", (int)TokenColorSlot.Type)]
    [InlineData("class name", (int)TokenColorSlot.Type)]
    [InlineData("interface", (int)TokenColorSlot.Interface)]
    [InlineData("enum name", (int)TokenColorSlot.Enum)]
    [InlineData("typeParameter", (int)TokenColorSlot.TypeParameter)]
    public void BothTheStandardAndRoslynsNamesAreRead(string type, int slot)
    {
        Assert.Equal((TokenColorSlot)slot, SemanticTokenMap.Map(new SemanticTokenType(type)));
    }
}
