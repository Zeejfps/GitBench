using System.Text;

using GitBench.Features.CodeIntel;

namespace GitBench.Tests;

internal static class CodeIntelSamples
{
    // A raw string literal carries whatever line endings git checked this file out with, so on a
    // core.autocrlf=true clone every literal below would be CRLF while Render writes LF. Both
    // literals are brought to the format's own newline here, where they enter the program, rather
    // than at the comparisons that would otherwise each have to know.
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    public static readonly string Sample = Lf(
        """
        using System;

        namespace Acme.Widgets;

        [Flags]
        public enum Corner
        {
            None = 0,
            Top = 1,
        }

        public delegate void Resized(int width, int height);

        public record Point(int X, int Y);

        public interface IWidget
        {
            int Width { get; }

            void Resize(int width);
        }

        [Obsolete("use Widget2")]
        public abstract partial class Widget : IWidget
        {
            private const string Separator = ", ";

            private int _width;

            public event Resized? Resized;

            public Widget()
            {
                _width = 0;
            }

            public int Width => _width;

            public string Label { get; set; } = "widget";

            public abstract void Resize(int width);

            public void Resize(int width, int height)
            {
                void Clamp(int value)
                {
                    _width = value;
                }

                Clamp(width + height);
            }

            public static bool operator ==(Widget? left, Widget? right) => ReferenceEquals(left, right);

            public static bool operator !=(Widget? left, Widget? right) => !(left == right);

            public static implicit operator string(Widget widget) => widget.Label;

            public struct Size
            {
                public int Value;
            }
        }
        """);

    /// <summary>
    /// The outline <see cref="Sample"/> is expected to produce, checked in so a tree-sitter grammar
    /// pin that moves shows up as a diff rather than as silently different navigation.
    /// </summary>
    public static readonly string ExpectedOutline = Lf(
        """
        Namespace Acme.Widgets [3-3] sig=3
          Enum Corner [6-10] sig=7
            EnumMember None [8-8] sig=8
            EnumMember Top [9-9] sig=9
          Type Resized(int, int) [12-12] sig=12
          Record Point [14-14] sig=14
          Interface IWidget [16-21] sig=17
            Property Width [18-18] sig=18
            Method Resize(int) [20-20] sig=20
          Class Widget [24-63] sig=25
            Field Separator [26-26] sig=26
            Field _width [28-28] sig=28
            Event Resized [30-30] sig=30
            Constructor Widget() [32-35] sig=33
            Property Width [37-37] sig=37
            Property Label [39-39] sig=39
            Method Resize(int) [41-41] sig=41
            Method Resize(int, int) [43-51] sig=44
              Function Clamp(int) [45-48] sig=46
            Method ==(Widget?, Widget?) [53-53] sig=53
            Method !=(Widget?, Widget?) [55-55] sig=55
            Method string(Widget) [57-57] sig=57
            Struct Size [59-62] sig=60
              Field Value [61-61] sig=61

        """);

    public static string Render(FileOutline outline)
    {
        var builder = new StringBuilder();
        Append(builder, outline.Roots, 0);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, IReadOnlyList<OutlineNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            builder.Append(' ', depth * 2)
                .Append(node.Kind)
                .Append(' ')
                .Append(node.Name);

            if (node.ParameterTypes is { } parameters)
            {
                builder.Append('(').Append(parameters).Append(')');
            }

            builder.Append(" [")
                .Append(node.StartLine)
                .Append('-')
                .Append(node.EndLine)
                .Append("] sig=")
                .Append(node.SignatureEndLine)
                .Append('\n');

            Append(builder, node.Children, depth + 1);
        }
    }
}
