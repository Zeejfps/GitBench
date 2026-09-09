using System.Text;
using GitBench.Features.FileBrowser;
using GitBench.Infrastructure;
using Xunit;

namespace GitBench.Tests;

/// <summary>What a load has to record for a file to be written back as it was found, and the two cases where it cannot be.</summary>
public class FileWriteBackTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-writeback-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AFileRemembersHowItBrokeItsLinesAndWhetherItEndedWithOne()
    {
        AssertRemembers("a\nb\n", LineEnding.Lf, endsWithNewline: true);
        AssertRemembers("a\r\nb\r\n", LineEnding.CrLf, endsWithNewline: true);
        AssertRemembers("a\rb\r", LineEnding.Cr, endsWithNewline: true);
        AssertRemembers("a\nb", LineEnding.Lf, endsWithNewline: false);
        AssertRemembers("a\r\nb", LineEnding.CrLf, endsWithNewline: false);
    }

    private static void AssertRemembers(string content, LineEnding ending, bool endsWithNewline)
    {
        var encoding = Reversible(Utf8(content));

        Assert.Equal(FileCharset.Utf8, encoding.Charset);
        Assert.Equal(ending, encoding.LineEnding);
        Assert.Equal(endsWithNewline, encoding.EndsWithNewline);
    }

    [Fact]
    public void AFileEndingInANewlineIsDistinguishableFromOneThatDoesNot()
    {
        Assert.True(Reversible(Utf8("a\n")).EndsWithNewline);
        Assert.False(Reversible(Utf8("a")).EndsWithNewline);
    }

    [Fact]
    public void TheDominantEndingWinsInAMixedFile() =>
        Assert.Equal(LineEnding.CrLf, Reversible(Utf8("a\r\nb\r\nc\n")).LineEnding);

    [Fact]
    public void AnEmptyFileRoundTripsAndHasNoEndingToRemember()
    {
        var encoding = Reversible([]);

        Assert.Equal(LineEnding.Lf, encoding.LineEnding);
        Assert.False(encoding.EndsWithNewline);
    }

    [Fact]
    public void AFileThatIsOnlyANewlineIsOneEmptyLineThatRoundTrips()
    {
        var encoding = Reversible(Utf8("\n"));

        Assert.True(encoding.EndsWithNewline);
        Assert.Equal([""], TextLines.Split(FileTextDecoder.DecodeText(Utf8("\n"))));
    }

    [Fact]
    public void AByteOrderMarkIsRememberedRatherThanQuietlyDropped()
    {
        var bytes = Bytes(Encoding.UTF8.GetPreamble(), Utf8("hello\n"));

        Assert.Equal(FileCharset.Utf8Bom, Reversible(bytes).Charset);
        Assert.Equal("hello\n", FileTextDecoder.DecodeText(bytes));
    }

    [Fact]
    public void AUtf16FileRoundTripsThroughTheEncodingItsMarkNamed()
    {
        AssertUtf16RoundTrips(FileCharset.Utf16LeBom);
        AssertUtf16RoundTrips(FileCharset.Utf16BeBom);
    }

    private static void AssertUtf16RoundTrips(FileCharset charset)
    {
        var bytes = Bytes(charset.Preamble().ToArray(), charset.Encoding().GetBytes("héllo\r\n"));

        var encoding = Reversible(bytes);

        Assert.Equal(charset, encoding.Charset);
        Assert.Equal(LineEnding.CrLf, encoding.LineEnding);
        Assert.Equal("héllo\r\n", FileTextDecoder.DecodeText(bytes));
    }

    [Fact]
    public void AUtf32MarkIsNotReadAsTheUtf16MarkItStartsWith()
    {
        var bytes = Bytes(
            FileCharset.Utf32LeBom.Preamble().ToArray(),
            Encoding.UTF32.GetBytes("héllo\r\n"));

        Assert.Equal(FileCharset.Utf32LeBom, FileTextDecoder.CharsetOf(bytes));
        Assert.Equal("héllo\r\n", FileTextDecoder.DecodeText(bytes));
        Assert.Equal(FileCharset.Utf32LeBom, Reversible(bytes).Charset);
    }

    [Fact]
    public void AUtf32FileOffDiskIsTextAndRoundTrips()
    {
        var bytes = Bytes(
            FileCharset.Utf32BeBom.Preamble().ToArray(),
            new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetBytes("héllo\nwörld\n"));
        var path = Path.Combine(_dir.Path, "utf32.txt");
        File.WriteAllBytes(path, bytes);

        var text = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        Assert.Equal(["héllo", "wörld"], text.Lines);
        Assert.Equal(FileCharset.Utf32BeBom, Assert.IsType<FileWriteBack.Reversible>(text.WriteBack).Encoding.Charset);
    }

    [Fact]
    public void ACp1252FileIsRefusedRatherThanReadAsUtf8()
    {
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9, 0x0A];

        var decoded = FileTextDecoder.Decode(bytes, truncated: false);

        Assert.Equal("caf�\n", decoded.Text);
        Assert.Equal(
            WriteBackRefusal.Lossy,
            Assert.IsType<FileWriteBack.Refused>(decoded.WriteBack).Reason);
    }

    [Fact]
    public void ALossyFileIsStillShownAndStillRefusedWhenItComesOffDisk()
    {
        var path = Path.Combine(_dir.Path, "notes.txt");
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9, 0x0A]);

        var text = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        Assert.Equal(["caf�"], text.Lines);
        Assert.False(text.Truncated);
        Assert.Equal(
            WriteBackRefusal.Lossy,
            Assert.IsType<FileWriteBack.Refused>(text.WriteBack).Reason);
    }

    [Fact]
    public void AUtf8FileOffDiskCarriesTheEncodingASaveWouldNeed()
    {
        var path = Path.Combine(_dir.Path, "sample.txt");
        File.WriteAllBytes(path, Utf8("héllo\r\nwörld\r\n"));

        var text = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        var encoding = Assert.IsType<FileWriteBack.Reversible>(text.WriteBack).Encoding;
        Assert.Equal(FileCharset.Utf8, encoding.Charset);
        Assert.Equal(LineEnding.CrLf, encoding.LineEnding);
        Assert.True(encoding.EndsWithNewline);
    }

    [Fact]
    public void ATruncatedReadIsRefusedWithoutBeingAskedAboutItsBytes()
    {
        var line = new string('x', 99) + "\n";
        var path = Path.Combine(_dir.Path, "long.txt");
        File.WriteAllText(
            path, string.Concat(Enumerable.Repeat(line, FileContentLoader.MaxTextBytes / line.Length + 100)));

        var text = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        Assert.True(text.Truncated);
        Assert.Equal(
            WriteBackRefusal.Truncated,
            Assert.IsType<FileWriteBack.Refused>(text.WriteBack).Reason);
    }

    private static FileEncoding Reversible(byte[] bytes) =>
        Assert.IsType<FileWriteBack.Reversible>(
            FileTextDecoder.Decode(bytes, truncated: false).WriteBack).Encoding;

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static byte[] Bytes(byte[] first, byte[] second) => [.. first, .. second];

    [Fact]
    public void AUtf16FileOffDiskIsTextRatherThanBinary()
    {
        var path = Path.Combine(_dir.Path, "utf16.txt");
        File.WriteAllBytes(path, [.. FileCharset.Utf16LeBom.Preamble(), .. Encoding.Unicode.GetBytes("héllo\r\nwörld\r\n")]);

        var text = Assert.IsType<FilePreview.Text>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        Assert.Equal(["héllo", "wörld"], text.Lines);
        var encoding = Assert.IsType<FileWriteBack.Reversible>(text.WriteBack).Encoding;
        Assert.Equal(FileCharset.Utf16LeBom, encoding.Charset);
        Assert.Equal(LineEnding.CrLf, encoding.LineEnding);
    }

    [Fact]
    public void AFileOfNulBytesWithNoMarkIsStillBinary()
    {
        var path = Path.Combine(_dir.Path, "blob.bin");
        File.WriteAllBytes(path, [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x00, 0x01, 0x02]);

        var refused = Assert.IsType<FilePreview.Unavailable>(
            FileContentLoader.Load(path, new UnparsedFiles(), CancellationToken.None));

        Assert.Equal(FilePreviewRefusal.Binary, refused.Reason);
    }
}
