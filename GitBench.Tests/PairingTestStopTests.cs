using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>Test stops: the agent writes one test file, which must run red before the stop is the
/// user's; Done only closes it green, or red when the user says so; the test can be taken back
/// out; and a test that passes before any change is sent back.</summary>
public sealed class PairingTestStopTests : IDisposable
{
    private static readonly TestRun Red = new TestRun.Failed(1, "Expected 6, got 0");
    private static readonly TestRun Green = new TestRun.Passed("1 passed");

    private readonly RecordingPairingPresentation _presentation = new();
    private readonly ScriptedWorkspace _workspace = new() { TestCommand = new TestCommand("dotnet test --filter {test}") };
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly PairingStore _store;

    public PairingTestStopTests()
    {
        _store = new PairingStore("Multiply", "Agent", _presentation, _workspace, _dispatcher, _clock);
        _store.MarkRunning();
    }

    public void Dispose() => _store.Dispose();

    private T Await<T>(Task<T> task, string what)
    {
        Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);
        return task.Result;
    }

    private void Await(Task task, string what) => Pump.WaitFor(_dispatcher, () => task.IsCompleted, what);

    private void OpenTestStop()
    {
        var opening = Await(_store.OpenStopAsync(new StopTarget("src/Calc.cs", "Calc", null), "Test Multiply", "r", PairingStopKind.Test, false, CancellationToken.None), "the stop");
        Assert.IsType<StopOpening.Opened>(opening);
    }

    private TestWriting Write(string path = "tests/CalcTests.cs") =>
        Await(_store.WriteTestAsync(path, "test", "CalcTests.Multiply", null, CancellationToken.None), "the write");

    // What the user does after reading the test: runs it with the repository's command.
    private void WriteAndRun()
    {
        Assert.IsType<TestWriting.AwaitingUser>(Write());
        _store.RunTest("dotnet test --filter {test}");
    }

    private PairingAction NextAction(string what)
    {
        var wait = _store.WaitAsync(CancellationToken.None);
        Pump.WaitFor(_dispatcher, () => wait.IsCompleted, what);
        return wait.Result;
    }

    [Fact]
    public void ARedTest_OpensTheStopForTheUser()
    {
        _workspace.Runs.Enqueue(Red);
        OpenTestStop();

        Assert.IsType<TestWriting.AwaitingUser>(Write());
        Assert.Empty(_workspace.RunNames);
        _store.RunTest("dotnet test --filter {test}");
        var ran = Assert.IsType<PairingAction.TestRan>(NextAction("the red run"));

        Assert.IsType<TestRun.Failed>(ran.Run);
        Assert.IsType<TestState.Red>(_store.Stop.Value!.Test!.State);
        Assert.Equal("test", _workspace.Files["tests/CalcTests.cs"]);
        Assert.Equal(["CalcTests.Multiply"], _workspace.RunNames);
    }

    [Fact]
    public void ATestThatAlreadyPasses_IsTakenBackOut()
    {
        _workspace.Runs.Enqueue(Green);
        OpenTestStop();

        WriteAndRun();
        var ran = Assert.IsType<PairingAction.TestRan>(NextAction("the run"));

        Assert.IsType<TestRun.Passed>(ran.Run);
        Assert.Null(_store.Stop.Value!.Test);
        Assert.Empty(_workspace.Files);
    }

    [Fact]
    public void OnlyTestFiles_CanBeWritten()
    {
        OpenTestStop();

        var refused = Assert.IsType<TestWriting.Refused>(Write("src/Calc.cs"));

        Assert.Contains("not a test file", refused.Message);
        Assert.Empty(_workspace.Files);
    }

    [Fact]
    public void ATestFor_AnEditStop_IsRefused()
    {
        Await(_store.OpenStopAsync(new StopTarget("src/Calc.cs", "Calc", null), "t", "r", PairingStopKind.Edit, false, CancellationToken.None), "the stop");

        Assert.IsType<TestWriting.Refused>(Write());
    }

    [Fact]
    public void Done_WhileStillRed_KeepsTheStopOpen_UntilDoneAnyway()
    {
        _workspace.Runs.Enqueue(Red);
        OpenTestStop();
        WriteAndRun();
        NextAction("the red run");

        Await(_store.DoneAsync(), "the first Done");
        Assert.IsType<TestState.Red>(_store.Stop.Value!.Test!.State);
        Assert.True(((TestState.Red)_store.Stop.Value!.Test!.State).AfterDone);

        // Still red: only Done anyway closes it now.
        Await(_store.DoneAsync(), "a Done while still red");
        Assert.NotNull(_store.Stop.Value);

        Await(_store.CloseRedAsync(), "Done anyway");
        var done = Assert.IsType<PairingAction.Done>(NextAction("the red close"));
        Assert.True(done.Forced);
        Assert.IsType<TestRun.Failed>(done.Test);
    }

    [Fact]
    public void Done_WhenGreen_CarriesThePassingRun()
    {
        _workspace.Runs.Enqueue(Red);
        _workspace.Runs.Enqueue(Green);
        OpenTestStop();
        WriteAndRun();
        NextAction("the red run");

        Await(_store.DoneAsync(), "Done");

        var done = Assert.IsType<PairingAction.Done>(NextAction("Done"));
        Assert.IsType<TestRun.Passed>(done.Test);
        Assert.False(done.Forced);
        Assert.Null(_store.Stop.Value);
    }

    [Fact]
    public void WithoutATestCommand_TheAgentsSuggestionIsOffered_AndTheTestRunsOnceTheUserRunsIt()
    {
        _workspace.TestCommand = null;
        _workspace.Runs.Enqueue(Red);
        OpenTestStop();

        Assert.IsType<TestWriting.AwaitingUser>(
            Await(_store.WriteTestAsync("tests/CalcTests.cs", "test", "CalcTests.Multiply", "npm test -- -t {test}", CancellationToken.None), "the write"));
        var awaiting = Assert.IsType<TestState.AwaitingRun>(_store.Stop.Value!.Test!.State);
        Assert.Equal("npm test -- -t {test}", awaiting.Command);
        Assert.Empty(_workspace.RunNames);

        _store.RunTest("npm test -- -t {test}");

        Assert.IsType<PairingAction.TestRan>(NextAction("the run"));
        Assert.Equal("npm test -- -t {test}", _workspace.TestCommand?.Template);
    }

    [Fact]
    public void UndoTest_RestoresTheFile_AndTellsTheAgent()
    {
        _workspace.Files["tests/CalcTests.cs"] = "before";
        _workspace.Runs.Enqueue(Red);
        OpenTestStop();
        WriteAndRun();
        NextAction("the red run");

        Await(_store.UndoTestAsync(), "the undo");

        Assert.Equal("before", _workspace.Files["tests/CalcTests.cs"]);
        Assert.IsType<PairingAction.TestUndone>(NextAction("the undo"));
        Assert.Null(_store.Stop.Value!.Test);
    }

    [Fact]
    public void ATestNameThatCouldReachTheShell_IsRefused()
    {
        OpenTestStop();

        Assert.IsType<TestWriting.Refused>(Await(_store.WriteTestAsync("tests/CalcTests.cs", "t", "x & del *", null, CancellationToken.None), "the write"));
        Assert.IsType<TestWriting.Refused>(Await(_store.WriteTestAsync("tests/CalcTests.cs", "t", "x --config=evil", null, CancellationToken.None), "the write"));
        Assert.IsType<TestWriting.Refused>(Await(_store.WriteTestAsync("tests/CalcTests.cs", "t", "-pevil", null, CancellationToken.None), "the write"));
    }

    [Fact]
    public void ConfigurationFiles_AreNotTestFiles()
    {
        OpenTestStop();

        Assert.IsType<TestWriting.Refused>(Write("tests/conftest.py"));
        Assert.IsType<TestWriting.Refused>(Write("Calc.Tests/Calc.Tests.csproj"));
        Assert.IsType<TestWriting.Refused>(Write("tests/jest.config.js"));
        Assert.Empty(_workspace.Files);
    }
}

/// <summary>What counts as a test file, and what a test name may carry into the command.</summary>
public sealed class TestFilesTests
{
    [Theory]
    [InlineData("GitBench.Tests/PairingStoreTests.cs", true)]
    [InlineData("tests/test_client.py", true)]
    [InlineData("src/client.test.ts", true)]
    [InlineData("src/__tests__/client.ts", true)]
    [InlineData("pkg/client_test.go", true)]
    [InlineData("src/CalculatorTest.java", true)]
    [InlineData("spec/models/user_spec.rb", true)]
    [InlineData("src/Calculator.cs", false)]
    [InlineData("src/latest.cs", false)]
    [InlineData("src/contest/Rules.cs", false)]
    [InlineData("tests/../src/Calculator.cs", false)]
    [InlineData("README", false)]
    public void IsTestPath(string path, bool expected) => Assert.Equal(expected, TestFiles.IsTestPath(path));

    [Fact]
    public void TestCommand_FillsTheName()
    {
        Assert.Equal("dotnet test --filter FullyQualifiedName~Calc.Multiply",
            new TestCommand("dotnet test --filter {test}").For("FullyQualifiedName~Calc.Multiply"));
    }

    [Theory]
    [InlineData("a;rm -rf /")]
    [InlineData("a && b")]
    [InlineData("$(whoami)")]
    [InlineData("a|b")]
    [InlineData("\"quoted\"")]
    [InlineData("%PATH%")]
    public void TestCommand_RefusesShellSyntax(string name) => Assert.Null(new TestCommand("run {test}").For(name));

    [Fact]
    public async Task Runner_ReportsPassAndFailWithOutput()
    {
        var dir = Path.GetTempPath();
        var pass = await TestCommandRunner.RunAsync(dir, "echo hello", CancellationToken.None);
        Assert.Contains("hello", Assert.IsType<TestRun.Passed>(pass).Output);

        var fail = await TestCommandRunner.RunAsync(dir, "exit 3", CancellationToken.None);
        Assert.Equal(3, Assert.IsType<TestRun.Failed>(fail).ExitCode);
    }
}
