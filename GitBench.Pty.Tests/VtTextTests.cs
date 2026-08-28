using System.Text;

namespace GitBench.Pty.Tests;

/// <summary>
/// The decoder every terminal assertion rests on. If it dropped the wrong bytes the suite would
/// assert on nothing, so it is pinned in its own right.
/// </summary>
public class VtTextTests
{
    [Fact]
    public void Decode_DropsControlSequences_AndKeepsThePrintedText()
    {
        var stream = Bytes("\u001b[2J\u001b[H\u001b[38;2;255;0;0mred\u001b[0m");

        Assert.Equal("red", VtText.Decode(stream));
    }

    [Fact]
    public void Decode_DropsOperatingSystemCommands_TerminatedByBellOrStringTerminator()
    {
        var withBell = Bytes("\u001b]0;C:\\Windows\\system32\\cmd.exe\u0007after");
        var withTerminator = Bytes("\u001b]0;title\u001b\\after");

        Assert.Equal("after", VtText.Decode(withBell));
        Assert.Equal("after", VtText.Decode(withTerminator));
    }

    [Fact]
    public void Decode_KeepsMultiByteText()
    {
        var stream = Bytes("\u001b[mnaïve — ok");

        Assert.Equal("naïve — ok", VtText.Decode(stream));
    }

    [Fact]
    public void Contains_MatchesTextThatTheTerminalWrapped()
    {
        var stream = Bytes("[cols=\u001b[K100;ro\r\nws=30]");

        Assert.True(VtText.Contains(VtText.Decode(stream), "[cols=100;rows=30]"));
    }

    [Fact]
    public void Contains_DoesNotMatchTextTheChildNeverPrinted()
    {
        var stream = Bytes("[argv=plain|two|words]");

        Assert.False(VtText.Contains(VtText.Decode(stream), "[argv=plain|two words]"));
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
