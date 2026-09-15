using System.Text;

using GitBench.Features.CodeIntel;

using TreeSitter.Bindings;

using Xunit;

namespace GitBench.Tests;

/// <summary>The safety net under the incremental parse: a tree that stops describing its buffer is
/// thrown away and the buffer parsed whole, rather than left quietly wrong forever.</summary>
[Collection(nameof(CodeIntelCollection))]
public sealed class MaintainedTreeTests(CodeIntelFixture fixture)
{
    private const string Source = "class A\n{\n    int x;\n}\n";

    [Fact]
    public void AFirstParseNeedsNoEdit()
    {
        using var tree = Track();

        var utf8 = Utf8(Source);
        var root = tree.Current(utf8).RootNode;

        Assert.Equal(0u, root.StartByte);
        Assert.Equal((uint)utf8.Length, root.EndByte);
        Assert.Equal(0, tree.Fallbacks);
    }

    [Fact]
    public void AnEditBeforeTheFirstParseIsSkippedRatherThanApplied()
    {
        using var tree = Track();

        // The buffer is already past the edit, so applying it to nothing would double-count it.
        // This is where an implementer applies the first edit twice.
        const string edited = "class B\n{\n    int x;\n}\n";
        tree.Advance(Utf8(edited), Replace(0, 7, 7));

        Assert.Equal((uint)Utf8(edited).Length, tree.Current(Utf8(edited)).RootNode.EndByte);
        Assert.Equal(0, tree.Fallbacks);
    }

    [Fact]
    public void AnEditThatContradictsTheBuffersItNamesIsRefusedAndTheBufferParsedWhole()
    {
        using var tree = Track();
        var utf8 = Utf8(Source);
        _ = tree.Current(utf8);

        // An end past the buffer it indexes: Reparse refuses it before the tree is touched, and
        // consumes the tree on its way out, so there is nothing left to be wrong.
        const string edited = "class A\n{\n    long x;\n}\n";
        tree.Advance(Utf8(edited), Replace(0, 9_000, 9_000));

        Assert.Equal(1, tree.Fallbacks);

        var recovered = tree.Current(Utf8(edited));
        Assert.Equal((uint)Utf8(edited).Length, recovered.RootNode.EndByte);
        Assert.Equal("long", TypeNameIn(recovered.RootNode));
    }

    /// <summary>
    /// The other half of the safety net, and the half that matters: an edit that breaks no rule at
    /// all, and is simply wrong about where the change was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing refuses this one — every bound is satisfied, so <c>Reparse</c> lets it through, and
    /// what comes back is a tree rather than an exception. It is the failure the whole design is
    /// afraid of, and the extent check is the only thing standing in front of it.
    /// </para>
    /// <para>
    /// The direction is the point. Claiming the edit began <em>earlier</em> than it did is harmless:
    /// more gets re-lexed than needed and the answer is still right. Claiming it began <em>later</em>
    /// under-invalidates, so subtrees that should have been thrown away are reused at offsets that no
    /// longer hold, and the root stops covering the buffer.
    /// </para>
    /// <para>
    /// Three bytes is grammar-specific rather than magic: it is the smallest drift that leaves the C#
    /// lexer reusing a token it should not have. If a grammar update stops it corrupting, this fails
    /// on the fallback count rather than passing quietly, and the drift can be re-derived by walking
    /// it upwards until <see cref="MaintainedTree.Fallbacks"/> moves.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEditThatIsMerelyWrongIsCaughtByTheExtentCheckAndTheBufferParsedWhole()
    {
        const string before =
            "namespace Demo;\n" +
            "\n" +
            "public class Greeter\n" +
            "{\n" +
            "    public string Greet(string name) => \"Hello\";\n" +
            "\n" +
            "    public string Farewell(string name) => \"Bye\";\n" +
            "}\n";

        var anchor = (uint)before.IndexOf("Greeter", StringComparison.Ordinal);
        var edited = before.Insert((int)anchor, "Polite");

        using var tree = Track();
        _ = tree.Current(Utf8(before));

        tree.Advance(Utf8(edited), Replace(anchor + 3, anchor + 3, anchor + 6));

        Assert.Equal(1, tree.Fallbacks);

        // Recovered from the buffer, which was authoritative all along.
        var recovered = tree.Current(Utf8(edited));
        Assert.Equal((uint)Utf8(edited).Length, recovered.RootNode.EndByte);
        Assert.Equal("PoliteGreeter", ClassNameIn(recovered.RootNode));
    }

    [Fact]
    public void DiscardingThrowsTheTreeAwayAndTheNextReadParsesWhatItIsGiven()
    {
        using var tree = Track();
        _ = tree.Current(Utf8(Source));

        tree.Discard();

        const string reloaded = "class Other\n{\n    string s;\n}\n";
        Assert.Equal("string", TypeNameIn(tree.Current(Utf8(reloaded)).RootNode));
        Assert.Equal(0, tree.Fallbacks);
    }

    private MaintainedTree Track() =>
        fixture.Grammars.Track(CodeLanguage.CSharp)
        ?? throw new InvalidOperationException("The C# grammar did not load.");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static TSInputEdit Replace(uint start, uint oldEnd, uint newEnd) => new()
    {
        StartByte = start,
        OldEndByte = oldEnd,
        NewEndByte = newEnd,
    };

    /// <summary>The type of the one field in the sample, read out of the tree rather than out of the
    /// text, so a tree describing a file nobody has says so.</summary>
    private static string TypeNameIn(TreeSitter.Node root)
    {
        foreach (var node in Descendants(root))
        {
            if (node.Type != "field_declaration" && node.Type != "variable_declaration") continue;
            if (node.ChildByFieldName("type") is { } type) return type.Text;
        }

        return string.Empty;
    }

    /// <summary>The name of the one class in the sample, read out of the tree rather than the text.</summary>
    private static string ClassNameIn(TreeSitter.Node root)
    {
        foreach (var node in Descendants(root))
        {
            if (node.Type != "class_declaration") continue;
            if (node.ChildByFieldName("name") is { } name) return name.Text;
        }

        return string.Empty;
    }

    private static IEnumerable<TreeSitter.Node> Descendants(TreeSitter.Node node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
