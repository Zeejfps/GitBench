using System.Text;
using GitBench.Features.Terminal;
using GitBench.Terminal.Vt;
using Xunit;

namespace GitBench.Tests.Terminal;

public class TerminalMouseEncoderTests
{
    const string Csi = "[";

    [Fact]
    public void ASgrPress_NamesTheButtonAndTheOneBasedCell()
    {
        Assert.Equal(
            Csi + "<0;1;1M",
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, 0, 0, Sgr));
    }

    [Fact]
    public void ASgrRelease_KeepsTheButtonAndEndsInLowercase()
    {
        Assert.Equal(
            Csi + "<2;4;9m",
            Encoded(TerminalMouseButton.Right, TerminalMouseAction.Release, 3, 8, Sgr));
    }

    [Fact]
    public void TheWheel_IsButtonSixtyFourAndSixtyFive()
    {
        Assert.Equal(
            Csi + "<64;11;5M",
            Encoded(TerminalMouseButton.WheelUp, TerminalMouseAction.Press, 10, 4, Sgr));
        Assert.Equal(
            Csi + "<65;11;5M",
            Encoded(TerminalMouseButton.WheelDown, TerminalMouseAction.Press, 10, 4, Sgr));
    }

    [Fact]
    public void TheWheel_IsNeverReleased()
    {
        Assert.Null(
            Encoded(TerminalMouseButton.WheelUp, TerminalMouseAction.Release, 0, 0, Sgr));
    }

    [Fact]
    public void HeldModifiers_AreTheXtermBits()
    {
        Assert.Equal(
            Csi + "<20;1;1M",
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Press,
                0,
                0,
                Sgr,
                TerminalKeyModifiers.Shift | TerminalKeyModifiers.Ctrl));
    }

    [Fact]
    public void AMotion_AddsThirtyTwoToTheButtonItIsDraggingWith()
    {
        Assert.Equal(
            Csi + "<32;3;4M",
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Move,
                2,
                3,
                Sgr with { MouseTracking = MouseTracking.ButtonEvent }));
    }

    [Fact]
    public void AMotionWithNothingHeld_IsButtonThreePlusThirtyTwo()
    {
        Assert.Equal(
            Csi + "<35;1;1M",
            Encoded(
                TerminalMouseButton.None,
                TerminalMouseAction.Move,
                0,
                0,
                Sgr with { MouseTracking = MouseTracking.AnyEvent }));
    }

    [Fact]
    public void TheLegacyEncoding_IsThreeOffsetBytesAfterCsiM()
    {
        Assert.Equal(
            Csi + "M !!",
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, 0, 0, Legacy));
    }

    [Fact]
    public void TheLegacyEncoding_CannotSayWhichButtonWasReleased()
    {
        Assert.Equal(
            Csi + "M#!!",
            Encoded(TerminalMouseButton.Right, TerminalMouseAction.Release, 0, 0, Legacy));
    }

    [Fact]
    public void TheLegacyEncoding_ReachesItsLastCellAtTwoHundredAndTwentyTwo()
    {
        Assert.NotNull(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, 222, 0, Legacy));
        Assert.Null(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, 223, 0, Legacy));
    }

    [Fact]
    public void TheUtf8Encoding_SpendsTwoBytesOnACoordinateAByteCannotHold()
    {
        Assert.Equal(
            Csi + "M \u00c2\u0085!",
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Press,
                100,
                0,
                Legacy with { MouseEncoding = MouseEncoding.Utf8 }));
    }

    [Fact]
    public void TheUrxvtEncoding_IsTheOffsetButtonAndTwoNumbers()
    {
        Assert.Equal(
            Csi + "32;1;1M",
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Press,
                0,
                0,
                Legacy with { MouseEncoding = MouseEncoding.Urxvt }));
    }

    [Fact]
    public void AProgramTrackingNothing_IsToldNothing()
    {
        Assert.Null(
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Press,
                0,
                0,
                Sgr with { MouseTracking = MouseTracking.Off }));
    }

    [Theory]
    [InlineData(TerminalMouseButton.Left, TerminalMouseAction.Release)]
    [InlineData(TerminalMouseButton.Left, TerminalMouseAction.Move)]
    [InlineData(TerminalMouseButton.WheelUp, TerminalMouseAction.Press)]
    public void X10Tracking_ReportsPressesAndNothingElse(
        TerminalMouseButton button,
        TerminalMouseAction action)
    {
        var x10 = Sgr with { MouseTracking = MouseTracking.X10 };

        Assert.NotNull(Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, 0, 0, x10));
        Assert.Null(Encoded(button, action, 0, 0, x10));
    }

    [Fact]
    public void X10Tracking_LeavesTheModifiersOut()
    {
        Assert.Equal(
            Csi + "<0;1;1M",
            Encoded(
                TerminalMouseButton.Left,
                TerminalMouseAction.Press,
                0,
                0,
                Sgr with { MouseTracking = MouseTracking.X10 },
                TerminalKeyModifiers.Ctrl));
    }

    [Fact]
    public void NormalTracking_ReportsTheClickButNotThePointer()
    {
        Assert.NotNull(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Release, 0, 0, Sgr));
        Assert.Null(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Move, 0, 0, Sgr));
    }

    [Fact]
    public void ButtonEventTracking_ReportsThePointerOnlyWhileAButtonIsDown()
    {
        var dragging = Sgr with { MouseTracking = MouseTracking.ButtonEvent };

        Assert.NotNull(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Move, 0, 0, dragging));
        Assert.Null(
            Encoded(TerminalMouseButton.None, TerminalMouseAction.Move, 0, 0, dragging));
    }

    [Fact]
    public void ACellOffTheScreen_IsNotReported()
    {
        Assert.Null(
            Encoded(TerminalMouseButton.Left, TerminalMouseAction.Press, -1, 0, Sgr));
    }

    static readonly TerminalModes Sgr = new(
        ApplicationCursorKeys: false,
        ApplicationKeypad: false,
        AutoWrap: true,
        AlternateScreen: false,
        AlternateScroll: true,
        BracketedPaste: false,
        FocusReporting: false,
        SynchronizedOutput: false,
        MouseTracking: MouseTracking.Normal,
        MouseEncoding: MouseEncoding.Sgr,
        KeyboardProtocolFlags: 0,
        ModifyOtherKeys: 0);

    static readonly TerminalModes Legacy = Sgr with { MouseEncoding = MouseEncoding.X10 };

    static string? Encoded(
        TerminalMouseButton button,
        TerminalMouseAction action,
        int column,
        int row,
        TerminalModes modes,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None)
    {
        Span<byte> report = stackalloc byte[TerminalMouseEncoder.MaxEncodedBytes];
        return TerminalMouseEncoder.Encode(
            button,
            action,
            column,
            row,
            modifiers,
            modes,
            report,
            out var written)
            ? Encoding.Latin1.GetString(report[..written])
            : null;
    }
}
