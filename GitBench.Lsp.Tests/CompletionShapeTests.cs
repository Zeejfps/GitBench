using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>Every shape a completion answer arrives in, collapsed at the boundary: a bare array, a
/// list that may be incomplete, plain and insert-or-replace edits, defaults the list hoists out of
/// its items, and snippets reduced to the text they would insert.</summary>
public sealed class CompletionShapeTests
{
    private static LspCompletions Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return LspCompletions.Reader.Read(document.RootElement);
    }

    private static ServerCapabilities Capabilities(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ServerCapabilities.Reader.Read(document.RootElement);
    }

    [Fact]
    public void NullIsNothingAndABareArrayIsComplete()
    {
        Assert.Empty(Read("null").Items);

        var list = Read("""[{"label":"Console","kind":7,"detail":"class System.Console"}]""");

        Assert.False(list.IsIncomplete);
        var item = Assert.Single(list.Items);
        Assert.Equal("Console", item.Label);
        Assert.Equal(LspCompletionKind.Class, item.Kind);
        Assert.Equal("class System.Console", item.Detail);
        Assert.Equal("Console", item.InsertText);
        Assert.Null(item.InsertRange);
    }

    [Fact]
    public void AListSaysWhetherTypingMoreMayBringOthers()
    {
        var list = Read("""{"isIncomplete":true,"items":[{"label":"a"},{"label":"b"}]}""");

        Assert.True(list.IsIncomplete);
        Assert.Equal(["a", "b"], list.Items.Select(i => i.Label));
    }

    [Fact]
    public void APlainEditIsBothTheInsertAndTheReplaceRange()
    {
        var item = Read("""
            [{"label":"WriteLine","textEdit":{"range":{"start":{"line":3,"character":8},"end":{"line":3,"character":11}},"newText":"WriteLine"}}]
            """).Items[0];

        Assert.Equal(new LspPosition(new LspLine(3), new LspCharacter(8)), item.InsertRange!.Value.Start);
        Assert.Equal(item.InsertRange, item.ReplaceRange);
    }

    [Fact]
    public void AnInsertOrReplaceEditKeepsBothRanges()
    {
        var item = Read("""
            [{"label":"total","textEdit":{"newText":"total",
              "insert":{"start":{"line":0,"character":4},"end":{"line":0,"character":6}},
              "replace":{"start":{"line":0,"character":4},"end":{"line":0,"character":9}}}}]
            """).Items[0];

        Assert.Equal(6, item.InsertRange!.Value.End.Character.Value);
        Assert.Equal(9, item.ReplaceRange!.Value.End.Character.Value);
    }

    [Fact]
    public void AnEditRangeTheListHoistedAppliesToEveryItem()
    {
        var list = Read("""
            {"isIncomplete":false,
             "itemDefaults":{"editRange":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}}},
             "items":[{"label":"alpha"},{"label":"beta","insertText":"beta()"}]}
            """);

        Assert.All(list.Items, item => Assert.Equal(2, item.InsertRange!.Value.Start.Character.Value));
        Assert.Equal("beta()", list.Items[1].InsertText);
    }

    [Fact]
    public void ASnippetIsInsertedAsTheTextItWouldStartWith()
    {
        var item = Read("""
            [{"label":"for","insertTextFormat":2,"insertText":"for (${1:int} ${2:i} = 0; $2 < ${3|length,count|}; $2++)$0"}]
            """).Items[0];

        Assert.Equal("for (int i = 0;  < length; ++)", item.InsertText);
    }

    [Fact]
    public void EscapedDollarsSurviveASnippet() =>
        Assert.Equal("cost: $5 {x}", Snippets.Plain(@"cost: \$5 {x}$0"));

    [Fact]
    public void AdditionalEditsComeWithTheItem()
    {
        var item = Read("""
            [{"label":"List","additionalTextEdits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"using System.Collections.Generic;\n"}]}]
            """).Items[0];

        var edit = Assert.Single(item.AdditionalEdits);
        Assert.StartsWith("using System.Collections.Generic;", edit.NewText);
    }

    [Fact]
    public void AKindOutsideTheProtocolsNumberingIsNoKind() =>
        Assert.Null(Read("""[{"label":"x","kind":99}]""").Items[0].Kind);

    [Fact]
    public void TheCapabilityCarriesItsTriggerCharacters()
    {
        var offered = Capabilities("""{"capabilities":{"completionProvider":{"triggerCharacters":[".","::"," "]}}}""").Completion;

        var triggers = Assert.IsType<CompletionSupport.Offered>(offered).TriggerCharacters;
        Assert.Equal(['.', ' '], triggers);
        Assert.IsType<CompletionSupport.None>(Capabilities("""{"capabilities":{}}""").Completion);
    }

    [Fact]
    public void TheRequestSaysWhichCharacterTriggeredIt()
    {
        var request = LspRequests.Completion(
            DocumentUri.OfFile(OperatingSystem.IsWindows() ? @"C:\repo\a.cs" : "/repo/a.cs"),
            new LspPosition(new LspLine(2), new LspCharacter(9)),
            new CompletionAsk.TypedTrigger('.'));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) request.WriteParams(writer);
        using var written = JsonDocument.Parse(stream.ToArray());
        var context = written.RootElement.GetProperty("context");

        Assert.Equal(2, context.GetProperty("triggerKind").GetInt32());
        Assert.Equal(".", context.GetProperty("triggerCharacter").GetString());
    }
}
