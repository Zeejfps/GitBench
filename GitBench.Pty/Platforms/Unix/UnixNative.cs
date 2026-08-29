using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitBench.Pty.Platforms.Unix;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowSize
{
    public ushort Rows;
    public ushort Columns;
    public ushort PixelWidth;
    public ushort PixelHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollDescriptor
{
    public int Descriptor;
    public short Requested;
    public short Returned;
}

/// <summary>
/// The libc entry points a Unix pseudo-terminal session is built from, and the constants whose value
/// depends on which Unix this is.
/// </summary>
/// <remarks>
/// <para>
/// The per-platform values are properties rather than constants because one binary runs on both
/// hosts: <c>TIOCSWINSZ</c>, <c>O_NOCTTY</c> and <c>POSIX_SPAWN_SETSID</c> are all spelled
/// differently by macOS and by Linux, and a single shared number would silently set the wrong
/// terminal size on one of them. Values that do agree stay constants. Errno numbers live in
/// <see cref="UnixErrno"/>, where the decisions taken on them are under test.
/// </para>
/// <para>
/// <c>posix_spawnattr_t</c> and <c>posix_spawn_file_actions_t</c> are opaque: a pointer on macOS and
/// a struct of a few hundred bytes on glibc and musl. They are passed as a pointer to a zeroed
/// buffer large enough for either, which is the only shape that works for both without a per-platform
/// declaration of a layout neither libc promises.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal static unsafe partial class UnixNative
{
    /// <summary>Bytes of zeroed storage that stands in for either libc's opaque posix_spawn types.</summary>
    public const int SpawnStorageBytes = 512;

    public const int ReadWrite = 0x0002;
    public const short Readable = 0x0001;
    public const int Terminate = 9;
    public const int WaitForever = -1;

    /// <remarks>Linux spells this 0400 and macOS 0x20000, and an unknown flag is refused outright.</remarks>
    public static int NoControllingTerminal => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

    /// <summary>Keeps a descriptor out of the spawned child, which both hosts honour on the open itself.</summary>
    public static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    /// <remarks>
    /// Whether the platform hands a variadic function its variadic arguments on the stack rather than
    /// in the registers the fixed ones use. Apple's arm64 ABI is the one that does, and it is the
    /// reason <see cref="SetWindowSize"/> has two declarations of the same entry point: a managed
    /// caller has no way to say "variadic", so the shape of the call has to be chosen by hand and the
    /// wrong one reads the size out of whatever the stack happened to hold.
    /// </remarks>
    static bool VariadicArgumentsGoOnTheStack { get; } =
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <remarks>
    /// The Linux value is the one x86, ARM and ARM64 share; the older MIPS, PowerPC, SPARC and Alpha
    /// ports spell it the way macOS does, and this session has never been run on those.
    /// </remarks>
    public static nuint SetWindowSizeRequest => OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

    /// <summary>Makes the spawned child a session leader, so that opening the slave gives it a ctty.</summary>
    public static short SpawnSetSession => OperatingSystem.IsMacOS() ? (short)0x0400 : (short)0x0080;

    [LibraryImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    public static partial int OpenPseudoTerminal(int flags);

    [LibraryImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    public static partial int GrantSlave(int master);

    [LibraryImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    public static partial int UnlockSlave(int master);

    /// <remarks>
    /// Returns a pointer into static storage and has no reentrant form on macOS, so callers hold a
    /// lock across the call and copy the name out before releasing it.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "ptsname", SetLastError = true)]
    public static partial byte* SlaveName(int master);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    public static partial int Open(byte* path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    public static partial int Pipe(int* descriptors);

    /// <summary>Sets or reads a terminal's size, which is the one <c>ioctl</c> this session issues.</summary>
    public static int SetWindowSize(int descriptor, nuint request, WindowSize* size) =>
        VariadicArgumentsGoOnTheStack
            ? SetWindowSizeOnTheStack(descriptor, request, 0, 0, 0, 0, 0, 0, size)
            : SetWindowSizeInRegisters(descriptor, request, size);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int SetWindowSizeInRegisters(int descriptor, nuint request, WindowSize* size);

    /// <remarks>
    /// Six arguments of padding fill the registers a call would otherwise use, so that the size lands
    /// in the first stack slot — which is where a variadic callee on Apple's arm64 looks for its first
    /// variadic argument.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int SetWindowSizeOnTheStack(
        int descriptor,
        nuint request,
        nint first,
        nint second,
        nint third,
        nint fourth,
        nint fifth,
        nint sixth,
        WindowSize* size);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int descriptor, byte* buffer, nuint count);

    /// <remarks>
    /// <c>nfds_t</c> is an unsigned int on macOS and an unsigned long on Linux; the wider of the two
    /// is passed and the narrower callee reads the low half of the same register.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollDescriptor* descriptors, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int process, int signal);

    [LibraryImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    public static partial int GetProcessGroup(int process);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    public static partial int WaitForProcess(int process, out int status, int options);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    public static partial int SpawnActionsInit(void* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    public static partial int SpawnActionsDestroy(void* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    public static partial int SpawnActionsAddOpen(
        void* actions, int descriptor, byte* path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    public static partial int SpawnActionsAddDuplicate(void* actions, int descriptor, int onto);

    /// <remarks>
    /// How the descriptors that cannot be opened close-on-exec are kept out of the child: macOS has no
    /// <c>pipe2</c> to open a pipe with the flag already set, and <c>fcntl</c> is variadic.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    public static partial int SpawnActionsAddClose(void* actions, int descriptor);

    /// <remarks>
    /// <c>posix_spawn</c> has no working-directory argument of its own and the alternative is changing
    /// this process's directory around the spawn, which is process-global. This entry point is the
    /// platform floor of the whole session: macOS 10.15 and glibc 2.29.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    public static partial int SpawnActionsAddChangeDirectory(void* actions, byte* path);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    public static partial int SpawnAttributesInit(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    public static partial int SpawnAttributesDestroy(void* attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    public static partial int SpawnAttributesSetFlags(void* attributes, short flags);

    /// <remarks>
    /// Reports its errno as its return value and leaves the global one untouched, so a caller that
    /// reads <c>errno</c> here reports whatever the last unrelated call left there. The <c>p</c>
    /// resolves a bare name against this process's PATH rather than against the environment it is
    /// handed.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "posix_spawnp")]
    public static partial int SpawnOnPath(
        out int process, byte* file, void* actions, void* attributes, byte** arguments, byte** environment);
}
