using System.Runtime.InteropServices;
using System.Text;
using GitBench.Features.Editor;
using GitBench.Features.FileBrowser;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>The save half of the write path: a file read, edited and written back is the same bytes it was.</summary>
public class DocumentWriterTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-docwriter-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ACrlfFileWithAMarkAndNoFinalNewlineComesBackByteIdentical() =>
        AssertRoundTrips("bom-crlf.txt", [.. Encoding.UTF8.GetPreamble(), .. Utf8("one\r\ntwo\r\nthree")]);

    [Fact]
    public void AFileWhoseLineEndingsDisagreeKeepsEveryOneOfThem() =>
        AssertRoundTrips("mixed.txt", Utf8("lf\ncrlf\r\ncr\rlast\n"));

    [Fact]
    public void AUtf16FileWithAMarkComesBackByteIdentical()
    {
        AssertRoundTrips(
            "utf16le.txt",
            [.. FileCharset.Utf16LeBom.Preamble(), .. Encoding.Unicode.GetBytes("héllo\r\nwörld\r\n")]);
        AssertRoundTrips(
            "utf16be.txt",
            [.. FileCharset.Utf16BeBom.Preamble(), .. Encoding.BigEndianUnicode.GetBytes("héllo\nwörld")]);
    }

    [Fact]
    public void AFileEndingInANewlineDoesNotLoseItAndOneWithoutDoesNotGainOne()
    {
        AssertRoundTrips("terminated.txt", Utf8("a\nb\n"));
        AssertRoundTrips("unterminated.txt", Utf8("a\nb"));
    }

    [Fact]
    public void AnAccentedCp1252FileIsRefusedRatherThanSavedAsReplacementCharacters()
    {
        var path = Write("cp1252.txt", [0x63, 0x61, 0x66, 0xE9, 0x0A]);

        var preview = Load(path);

        Assert.Equal(
            WriteBackRefusal.Lossy,
            Assert.IsType<FileWriteBack.Refused>(preview.WriteBack).Reason);
        Assert.IsNotType<FileWriteBack.Reversible>(preview.WriteBack);
    }

    [Fact]
    public void ATruncatedFileHasNoEncodingToBeSavedThrough()
    {
        var line = new string('x', 99) + "\n";
        var path = Path.Combine(_dir.Path, "long.txt");
        File.WriteAllText(
            path,
            string.Concat(Enumerable.Repeat(line, FileContentLoader.MaxTextBytes / line.Length + 100)));

        var preview = Load(path);

        Assert.True(preview.Truncated);
        Assert.Equal(
            WriteBackRefusal.Truncated,
            Assert.IsType<FileWriteBack.Refused>(preview.WriteBack).Reason);
        Assert.IsNotType<FileWriteBack.Reversible>(preview.WriteBack);
    }

    [Fact]
    public void TheFinalNewlineIsTheDocumentsRatherThanTheOneTheFileWasFoundWith()
    {
        var path = Write("trailing.txt", Utf8("a\nb\n"));
        var preview = Load(path);
        var encoding = Reversible(preview);
        var document = TextDocument.FromText(preview.Lines.Text);

        document.Apply(new TextEdit(
            new TextRange(TextPosition.At(2, 1), document.End), string.Empty));

        Assert.True(encoding.EndsWithNewline);
        Assert.False(document.EndsWithNewline);
        Assert.Equal(DocumentSave.Ok, DocumentWriter.Write(path, DocumentWriter.Serialize(document, encoding)));
        Assert.Equal(Utf8("a\nb"), File.ReadAllBytes(path));
    }

    [Fact]
    public void ANewlineTypedIntoACrlfFileIsWrittenAsOne()
    {
        var path = Write("typed.txt", Utf8("one\r\ntwo\r\n"));
        var preview = Load(path);
        var encoding = Reversible(preview);
        var document = TextDocument.FromText(preview.Lines.Text);
        var options = EditOptions.For(path, encoding.LineEnding, preview.Lines);

        var session = new EditSession(document, options);
        session.InsertNewline(SelectionRange.At(TextPosition.At(1, 3)));

        Assert.Equal(DocumentSave.Ok, DocumentWriter.Write(path, DocumentWriter.Serialize(document, encoding)));
        Assert.Equal(Utf8("one\r\n\r\ntwo\r\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void AWriteOverAFileThatCannotBeReplacedIsReportedRatherThanThrown()
    {
        var path = Write("locked.txt", Utf8("a\n"));
        var encoding = Reversible(Load(path));
        var bytes = DocumentWriter.Serialize(TextDocument.FromText("b\n"), encoding);

        File.Delete(path);
        Directory.CreateDirectory(path);

        var failed = Assert.IsType<DocumentSave.Failed>(DocumentWriter.Write(path, bytes));

        Assert.NotEmpty(failed.Message);
        Assert.Empty(Directory.GetFiles(_dir.Path).Where(AtomicFile.IsStagingPath));
    }

    [Fact]
    public void AnExecutableFileIsStillExecutableAfterBeingSaved()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var path = Write("run.sh", Utf8("#!/bin/sh\necho hi\n"));
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
        var encoding = Reversible(Load(path));

        DocumentWriter.Write(path, DocumentWriter.Serialize(TextDocument.FromText("#!/bin/sh\necho bye\n"), encoding));

        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }

    private void AssertRoundTrips(string name, byte[] contents)
    {
        var path = Write(name, contents);
        var preview = Load(path);
        var encoding = Reversible(preview);

        var document = TextDocument.FromText(preview.Lines.Text);
        Assert.Equal(DocumentSave.Ok, DocumentWriter.Write(path, DocumentWriter.Serialize(document, encoding)));

        Assert.Equal(contents, File.ReadAllBytes(path));
    }

    private string Write(string name, byte[] contents)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static FilePreview.Text Load(string path) => Assert.IsType<FilePreview.Text>(
        FileContentLoader.Load(path, new UnparsedFiles(), new PlainText(), CancellationToken.None));

    private static FileEncoding Reversible(FilePreview.Text preview) =>
        Assert.IsType<FileWriteBack.Reversible>(preview.WriteBack).Encoding;

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
