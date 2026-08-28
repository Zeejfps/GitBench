using System.Text;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// Compares a rendered snapshot against a committed golden.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no "update the goldens" switch. A golden here states what a correct
/// terminal shows, worked out from the recorded bytes; a switch that rewrites it from whatever the
/// engine printed would turn every bug into the specification the moment someone ran the suite
/// with the flag on. When a golden is wrong, a person edits it and says why in the commit.
/// </para>
/// <para>
/// What the suite does write is <c>.actual</c> beside the golden on failure, so the two can be
/// diffed with an ordinary diff tool. Those files are build output and belong in .gitignore.
/// </para>
/// </remarks>
public static class GoldenFile
{
    public static string Directory => Path.Combine(TestPaths.SourceDirectory, "Goldens");

    public static void Matches(string name, string actual)
    {
        var path = Path.Combine(Directory, name);
        var actualPath = path + ".actual";

        System.IO.Directory.CreateDirectory(Directory);

        if (!File.Exists(path))
        {
            File.WriteAllText(actualPath, actual);
            Assert.Fail(
                $"No golden at {path}.\n"
                + $"What the engine produced is at {actualPath}. Read it against the corpus, correct anything the "
                + "engine got wrong, and commit the corrected file as the golden — do not copy it across unread.");
        }

        var expected = Normalise(File.ReadAllText(path));
        var normalisedActual = Normalise(actual);

        if (expected == normalisedActual)
        {
            if (File.Exists(actualPath))
                File.Delete(actualPath);
            return;
        }

        File.WriteAllText(actualPath, actual);
        Assert.Fail(Describe(path, actualPath, expected, normalisedActual));
    }

    /// <summary>
    /// Line endings squared up, and provenance lines dropped. A line beginning with ';' is a note
    /// from whoever authored the golden — where it came from, what was checked against the bytes,
    /// what is known to be wrong — and is not part of the screen. A golden whose provenance nobody
    /// can see is a golden nobody can trust, and there is nowhere else to put it.
    /// </summary>
    static string Normalise(string text) => string.Join(
        '\n',
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Where(line => !line.StartsWith(';')));

    static string Describe(string goldenPath, string actualPath, string expected, string actual) =>
        $"""
         The screen does not match the golden {Path.GetFileName(goldenPath)}.
           golden: {goldenPath}
           actual: {actualPath}

         {TextDiff.Describe(expected, actual, "want", "got ")}
         """;
}
