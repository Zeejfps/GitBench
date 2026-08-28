using System.ComponentModel;
using System.Runtime.InteropServices;
using GitBench.Pty.Platforms.Windows;

namespace GitBench.Pty.Tests;

/// <summary>
/// Pins the argv -> CreateProcessW command line encoding. The explicit cases document the shape;
/// the round trip is the authority — every command line we emit must decode back to the argv it
/// came from under the same parser (CommandLineToArgvW) that the CRT startup code uses.
/// </summary>
public class WindowsCommandLineTests
{
    [Fact]
    public void QuotesTheExecutableSoASpacedPathCannotSplit()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Git\\bin\\git.exe\"",
            WindowsCommandLine.Build("C:\\Program Files\\Git\\bin\\git.exe", []));
    }

    [Fact]
    public void QuotesTheExecutableEvenWhenItNeedsNoQuoting()
    {
        Assert.Equal("\"cmd.exe\"", WindowsCommandLine.Build("cmd.exe", []));
    }

    [Fact]
    public void LeavesBackslashesInTheExecutableAlone()
    {
        Assert.Equal("\"C:\\dir\\\"", WindowsCommandLine.Build("C:\\dir\\", []));
    }

    [Fact]
    public void SeparatesArgumentsWithASingleSpace()
    {
        Assert.Equal(
            "\"cmd.exe\" /c echo \"hello world\"",
            WindowsCommandLine.Build("cmd.exe", ["/c", "echo", "hello world"]));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("   ", "\"   \"")]
    [InlineData("has\ttab", "\"has\ttab\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("\"", "\"\\\"\"")]
    [InlineData("c:\\path\\", "c:\\path\\")]
    [InlineData("\\", "\\")]
    [InlineData("\\\\", "\\\\")]
    [InlineData("c:\\path with space\\", "\"c:\\path with space\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("\\\\\"", "\"\\\\\\\\\\\"\"")]
    [InlineData("\\\\ \\\\", "\"\\\\ \\\\\\\\\"")]
    public void EncodesOneArgument(string argument, string expected)
    {
        Assert.Equal("\"x.exe\" " + expected, WindowsCommandLine.Build("x.exe", [argument]));
    }

    [Fact]
    public void RejectsAnEmptyExecutable()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("", []));
    }

    [Fact]
    public void RejectsAnExecutableContainingADoubleQuote()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("c:\\a\"b\\x.exe", []));
    }

    [Fact]
    public void RejectsAnExecutableContainingANullCharacter()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("c:\\x\0.exe", []));
    }

    [Fact]
    public void RejectsAnArgumentContainingANullCharacter()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("x.exe", ["a\0b"]));
    }

    [Fact]
    public void RejectsANullArgument()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("x.exe", [null!]));
    }

    [WindowsOnlyTheory]
    [InlineData("cmd.exe")]
    [InlineData("cmd.exe", "plain")]
    [InlineData("cmd.exe", "")]
    [InlineData("cmd.exe", "", "")]
    [InlineData("cmd.exe", "", "after empty")]
    [InlineData("cmd.exe", "one", "two", "three")]
    [InlineData("cmd.exe", "with space")]
    [InlineData("cmd.exe", "   ")]
    [InlineData("cmd.exe", "with\ttab")]
    [InlineData("cmd.exe", "with\nnewline")]
    [InlineData("cmd.exe", "\"")]
    [InlineData("cmd.exe", "a\"b")]
    [InlineData("cmd.exe", "\"quoted\"")]
    [InlineData("cmd.exe", "\"has space\"")]
    [InlineData("cmd.exe", "\\")]
    [InlineData("cmd.exe", "\\\\")]
    [InlineData("cmd.exe", "\\\\\\")]
    [InlineData("cmd.exe", "\\\"")]
    [InlineData("cmd.exe", "\\\\\"")]
    [InlineData("cmd.exe", "\\\\\\\"")]
    [InlineData("cmd.exe", "c:\\path\\")]
    [InlineData("cmd.exe", "c:\\path with space\\")]
    [InlineData("cmd.exe", "\\\\server\\share\\a b\\")]
    [InlineData("cmd.exe", "\\\\?\\C:\\very\\long\\path")]
    [InlineData("cmd.exe", "-Dprop=\"a b\"")]
    [InlineData("cmd.exe", "^&|<>%!")]
    [InlineData("cmd.exe", "*", "?")]
    [InlineData("cmd.exe", "h\u00e9llo", "\u65e5\u672c\u8a9e", "\ud83d\ude00")]
    [InlineData("cmd.exe", "a\u0301", "\u202eRTL\u202c")]
    [InlineData("C:\\Program Files\\Git\\bin\\git.exe", "log", "--pretty=format:%h \"%s\"")]
    [InlineData("C:\\dir\\", "arg")]
    [InlineData("C:\\a b\\dir\\", "")]
    public void RoundTripsThroughCommandLineToArgv(string executable, params string[] arguments)
    {
        var commandLine = WindowsCommandLine.Build(executable, arguments);

        Assert.Equal([executable, .. arguments], CommandLineToArgv(commandLine));
    }

    static string[] CommandLineToArgv(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var parsed = new string[count];
            for (var i = 0; i < count; i++)
                parsed[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return parsed;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LocalFree(IntPtr hMem);

    internal sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
    {
        public WindowsOnlyTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "CommandLineToArgvW is a Windows API.";
        }
    }
}
