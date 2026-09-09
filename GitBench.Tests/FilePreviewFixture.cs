using GitBench.Features.FileBrowser;
using GitBench.Infrastructure;

namespace GitBench.Tests;

/// <summary>Stand-in values for the fixtures that build a <see cref="FilePreview.Text"/> to exercise something else.</summary>
internal static class FilePreviewFixture
{
    public static FileWriteBack Reversible { get; } = new FileWriteBack.Reversible(
        new FileEncoding(FileCharset.Utf8, LineEnding.Lf, EndsWithNewline: true));

    public static FileText Of(IReadOnlyList<string> lines, bool endsWithNewline = true) =>
        new(lines.Count == 0
            ? string.Empty
            : string.Join('\n', lines) + (endsWithNewline ? "\n" : string.Empty));
}
