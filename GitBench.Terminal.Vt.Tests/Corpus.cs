using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GitBench.Terminal.Vt.Tests;

/// <summary>
/// A recorded pseudo-terminal session: the bytes, and the terminal geometry they were recorded at.
/// </summary>
/// <remarks>
/// The size is not decoration. A stream recorded at 120 columns replayed at 80 wraps in different
/// places and produces a different screen, so bytes alone do not determine a grid and a corpus that
/// stores only bytes is under-specified. It is parsed out of the inventory the probe wrote beside
/// the recording rather than kept in a table here, so the two cannot drift apart.
/// </remarks>
public sealed partial class Corpus
{
    Corpus(string name, byte[] bytes, TerminalSize size)
    {
        Name = name;
        Bytes = bytes;
        Size = size;
    }

    public string Name { get; }

    public byte[] Bytes { get; }

    public TerminalSize Size { get; }

    public override string ToString() => $"{Name} ({Bytes.Length} bytes at {Size})";

    public static IReadOnlyList<string> Names { get; } = ["claude", "vim", "less", "git-log", "smoke"];

    public static IEnumerable<object[]> All() => Names.Select(name => new object[] { name });

    public static Corpus Load(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Directory, $"{name}.bin"));
        var inventory = File.ReadAllText(Path.Combine(Directory, $"{name}.inventory.txt"));

        return new Corpus(name, bytes, ParseSize(name, inventory));
    }

    public static string Directory => Path.Combine(TestPaths.SourceDirectory, "Corpus");

    static TerminalSize ParseSize(string name, string inventory)
    {
        var match = TerminalLine().Match(inventory);
        if (!match.Success)
            throw new InvalidDataException(
                $"The inventory for '{name}' has no 'terminal: COLSxROWS' line, so the geometry the "
                + "corpus was recorded at is unknown and replaying it would be meaningless.");

        return new TerminalSize(int.Parse(match.Groups["cols"].Value), int.Parse(match.Groups["rows"].Value));
    }

    [GeneratedRegex(@"^terminal:\s*(?<cols>\d+)x(?<rows>\d+)", RegexOptions.Multiline)]
    private static partial Regex TerminalLine();
}

/// <summary>
/// Where the suite's data files live. Resolved from the compiler's view of this file rather than
/// from the output directory, because the goldens are source: an artifacts path can put the
/// assembly anywhere, and a golden written next to a binary is a golden nobody reviews.
/// </summary>
public static class TestPaths
{
    public static string SourceDirectory { get; } = Resolve();

    static string Resolve([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile) ?? throw new InvalidOperationException("Cannot locate the test source directory.");
}
