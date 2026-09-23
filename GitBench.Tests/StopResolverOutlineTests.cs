using GitBench.Features.Editor;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>Stops placed against a real C# outline.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class StopResolverOutlineTests(CodeIntelFixture fixture)
{
    private const string Calculator =
        "namespace Demo;\n"
        + "\n"
        + "public sealed class Calculator\n"
        + "{\n"
        + "    public int Add(int a, int b) => a + b;\n"
        + "\n"
        + "    public int Subtract(int a, int b) => a - b;\n"
        + "}\n";

    [Fact]
    public void ANewMethodAfterAnExpressionBodiedOne_StartsAtTheEndOfItsLine()
    {
        var placement = StopResolver.Resolve("C:/r/Calculator.cs", Calculator, fixture.Outline(Calculator),
            new StopTarget("Calculator.cs", "Multiply", "Calculator.Subtract"));

        var at = Assert.IsType<StopLocation.Insertion>(Assert.IsType<StopPlacement.Placed>(placement).Location);
        Assert.Equal(TextPosition.At(7, "    public int Subtract(int a, int b) => a - b;".Length), at.At);
    }

    [Fact]
    public void AMethod_LandsOnItsName()
    {
        var placement = StopResolver.Resolve("C:/r/Calculator.cs", Calculator, fixture.Outline(Calculator),
            new StopTarget("Calculator.cs", "Subtract", null));

        var at = Assert.IsType<StopLocation.OnSymbol>(Assert.IsType<StopPlacement.Placed>(placement).Location);
        Assert.Equal(TextPosition.At(7, 15), at.At);
    }
}
