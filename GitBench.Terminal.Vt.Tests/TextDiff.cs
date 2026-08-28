using System.Text;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// A line-oriented difference report for the snapshot format.
/// </summary>
/// <remarks>
/// xunit's string comparison reports a character offset into a multi-kilobyte string, which for a
/// screen dump tells you nothing. What a person needs is the line number, the two lines stacked so
/// their columns align, and a mark under the column that moved.
/// </remarks>
public static class TextDiff
{
    const int MaxDifferences = 8;

    public static string? Describe(string expected, string actual, string expectedLabel, string actualLabel)
    {
        if (expected == actual)
            return null;

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var report = new StringBuilder();
        var shown = 0;

        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length) && shown < MaxDifferences; i++)
        {
            var expectedLine = i < expectedLines.Length ? expectedLines[i] : "<no such line>";
            var actualLine = i < actualLines.Length ? actualLines[i] : "<no such line>";
            if (expectedLine == actualLine)
                continue;

            shown++;
            report.AppendLine($"line {i + 1}:");
            report.AppendLine($"  {expectedLabel} |{expectedLine}");
            report.AppendLine($"  {actualLabel} |{actualLine}");
            report.AppendLine($"  {new string(' ', expectedLabel.Length)} {Caret(expectedLine, actualLine)}");
        }

        if (shown == MaxDifferences)
            report.AppendLine($"... only the first {MaxDifferences} differing lines are shown.");

        return report.ToString();
    }

    /// <summary>Points at the first column that differs, so a one-cell change is not a hunt.</summary>
    static string Caret(string expected, string actual)
    {
        var column = 0;
        while (column < expected.Length && column < actual.Length && expected[column] == actual[column])
            column++;

        return new string(' ', column + 1) + '^';
    }
}
