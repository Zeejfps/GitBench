using System.Text;
using GitBench.Features.Terminal;
using GitBench.Infrastructure;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// Loading a probe-harness recording: the bytes, and the geometry that makes them mean a screen.
/// </summary>
public class TerminalRecordingTests
{
    [Fact]
    public void ARecording_CarriesTheSizeItsInventoryRecorded()
    {
        using var files = new RecordingFiles(
            bytes: "hello"u8.ToArray(),
            inventory: """
                == capability inventory ==
                program:  C:\somewhere\claude.exe
                terminal: 120x34, TERM=xterm-256color COLORTERM=truecolor
                captured: 5 bytes, 1 token
                """);

        var recording = TerminalRecording.Load(files.BinPath);

        Assert.Equal("hello"u8.ToArray(), recording.Bytes);
        Assert.Equal(new TerminalSize(120, 34), recording.Size);
    }

    [Fact]
    public void AnInventoryWithoutAGeometry_IsRefusedRatherThanGuessedAt()
    {
        using var files = new RecordingFiles("x"u8.ToArray(), "== capability inventory ==");

        Assert.Throws<InvalidDataException>(() => TerminalRecording.Load(files.BinPath));
    }

    [Fact]
    public void ARecordingWithNoInventoryBesideIt_IsRefused()
    {
        using var files = new RecordingFiles("x"u8.ToArray(), inventory: null);

        Assert.Throws<FileNotFoundException>(() => TerminalRecording.Load(files.BinPath));
    }

    [Fact]
    public void AReplay_KeepsItsRecordedSizeWhateverThePaneCanShow()
    {
        var launch = new ReplayLaunch(
            new TerminalRecording([], new TerminalSize(120, 34)),
            new XtermSharpEngineFactory());

        Assert.Equal(new TerminalSize(120, 34), launch.SizeFor(new TerminalSize(200, 60)));
        Assert.Equal(new TerminalSize(120, 34), launch.SizeFor(new TerminalSize(40, 10)));
    }

    private sealed class RecordingFiles : IDisposable
    {
        private readonly string _directory;

        public RecordingFiles(byte[] bytes, string? inventory)
        {
            _directory = Path.Combine(Path.GetTempPath(), "gitbench-recording-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_directory);

            BinPath = Path.Combine(_directory, "session.bin");
            File.WriteAllBytes(BinPath, bytes);

            if (inventory is not null)
                File.WriteAllText(Path.Combine(_directory, "session.inventory.txt"), inventory, Encoding.UTF8);
        }

        public string BinPath { get; }

        public void Dispose() => DirectoryTree.Delete(_directory);
    }
}
