using System.Text.Json;
using Xunit;

namespace GitBench.Lsp.Tests;

// Go to definition comes back as one location, an array of locations, or an array of the richer links
// — decided per element, since an array may mix them. These pin the collapse into one closed list and,
// more importantly, which range the caller is meant to jump to: a link's selection range is the name,
// its target range is the whole body, and jumping to the wrong one looks exactly like working.
public sealed class DefinitionShapeTests
{
    private const string ALocation =
        """{"uri":"file:///repo/src/lib.rs","range":{"start":{"line":10,"character":4},"end":{"line":10,"character":8}}}""";

    private const string ALink =
        """
        {"targetUri":"file:///repo/src/lib.rs",
         "targetRange":{"start":{"line":10,"character":0},"end":{"line":20,"character":1}},
         "targetSelectionRange":{"start":{"line":10,"character":4},"end":{"line":10,"character":8}}}
        """;

    private const string ALinkWithAnOrigin =
        """
        {"targetUri":"file:///repo/src/lib.rs",
         "targetRange":{"start":{"line":10,"character":0},"end":{"line":20,"character":1}},
         "targetSelectionRange":{"start":{"line":10,"character":4},"end":{"line":10,"character":8}},
         "originSelectionRange":{"start":{"line":3,"character":12},"end":{"line":3,"character":20}}}
        """;

    private const string AnotherLocation =
        """{"uri":"file:///repo/src/other.rs","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}}}""";

    private static Definition Read(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return Definition.Reader.Read(document.RootElement);
    }

    private static IReadOnlyList<DefinitionLocation> TargetsOf(string resultJson) =>
        Assert.IsType<Definition.Targets>(Read(resultJson)).Items;

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void No_definition_is_a_case_of_its_own_rather_than_an_empty_list(string resultJson)
    {
        Assert.IsType<Definition.None>(Read(resultJson));
    }

    [Fact]
    public void One_location_reads_as_one_target()
    {
        var target = Assert.Single(TargetsOf(ALocation));

        Assert.Equal("file:///repo/src/lib.rs", target.Uri.Value);
        Assert.Equal(new LspLine(10), target.Range.Start.Line);
        Assert.Equal(new LspCharacter(4), target.Range.Start.Character);
    }

    [Fact]
    public void An_array_of_locations_keeps_the_servers_order()
    {
        var targets = TargetsOf("[" + ALocation + "," + AnotherLocation + "]");

        Assert.Equal(2, targets.Count);
        Assert.Equal("file:///repo/src/lib.rs", targets[0].Uri.Value);
        Assert.Equal("file:///repo/src/other.rs", targets[1].Uri.Value);
    }

    [Fact]
    public void A_link_jumps_to_the_name_and_remembers_the_whole_declaration()
    {
        var target = Assert.Single(TargetsOf($"[{ALink}]"));

        Assert.Equal(new LspLine(10), target.Range.Start.Line);
        Assert.Equal(new LspCharacter(4), target.Range.Start.Character);
        Assert.Equal(new LspLine(20), target.EnclosingRange.End.Line);
    }

    [Fact]
    public void A_link_with_no_selection_range_falls_back_to_the_whole_declaration()
    {
        var target = Assert.Single(TargetsOf(
            """[{"targetUri":"file:///repo/src/lib.rs","targetRange":{"start":{"line":3,"character":0},"end":{"line":9,"character":1}}}]"""));

        Assert.Equal(new LspLine(3), target.Range.Start.Line);
        Assert.Equal(new LspLine(9), target.EnclosingRange.End.Line);
    }

    [Fact]
    public void An_array_mixing_both_shapes_reads_every_element()
    {
        var targets = TargetsOf($"[{ALocation},{ALink}]");

        Assert.Equal(2, targets.Count);
        Assert.Equal(new LspLine(10), targets[1].Range.Start.Line);
        Assert.Equal(new LspLine(20), targets[1].EnclosingRange.End.Line);
    }

    // The one range in the answer that is not about the destination: where the symbol the server
    // resolved sits in the file being read. It is what a link is drawn over, so losing it means
    // underlining the word the client guessed at instead of the one the server understood.
    [Fact]
    public void A_link_keeps_the_span_it_resolved_in_the_asking_file()
    {
        var target = Assert.Single(TargetsOf($"[{ALinkWithAnOrigin}]"));

        Assert.Equal(new LspLine(3), target.OriginRange!.Value.Start.Line);
        Assert.Equal(new LspCharacter(12), target.OriginRange.Value.Start.Character);
        Assert.Equal(new LspCharacter(20), target.OriginRange.Value.End.Character);
    }

    [Theory]
    [InlineData(ALocation)]
    [InlineData(ALink)]
    public void A_target_that_names_no_such_span_says_so_rather_than_inventing_one(string resultJson)
    {
        Assert.Null(Assert.Single(TargetsOf($"[{resultJson}]")).OriginRange);
    }

    [Fact]
    public void A_target_outside_the_repo_is_kept_whatever_its_scheme()
    {
        // Rust and Go jumps land in a package cache or a jar, which the tree cannot show. Dropping
        // these would silently turn a working jump into nothing happening.
        var target = Assert.Single(TargetsOf(
            """{"uri":"jdt://contents/java.base/java.lang/String.class","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}"""));

        Assert.StartsWith("jdt://", target.Uri.Value);
    }

    [Fact]
    public void A_percent_encoded_path_reads_back_as_the_path_it_encodes()
    {
        var target = Assert.Single(TargetsOf(
            """{"uri":"file:///repo/a%20b/%C3%A9.rs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}"""));

        Assert.Contains("a b", target.Uri.LocalPath);
        Assert.Contains("é.rs", target.Uri.LocalPath);
    }

    [Theory]
    [InlineData("""{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}""")]
    [InlineData("""{"uri":5,"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}""")]
    [InlineData("""{"uri":"src/lib.rs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}}""")]
    [InlineData("""{"uri":"file:///a.rs"}""")]
    [InlineData("""{"uri":"file:///a.rs","range":{"start":{"line":-3,"character":0},"end":{"line":0,"character":1}}}""")]
    [InlineData("""["file:///a.rs"]""")]
    [InlineData("42")]
    public void A_target_that_cannot_be_trusted_is_refused_rather_than_guessed_at(string resultJson)
    {
        Assert.Throws<LspParseException>(() => Read(resultJson));
    }

    [Fact]
    public void One_unreadable_target_refuses_the_whole_answer_rather_than_shifting_the_rest()
    {
        // Silently dropping element 0 turns "the second result" into "the first" — a jump to a real
        // file that is not the one the user picked.
        Assert.Throws<LspParseException>(() => Read($$"""[{"uri":"file:///a.rs"},{{ALocation}}]"""));
    }
}
