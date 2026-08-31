using GitBench.Features.CodeIntel;

using Xunit;

namespace GitBench.Tests;

[Collection(nameof(CodeIntelCollection))]
public class FileOutlineTests(CodeIntelFixture fixture)
{
    private const string Source =
        """
        namespace Acme;

        class Widget
        {
            void Resize(int width)
            {
                _ = width;
            }

            void Draw() { }
        }
        """;

    [Fact]
    public void EnclosingAtReturnsTheInnermostDeclaration()
    {
        var outline = fixture.Outline(Source);

        Assert.Equal("Resize", outline.EnclosingAt(7)?.Name);
        Assert.Equal("Resize", outline.EnclosingAt(5)?.Name);
        Assert.Equal("Draw", outline.EnclosingAt(10)?.Name);
    }

    [Fact]
    public void EnclosingAtReachesInsideAFileScopedNamespaceWhoseOwnSpanIsOneLine()
    {
        var outline = fixture.Outline(Source);
        var ns = Assert.Single(outline.Roots);

        Assert.Equal(1, ns.StartLine);
        Assert.Equal(1, ns.EndLine);
        Assert.Equal("Widget", outline.EnclosingAt(4)?.Name);
        Assert.Equal("Acme", outline.EnclosingAt(1)?.Name);
    }

    [Fact]
    public void EnclosingAtReturnsNullOutsideEveryDeclaration()
    {
        var outline = fixture.Outline(
            """
            using System;

            class Widget { }
            """);

        Assert.Null(outline.EnclosingAt(1));
        Assert.Null(outline.EnclosingAt(2));
        Assert.Equal("Widget", outline.EnclosingAt(3)?.Name);
    }

    [Fact]
    public void FlattenIsPreOrderOutermostFirst()
    {
        var outline = fixture.Outline(Source);

        Assert.Equal(["Acme", "Widget", "Resize", "Draw"], outline.Flatten().Select(n => n.Name));
    }

    [Fact]
    public void FlattenOfAnEmptyOutlineIsEmpty()
    {
        Assert.Empty(new FileOutline([]).Flatten());
        Assert.Null(new FileOutline([]).EnclosingAt(1));
    }

    [Fact]
    public void DetectRecognisesOnlyTheLanguagesWithABundledQuery()
    {
        Assert.Equal(CodeLanguage.CSharp, CodeLanguages.Detect("Widget.cs"));
        Assert.Equal(CodeLanguage.CSharp, CodeLanguages.Detect("src/Deep/Widget.CS"));
        Assert.Equal(CodeLanguage.TypeScript, CodeLanguages.Detect("Widget.ts"));
        Assert.Equal(CodeLanguage.Tsx, CodeLanguages.Detect("Widget.tsx"));
        Assert.Null(CodeLanguages.Detect("Widget.js"));
        Assert.Null(CodeLanguages.Detect("Widget.txt"));
        Assert.Null(CodeLanguages.Detect("Makefile"));
        Assert.Null(CodeLanguages.Detect(string.Empty));
    }
}
