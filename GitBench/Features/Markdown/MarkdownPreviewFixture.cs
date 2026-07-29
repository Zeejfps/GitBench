namespace GitBench.Features.Markdown;

/// <summary>
/// The dev-preview fixture document: one markdown source exercising every construct the renderer
/// claims (docs/plans/markdown-renderer.md, Scope) — the full heading ladder, every inline style
/// including a hard break, plain/nested/ordered/task lists, nested quotes with inner constructs,
/// a thematic break, closed (highlighted), unknown-language and unterminated fences, and two
/// tables (one per alignment kind, one wide enough to force the horizontal-scroll fallback).
/// <see cref="MarkdownPreviewWidget"/> renders it through the streaming path;
/// <c>MarkdownPreviewTests</c> pins that it stays parseable and wired. A const rather than an
/// embedded resource: no csproj resource plumbing, and the text is versioned right next to the
/// widget that shows it.
/// </summary>
internal static class MarkdownPreviewFixture
{
    /// <summary>Two trailing spaces form the hard break; spelled as an interpolation hole so the
    /// break survives editors that trim trailing whitespace.</summary>
    private const string HardBreakSpaces = "  ";

    public const string Text = $$"""
        # Markdown preview (H1)

        ## Inline styles (H2)

        ### Heading level three (H3)

        #### Heading level four (H4)

        ##### Heading level five (H5)

        ###### Heading level six (H6)

        A paragraph with **bold**, *italic*, ***bold italic***, `inline code`,
        ~~strikethrough~~, a [styled link](https://example.com/docs), and an
        autolink https://example.com/api/v1.

        This line ends in a hard break:{{HardBreakSpaces}}
        so this text starts a new line inside the same paragraph.

        ## Lists

        - unordered alpha
        - unordered beta
          - nested child one
          - nested child two
        - unordered gamma

        3. ordered honoring start
        4. ordered continues
          1. nested ordered one
          2. nested ordered two
        5. ordered ends

        - [ ] task still open
        - [x] task already done

        ## Blockquotes

        > Outer quote with **bold**, `code`, and a [link](https://example.com/quote).
        >
        > > Nested quote with *italic* text.
        > >
        > > - a list inside the nested quote
        >
        > Back at the outer quote level.

        ---

        ## Code blocks

        ```csharp
        public sealed record Fixture(string Name, int Count)
        {
            public override string ToString() => $"{Name} x{Count}";
        }
        ```

        ```mystery-lang
        no grammar answers to this fence -> plain mono text, no highlighting
        ```

        ## Tables

        | Left aligned | Centered | Right aligned |
        |:-------------|:--------:|--------------:|
        | alpha        | beta     | gamma         |
        | *italic*     | `code`   | **bold**      |

        | Commit | URL | Token | Digest | Trailer |
        |---|---|---|---|---|
        | 4f83a2d9c1b07e6a5d3f8c2b9a1e0d7c6b5a49380a1b2c3d4e5f60718293a4b5 | https://example.com/some/very/long/path/to/a/resource | c0ffee0ddf00dfacefeedbeefdeadc0dedecafbad0123456789abcdef0123456 | 9e8d7c6b5a493827160f5e4d3c2b1a09182736450f1e2d3c4b5a69788796a5b4 | thefinalwidecellkeepsgoingwithoutanybreakopportunity |

        ## Streaming tail

        The fence below never closes — the in-progress shape a streamed reply pauses in.

        ```json
        {
          "status": "still streaming",
          "note": "unterminated on purpose"
        """;
}
