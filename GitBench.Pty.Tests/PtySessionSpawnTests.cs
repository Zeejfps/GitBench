namespace GitBench.Pty.Tests;

/// <summary>
/// What a spawned child must find when it starts: a real terminal at the requested size, the
/// requested directory, and exactly the environment the caller described — no more.
/// </summary>
/// <remarks>
/// Every assertion here is containment on the decoded stream, never equality: the stream carries the
/// platform's startup and teardown frames, cursor addressing, and reflow around whatever the child
/// printed. See <see cref="VtText"/>. The children come from <see cref="PtyChild"/>, which prints the
/// same text from a Windows shell and from sh, so these assertions read the same on both.
/// </remarks>
[Collection(PtyTestCollection.Name)]
public class PtySessionSpawnTests
{
    /// <remarks>
    /// All three standard descriptors, not just standard output: an implementation that dups the
    /// slave onto 0 and 1 and forgets 2 passes the narrower question, and then every diagnostic the
    /// shell writes vanishes.
    /// </remarks>
    [PtyFact]
    public void Start_RunsTheChildOnThePseudoTerminal_SoItSeesATtyAtTheRequestedSize()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReportsTerminal(work, new PtySize(100, 30)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[tty=yesyesyes;cols=100;rows=30]", PtyChild.Patience),
            $"The child never reported all three descriptors on a 100x30 terminal. A 'no' in the third "
            + $"position means standard error was left behind; a size of 0x0 means the window size was "
            + $"set before the slave had ever been opened, which the kernel refuses. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Opening <c>/dev/tty</c> is the only direct question a child can ask about whether it has a
    /// controlling terminal, and the answer is no unless it was made a session leader before the slave
    /// was opened. A dup'd slave on the standard descriptors passes <c>[ -t 1 ]</c> and still leaves
    /// the child with no job control, so a Ctrl-C would reach nothing.
    /// </remarks>
    [UnixPtyFact]
    public void Start_GivesTheChildAControllingTerminalAndAForegroundProcessGroupOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReportsControllingTerminal(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[ctty=yes;foreground=yes;device=/dev/", PtyChild.Patience),
            $"The child could not open /dev/tty, or was not in the foreground process group of its "
            + $"terminal: either way it has a readable terminal with no job control and no Ctrl-C. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_RunsTheChildInTheWorkingDirectory()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.PrintsWorkingDirectory(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The child never reported the working directory it was asked for ({work.Path}). "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_RunsTheChildInAWorkingDirectoryWhoseNameContainsSpacesAndNonAscii()
    {
        using var work = new TempDirectory("a name with spaces and ünïcode");

        using var session = PtyChild.Start(PtyChild.PrintsWorkingDirectory(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The child did not start in {work.Path}. A directory holding spaces only arrives intact if "
            + $"it was passed as one argument rather than pasted into a command line. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// The base <c>posix_spawn</c> standard has no working-directory attribute, and the tempting
    /// substitute is to change this process's directory around the spawn. That is a process-wide
    /// global: two sessions started at once would each land wherever the other left it. Whether the
    /// parent's directory survives is the observable half of that, and the half a test can pin.
    /// </remarks>
    [PtyFact]
    public void Start_LeavesTheCallingProcessWorkingDirectoryAlone()
    {
        using var work = new TempDirectory();
        var before = Directory.GetCurrentDirectory();

        using var session = PtyChild.Start(PtyChild.PrintsWorkingDirectory(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The child did not start in the directory it was given. Terminal showed:\n{output.Describe()}");
        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    [PtyFact]
    public void Start_AppliesTheEnvironmentOverlay()
    {
        using var work = new TempDirectory();

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_OVERLAID")
            .WithVariable("GITBENCH_PTY_OVERLAID", "overlaid-value");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_OVERLAID=overlaid-value]", PtyChild.Patience),
            $"The child did not see the overlaid variable. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_InheritsTheParentEnvironment_WhereTheOverlayIsSilent()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_INHERITED", "inherited-value");

        using var session = PtyChild.Start(PtyChild.PrintsVariables(work, "GITBENCH_PTY_INHERITED"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_INHERITED=inherited-value]", PtyChild.Patience),
            $"The child did not inherit a variable this process had set. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_RemovesAnInheritedVariable_WhenTheOverlayValueIsNull()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_REMOVED", "inherited-value");

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_REMOVED")
            .WithVariable("GITBENCH_PTY_REMOVED", null);

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_REMOVED=unset]", PtyChild.Patience),
            $"The variable was still in the child's environment, so a null overlay value did not remove "
            + $"it. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// <para>
    /// Empty and absent are different states and only the overlay can tell them apart: an empty string
    /// means the caller set it to nothing, a null means the caller took it away. A child that reports
    /// the variable unset here has had the two collapsed into one.
    /// </para>
    /// <para>
    /// Unix-gated because Windows has no such pair to keep apart. The block this builds is right —
    /// <c>WindowsEnvironmentBlock</c> encodes an empty value as <c>NAME=</c>, and
    /// <c>WindowsEnvironmentBlockTests</c> asserts it — but no child can report the difference back:
    /// <c>GetEnvironmentVariable</c> answers null for an empty variable exactly as it does for a
    /// missing one. Asserted end to end where a child can see it, and at the block on Windows.
    /// </para>
    /// </remarks>
    [UnixPtyFact]
    public void Start_KeepsAnEmptyOverlayValueAsAnEmptyVariableRatherThanARemovalOnUnix()
    {
        using var work = new TempDirectory();

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_EMPTY")
            .WithVariable("GITBENCH_PTY_EMPTY", "");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_EMPTY=]", PtyChild.Patience),
            $"An empty overlay value was treated as a removal — the child reported the variable unset "
            + $"instead of empty. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Unix-gated for the reason above, and for a second one that bites first: setting a variable to
    /// the empty string on Windows deletes it, so the parent this inherits from cannot be put into the
    /// state the test needs. <c>WindowsEnvironmentBlockTests.AnEmptyInheritedValueSurvives</c> covers
    /// the inherited side there, where the environment is a value rather than the process's own.
    /// </remarks>
    [UnixPtyFact]
    public void Start_InheritsAnEmptyParentVariableAsAnEmptyVariableOnUnix()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_BLANK", "");

        using var session = PtyChild.Start(PtyChild.PrintsVariables(work, "GITBENCH_PTY_BLANK"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_BLANK=]", PtyChild.Patience),
            $"An inherited variable whose value is the empty string did not reach the child at all. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_CarriesAnOverlayValueThatIsNotAscii()
    {
        using var work = new TempDirectory();

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_UNICODE")
            .WithVariable("GITBENCH_PTY_UNICODE", "naïve-café-日本語");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_UNICODE=naïve-café-日本語]", PtyChild.Patience),
            $"A non-ASCII overlay value did not survive the trip into the child's environment. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Windows compares environment names case-insensitively, so an overlay key that differs only in
    /// case replaces the inherited variable rather than adding a second one. Its Unix counterpart
    /// asserts the opposite, and both have to be gated or one of them is lying.
    /// </remarks>
    [WindowsPtyFact]
    public void Start_MatchesOverlayKeysToInheritedVariables_CaseInsensitivelyOnWindows()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_CASE", "inherited-value");

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_CASE")
            .WithVariable("gitbench_pty_case", "overlaid-value");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[GITBENCH_PTY_CASE=overlaid-value]", PtyChild.Patience),
            $"An overlay key that differs only in case did not replace the inherited variable, which is "
            + $"what Windows environment name collation promises. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// POSIX compares environment names byte for byte, so an overlay key differing only in case is a
    /// second variable rather than a replacement. The two spellings carry values that differ by more
    /// than their case on purpose: <see cref="VtText.Contains"/> folds case, so an assertion that
    /// discriminated only by letter case would be vacuous.
    /// </remarks>
    [UnixPtyFact]
    public void Start_KeepsOverlayKeysApartFromInheritedVariables_CaseSensitivelyOnUnix()
    {
        using var work = new TempDirectory();
        using var inherited = new EnvironmentVariable("GITBENCH_PTY_CASE", "inherited-value");

        var options = PtyChild
            .PrintsVariables(work, "GITBENCH_PTY_CASE", "gitbench_pty_case")
            .WithVariable("gitbench_pty_case", "overlaid-value");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[gitbench_pty_case=overlaid-value]", PtyChild.Patience),
            $"The overlay under a lower-case name never reached the child. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitFor("[GITBENCH_PTY_CASE=inherited-value]", PtyChild.Patience),
            $"The inherited variable was replaced by an overlay key that only differs in case, so the "
            + $"session collated POSIX names the way Windows does. Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_PassesTerminalIdentityThroughUntouched()
    {
        using var work = new TempDirectory();

        var options = PtyChild
            .PrintsVariables(work, "TERM", "COLORTERM")
            .WithVariable("TERM", "xterm-256color")
            .WithVariable("COLORTERM", "truecolor");

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[TERM=xterm-256color]", PtyChild.Patience),
            $"The child did not see the TERM the caller supplied. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitFor("[COLORTERM=truecolor]", PtyChild.Patience),
            $"The child did not see the COLORTERM the caller supplied. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Read through <c>env</c> rather than through the shell's own variables: bash-in-sh-mode invents
    /// a TERM of <c>dumb</c> for itself when the environment carries none, so asking <c>$TERM</c> here
    /// would report a terminal identity the session never set and fail a correct implementation.
    /// </remarks>
    [PtyFact]
    public void Start_SetsNoTerminalIdentityOfItsOwn()
    {
        using var work = new TempDirectory();
        using var term = new EnvironmentVariable("TERM", null);
        using var colorTerm = new EnvironmentVariable("COLORTERM", null);

        using var session = PtyChild.Start(PtyChild.PrintsVariables(work, "TERM", "COLORTERM"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[TERM=unset]", PtyChild.Patience),
            $"The session invented a TERM the caller never asked for. Terminal showed:\n{output.Describe()}");
        Assert.True(
            output.WaitFor("[COLORTERM=unset]", PtyChild.Patience),
            $"The session invented a COLORTERM the caller never asked for. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// <para>
    /// An empty entry that disappeared, or a <c>$HOME</c> that came back expanded, means the arguments
    /// went through a shell instead of into argv.
    /// </para>
    /// <para>
    /// Unix-gated because the gate is the reporter's, not the contract's: the Windows child is
    /// <c>powershell.exe -File</c>, which drops an empty argument, strips the quote out of
    /// <c>a"b\c</c>, and rejects a bare <c>-</c> as a malformed parameter name — measured, not
    /// assumed. Every argument worth asking about is one it mangles, so a Windows arm here would
    /// assert only the three it happens to survive and would read as coverage it is not. On Windows
    /// this is <c>WindowsCommandLineTests</c>'s job instead, and it does it better: thirty argument
    /// shapes round-tripped through <c>CommandLineToArgvW</c>, the parser every Windows program
    /// actually uses. Restoring an end-to-end arm here needs a child that reports argv without a
    /// shell in the middle.
    /// </para>
    /// </remarks>
    [UnixPtyFact]
    public void Start_PassesEachArgumentAsOneArgv_IncludingOnesContainingSpacesOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(
            PtyChild.PrintsArguments(work, "", "two words", "a\"b\\c", "-", "$HOME", "*"));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("[argv=<>|<two words>|<a\"b\\c>|<->|<$HOME>|<*>|]", PtyChild.Patience),
            $"Arguments did not arrive as the argv entries they were given as. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// Rows and columns arriving swapped is the usual cause of a failure here — the winsize struct
    /// puts rows first, and <c>stty size</c> prints them that way too.
    /// </remarks>
    [PtyTheory]
    [InlineData(1, 1)]
    [InlineData(1, 200)]
    [InlineData(200, 1)]
    public void Start_GivesTheChildTheSmallestTerminalPtySizeAdmits(int columns, int rows)
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.ReportsTerminal(work, new PtySize(columns, rows)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor($"cols={columns};rows={rows}", PtyChild.Patience),
            $"The child did not see a {columns}x{rows} terminal. Terminal showed:\n{output.Describe()}");
    }

    /// <remarks>
    /// <see cref="PtySize"/> admits anything up to <see cref="ushort.MaxValue"/> because that is what a
    /// POSIX winsize holds. Windows cannot go past a signed short and its session says so by throwing,
    /// which is why this is asserted here and not everywhere.
    /// </remarks>
    [UnixPtyFact]
    public void Start_GivesTheChildTheLargestTerminalPtySizeAdmitsOnUnix()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(
            PtyChild.ReportsTerminal(work, new PtySize(ushort.MaxValue, ushort.MaxValue)));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor("cols=65535;rows=65535", PtyChild.Patience),
            $"The child did not see a 65535x65535 terminal. A size that came back as 0 or -1 means the "
            + $"dimensions were narrowed to a signed short on the way to the kernel. "
            + $"Terminal showed:\n{output.Describe()}");
    }

    [PtyFact]
    public void Start_FailsWhenTheExecutableCannotBeFound()
    {
        using var work = new TempDirectory();

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.NoSuchProgram(work)));

        Assert.Equal(PtySpawnFailure.ExecutableNotFound, thrown.Failure);
        Assert.Equal(PtyChild.MissingExecutable, thrown.Executable);
    }

    [PtyFact]
    public void Start_FailsWhenTheFullPathToTheExecutableDoesNotExist()
    {
        using var work = new TempDirectory();
        var missing = Path.Combine(work.Path, PtyChild.MissingExecutable);

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.At(work, missing)));

        Assert.Equal(PtySpawnFailure.ExecutableNotFound, thrown.Failure);
        Assert.Equal(missing, thrown.Executable);
    }

    /// <remarks>
    /// Measured with <c>posix_spawnp</c> on this machine: a file that exists but carries no execute bit
    /// answers EACCES. That is the user's to fix and it is not "not found", so an implementation that
    /// maps every failure to not-found tells them to go looking for a program sitting right there.
    /// </remarks>
    [UnixPtyFact]
    public void Start_FailsWithAccessDenied_WhenTheExecutableCannotBeRunOnUnix()
    {
        using var work = new TempDirectory();
        var options = PtyChild.NotExecutable(work);

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(options));

        Assert.Equal(PtySpawnFailure.AccessDenied, thrown.Failure);
        Assert.Equal(options.Executable, thrown.Executable);
    }

    [UnixPtyFact]
    public void Start_FailsWithAccessDenied_WhenTheExecutableIsADirectoryOnUnix()
    {
        using var work = new TempDirectory();

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.At(work, work.Path)));

        Assert.Equal(PtySpawnFailure.AccessDenied, thrown.Failure);
    }

    /// <remarks>
    /// <c>posix_spawnp</c> reports its errno as its return value and leaves the global <c>errno</c>
    /// untouched, so an implementation that reads the global one reports whatever the last unrelated
    /// call left there. Two failures that must map to different values are the cheapest way to catch
    /// that: reading the wrong variable cannot get both right.
    /// </remarks>
    [UnixPtyFact]
    public void Start_DistinguishesAMissingProgramFromAnUnrunnableOneOnUnix()
    {
        using var work = new TempDirectory();

        var missing = Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.NoSuchProgram(work)));
        var refused = Assert.Throws<PtySpawnException>(() => PtyChild.Start(PtyChild.NotExecutable(work)));

        Assert.NotEqual(missing.Failure, refused.Failure);
    }

    /// <remarks>
    /// ENOENT and EACCES have arms of their own and a test each; nothing otherwise forbids mapping
    /// every remaining errno onto one of them and sending the user to look for a program that is
    /// sitting right there. An argument list past ARG_MAX is the one third errno a test can produce on
    /// demand — measured at 4MiB against this machine's 1MiB ARG_MAX, and past MAX_ARG_STRLEN on Linux.
    /// </remarks>
    [UnixPtyFact]
    public void Start_FailsWithOther_WhenThePlatformRefusesForSomeOtherReasonOnUnix()
    {
        using var work = new TempDirectory();

        var options = PtyChild.At(work, "sh") with
        {
            Arguments = ["-c", new string('a', 4 * 1024 * 1024)],
        };

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(options));

        Assert.Equal(PtySpawnFailure.Other, thrown.Failure);
        Assert.Equal("sh", thrown.Executable);
    }

    /// <remarks>
    /// <c>posix_spawnp</c> resolves a bare name against the calling process's PATH, not against the
    /// PATH in the environment it is handed — measured on this machine, and the same is true of
    /// CreateProcessW's search. So an overlaid PATH reaches the child but does not decide what the
    /// child is. Worth pinning either way, because the opposite reading is the intuitive one.
    /// </remarks>
    [UnixPtyFact]
    public void Start_ResolvesTheProgramAgainstThisProcessPath_NotTheOverlaidOneOnUnix()
    {
        using var work = new TempDirectory();
        work.Executable("gitbench-pty-on-overlay-path", "#!/bin/sh\nprintf 'found\\n'\n");

        var options = PtyChild
            .At(work, "gitbench-pty-on-overlay-path")
            .WithVariable("PATH", work.Path);

        var thrown = Assert.Throws<PtySpawnException>(() => PtyChild.Start(options));

        Assert.Equal(PtySpawnFailure.ExecutableNotFound, thrown.Failure);
    }

    [PtyFact]
    public void Start_SeesTheOverlaidPath_EvenThoughItDidNotResolveAgainstIt()
    {
        using var work = new TempDirectory();

        var options = PtyChild
            .PrintsThePath(work)
            .WithVariable("PATH", work.Path);

        using var session = PtyChild.Start(options);
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(work.Token, PtyChild.Patience),
            $"The child did not see the PATH the overlay set. Terminal showed:\n{output.Describe()}");
    }
}
