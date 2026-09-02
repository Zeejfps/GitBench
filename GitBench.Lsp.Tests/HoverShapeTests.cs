using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// Hover contents arrive in three shapes — a bare string, a language-tagged snippet, an array of
// either — and MarkupContent on top of that, plus null for "nothing here". These pin the collapse of
// all of them into one closed type at the boundary, so no caller ever branches on a wire shape and
// none of them can be handed markup that was never meant as markup.
public sealed class HoverShapeTests
{
    private static Hover Read(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return Hover.Reader.Read(document.RootElement);
    }

    private static Hover.Text TextOf(string resultJson) => Assert.IsType<Hover.Text>(Read(resultJson));

    // ---- nothing to show, four ways ----

    [Theory]
    [InlineData("null")]
    [InlineData("""{"contents":null}""")]
    [InlineData("""{"contents":""}""")]
    [InlineData("""{"contents":"   \n  "}""")]
    [InlineData("""{"contents":[]}""")]
    [InlineData("""{"contents":["",""]}""")]
    [InlineData("{}")]
    public void An_empty_answer_is_nothing_to_show(string resultJson)
    {
        Assert.IsType<Hover.None>(Read(resultJson));
    }

    // ---- the three shapes ----

    [Fact]
    public void A_bare_string_is_markdown_as_the_protocol_defines_it()
    {
        var hover = TextOf("""{"contents":"`Foo` — a thing"}""");

        Assert.Equal(MarkupKind.Markdown, hover.Kind);
        Assert.Equal("`Foo` — a thing", hover.Value);
    }

    [Fact]
    public void A_language_tagged_snippet_becomes_a_fenced_block_in_the_same_type()
    {
        var hover = TextOf("""{"contents":{"language":"rust","value":"fn main()"}}""");

        Assert.Equal(MarkupKind.Markdown, hover.Kind);
        Assert.Contains("```rust", hover.Value);
        Assert.Contains("fn main()", hover.Value);
    }

    [Fact]
    public void Markup_content_keeps_the_kind_the_server_declared()
    {
        var hover = TextOf("""{"contents":{"kind":"markdown","value":"# Title"}}""");

        Assert.Equal(MarkupKind.Markdown, hover.Kind);
        Assert.Equal("# Title", hover.Value);
    }

    [Fact]
    public void Plain_text_is_not_quietly_promoted_to_markdown()
    {
        // gopls answers in plaintext. Rendering `_x_ *y*` as markdown would eat the underscores.
        var hover = TextOf("""{"contents":{"kind":"plaintext","value":"_x_ *y* <b>"}}""");

        Assert.Equal(MarkupKind.PlainText, hover.Kind);
        Assert.Equal("_x_ *y* <b>", hover.Value);
    }

    [Fact]
    public void An_unrecognised_markup_kind_is_treated_as_plain_text()
    {
        Assert.Equal(MarkupKind.PlainText, TextOf("""{"contents":{"kind":"asciidoc","value":"x"}}""").Kind);
    }

    [Fact]
    public void An_array_keeps_every_part_in_the_order_the_server_ranked_them()
    {
        var hover = TextOf("""{"contents":["first",{"language":"rust","value":"second"},"third"]}""");

        Assert.True(
            hover.Value.IndexOf("first", StringComparison.Ordinal) <
            hover.Value.IndexOf("second", StringComparison.Ordinal),
            "the parts must keep their order");
        Assert.Contains("third", hover.Value);
    }

    [Fact]
    public void An_array_with_one_usable_part_still_shows_it()
    {
        Assert.Contains("only", TextOf("""{"contents":["","only",""]}""").Value);
    }

    // ---- the range that comes with it ----

    [Fact]
    public void The_range_a_hover_covers_is_read_into_typed_positions()
    {
        var hover = TextOf("""{"contents":"x","range":{"start":{"line":4,"character":2},"end":{"line":4,"character":9}}}""");

        Assert.NotNull(hover.Range);
        var range = hover.Range.Value;
        Assert.Equal(new LspLine(4), range.Start.Line);
        Assert.Equal(new LspCharacter(2), range.Start.Character);
        Assert.Equal(new LspCharacter(9), range.End.Character);
    }

    [Fact]
    public void A_hover_without_a_range_has_none_rather_than_a_zero_one()
    {
        Assert.Null(TextOf("""{"contents":"x"}""").Range);
    }

    // ---- shapes that are not hovers at all ----

    [Theory]
    [InlineData("""{"contents":42}""")]
    [InlineData("""{"contents":true}""")]
    [InlineData("""{"contents":{"kind":"markdown"}}""")]
    [InlineData("""{"contents":{"language":"rust"}}""")]
    [InlineData("""{"contents":[42]}""")]
    [InlineData("""{"contents":"x","range":{"start":{"line":-1,"character":0},"end":{"line":1,"character":0}}}""")]
    [InlineData("""{"contents":"x","range":{"start":{"line":1},"end":{"line":2,"character":0}}}""")]
    [InlineData("\"a string, not a hover\"")]
    public void A_shape_the_protocol_does_not_allow_is_refused(string resultJson)
    {
        Assert.Throws<LspParseException>(() => Read(resultJson));
    }
}
