using GitBench.Theming;
using Xunit;

namespace GitBench.Tests;

public class ScopeColorMapTests
{
    // expected is passed as int because the public test signature can't expose the internal
    // TokenColorSlot enum (CS0051); it's cast back inside.
    [Theory]
    [InlineData("keyword.control.cs", (int)TokenColorSlot.Keyword)]
    [InlineData("keyword.operator.assignment.cs", (int)TokenColorSlot.Operator)]
    [InlineData("string.quoted.double.ts", (int)TokenColorSlot.String)]
    [InlineData("comment.block", (int)TokenColorSlot.Comment)]
    [InlineData("comment.line.double-slash.cs", (int)TokenColorSlot.Comment)]
    [InlineData("constant.numeric.decimal.cs", (int)TokenColorSlot.Number)]
    [InlineData("constant.language.boolean.true.cs", (int)TokenColorSlot.Constant)]
    [InlineData("storage.modifier.cs", (int)TokenColorSlot.Keyword)]
    [InlineData("entity.name.function.cs", (int)TokenColorSlot.Function)]
    [InlineData("entity.name.type.class.cs", (int)TokenColorSlot.Type)]
    [InlineData("punctuation.terminator.statement.cs", (int)TokenColorSlot.Punctuation)]
    public void MapsRepresentativeScopes(string scope, int expected)
        => Assert.Equal((TokenColorSlot)expected, ScopeColorMap.Map(scope));

    [Fact]
    public void LongestPrefixWins_NumericBeatsConstant()
    {
        // "constant" → Constant, but "constant.numeric" is the longer matching prefix.
        Assert.Equal(TokenColorSlot.Number, ScopeColorMap.Map("constant.numeric.decimal.cs"));
        Assert.Equal(TokenColorSlot.Constant, ScopeColorMap.Map("constant.language.null.cs"));
    }

    [Fact]
    public void CommentAndStringDelimitersStayWithParentColor()
    {
        Assert.Equal(TokenColorSlot.Comment, ScopeColorMap.Map("punctuation.definition.comment.cs"));
        Assert.Equal(TokenColorSlot.String, ScopeColorMap.Map("punctuation.definition.string.begin.cs"));
    }

    [Theory]
    [InlineData("source.cs")]
    [InlineData("meta.class.body.cs")]
    [InlineData("keywordish.not-a-keyword")] // must not match "keyword" across a non-dot boundary
    [InlineData("")]
    public void UnknownScopesFallBackToDefault(string scope)
        => Assert.Equal(TokenColorSlot.Default, ScopeColorMap.Map(scope));
}
