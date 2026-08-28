using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitBench.Pty.Platforms.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    public short X;
    public short Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public uint Size;
    public IntPtr Reserved;
    public IntPtr Desktop;
    public IntPtr Title;
    public uint X;
    public uint Y;
    public uint XSize;
    public uint YSize;
    public uint XCountChars;
    public uint YCountChars;
    public uint FillAttribute;
    public uint Flags;
    public ushort ShowWindow;
    public ushort Reserved2Size;
    public IntPtr Reserved2;
    public IntPtr StdInput;
    public IntPtr StdOutput;
    public IntPtr StdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public IntPtr AttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr Process;
    public IntPtr Thread;
    public uint ProcessId;
    public uint ThreadId;
}

/// <summary>
/// The kernel32 entry points a pseudo-console session is built from.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsNative
{
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint StartfUseStdHandles = 0x00000100;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const nuint ProcThreadAttributePseudoConsole = 0x00020016;

    public const uint Infinite = 0xFFFFFFFF;
    public const uint WaitObject0 = 0;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBrokenPipe = 109;
    public const int ErrorNoData = 232;
    public const int ErrorOperationAborted = 995;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(
        out IntPtr readPipe, out IntPtr writePipe, IntPtr securityAttributes, uint suggestedBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CreatePseudoConsole(
        Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int ResizePseudoConsole(SafePseudoConsoleHandle pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void ClosePseudoConsole(IntPtr pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(
        IntPtr attributeList, uint attributeCount, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        void* value,
        nuint valueSize,
        void* previousValue,
        nuint* returnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        void* environment,
        char* currentDirectory,
        StartupInfoEx* startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(
        SafeFileHandle file, byte* buffer, uint bytesToRead, out int bytesRead, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(
        SafeFileHandle file, byte* buffer, uint bytesToWrite, out int bytesWritten, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
