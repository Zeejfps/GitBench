using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>Parameter info as a server sends it, collapsed at the boundary: parameter labels held as
/// spans of their signature's label whichever of the two shapes they came in, the active parameter
/// read per overload before the call's, and nothing for a position in no call.</summary>
public sealed class SignatureHelpShapeTests
{
    private static SignatureHelp Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SignatureHelp.Reader.Read(document.RootElement);
    }

    [Fact]
    public void NullAndNoSignaturesAreBothNothing()
    {
        Assert.Empty(Read("null").Signatures);
        Assert.Empty(Read("""{"signatures":[]}""").Signatures);
    }

    // `(int a, int b)`: searched from the end of the one before, so the second `int` is found where
    // it is rather than at the first.
    [Fact]
    public void SubstringLabelsAreFoundInOrder()
    {
        var help = Read("""
            {"signatures":[{"label":"Add(int a, int b)","parameters":[{"label":"int a"},{"label":"int b"}]}],"activeParameter":1}
            """);

        var signature = Assert.Single(help.Signatures);
        Assert.Equal(new ParameterSpan(4, 5), signature.Parameters[0]);
        Assert.Equal(new ParameterSpan(11, 5), signature.Parameters[1]);
        Assert.Equal(1, help.ActiveParameterOf(0));
    }

    [Fact]
    public void OffsetLabelsAreSpansAsGiven()
    {
        var help = Read("""
            {"signatures":[{"label":"f(x, y)","parameters":[{"label":[2,3]},{"label":[5,6]}]}]}
            """);

        Assert.Equal(new ParameterSpan(5, 1), help.Signatures[0].Parameters[1]);
    }

    [Fact]
    public void AnOverloadsOwnActiveParameterWinsOverTheCalls()
    {
        var help = Read("""
            {"signatures":[
               {"label":"M()","parameters":[]},
               {"label":"M(int x, int y)","parameters":[{"label":"int x"},{"label":"int y"}],"activeParameter":1}],
             "activeSignature":1,"activeParameter":0}
            """);

        Assert.Equal(1, help.ActiveSignature);
        Assert.Equal(1, help.ActiveParameterOf(1));
        Assert.Equal(0, help.ActiveParameterOf(0));
    }

    // csharp-ls omits it for the first argument, and the protocol says omitted means the first.
    [Fact]
    public void NoActiveParameterAtAllMeansTheFirst()
    {
        var help = Read("""{"signatures":[{"label":"int Twice(int value)","parameters":[{"label":"int value"}]}]}""");

        Assert.Equal(0, help.ActiveParameterOf(0));
    }

    [Fact]
    public void AnActiveSignaturePastTheEndIsTheLastOne() =>
        Assert.Equal(0, Read("""{"signatures":[{"label":"f()"}],"activeSignature":7}""").ActiveSignature);

    [Fact]
    public void DocumentationIsReadFromEitherShape()
    {
        var help = Read("""
            {"signatures":[{"label":"a()","documentation":"plain"},{"label":"b()","documentation":{"kind":"markdown","value":"marked"}}]}
            """);

        Assert.Equal(["plain", "marked"], help.Signatures.Select(s => s.Documentation));
    }

    [Fact]
    public void TheCapabilityKeepsTriggersAndRetriggersApart()
    {
        using var document = JsonDocument.Parse(
            """{"capabilities":{"signatureHelpProvider":{"triggerCharacters":["(",","],"retriggerCharacters":[")"]}}}""");
        var support = ServerCapabilities.Reader.Read(document.RootElement).SignatureHelp;

        var offered = Assert.IsType<SignatureHelpSupport.Offered>(support);
        Assert.Equal(['(', ','], offered.Triggers);
        Assert.Equal([')'], offered.Retriggers);
    }

    [Fact]
    public void AskingAgainWhileShowingIsARetrigger()
    {
        var request = LspRequests.SignatureHelp(
            DocumentUri.OfFile(OperatingSystem.IsWindows() ? @"C:\repo\a.cs" : "/repo/a.cs"),
            new LspPosition(new LspLine(0), new LspCharacter(4)),
            SignatureAsk.Following);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) request.WriteParams(writer);
        using var written = JsonDocument.Parse(stream.ToArray());
        var context = written.RootElement.GetProperty("context");

        Assert.Equal(3, context.GetProperty("triggerKind").GetInt32());
        Assert.True(context.GetProperty("isRetrigger").GetBoolean());
    }
}
