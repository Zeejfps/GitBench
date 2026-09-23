using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using Xunit;

namespace GitBench.Tests;

/// <summary>When parameter info opens, what keeps it current, and what closes it: the server alone
/// decides whether the caret is in a call, so an answer of nothing is what ends it.</summary>
public sealed class ParameterHintsTests
{
    private const string Path = "/repo/Program.cs";

    private static readonly SignatureHelp Call = new(
        [new SignatureInfo("Add(int a, int b)", null, [new ParameterSpan(4, 5), new ParameterSpan(11, 5)], null)],
        0,
        0);

    private readonly FakeSignatures _server = new();
    private readonly List<SignatureHelp?> _shown = [];
    private readonly ParameterHints _hints;

    public ParameterHintsTests() =>
        _hints = new ParameterHints(_server, new ImmediateDispatcher(), help => _shown.Add(help), _ => Counted);

    private int? Counted { get; set; }

    private static TextPosition At(int column) => TextPosition.At(1, column);

    [Fact]
    public void AskingShowsWhatTheServerSays()
    {
        _server.Next = Call;

        _hints.Invoke(Path, At(4));

        Assert.True(_hints.IsOpen);
        Assert.Same(Call, Assert.Single(_shown));
        Assert.IsType<SignatureAsk.Requested>(_server.Asked[^1]);
    }

    [Fact]
    public void ATriggerCharacterOpensItAndAnyOtherDoesNot()
    {
        _server.Next = Call;

        _hints.Typed(Path, At(3), 'x');
        Assert.False(_hints.IsOpen);
        Assert.Empty(_server.Asked);

        _hints.Typed(Path, At(4), '(');
        Assert.True(_hints.IsOpen);
        Assert.Equal(new SignatureAsk.TypedTrigger('('), _server.Asked[^1]);
    }

    [Fact]
    public void WhileOpenEveryMoveAsksAgain()
    {
        _server.Next = Call;
        _hints.Invoke(Path, At(4));

        _hints.Typed(Path, At(5), '1');
        _hints.Moved(Path, At(6));

        Assert.Equal(3, _server.Asked.Count);
        Assert.All(_server.Asked.Skip(1), ask => Assert.IsType<SignatureAsk.ContentChanged>(ask));
    }

    [Fact]
    public void AnAnswerOfNothingClosesIt()
    {
        _server.Next = Call;
        _hints.Invoke(Path, At(4));

        _server.Next = null;
        _hints.Moved(Path, At(20));

        Assert.False(_hints.IsOpen);
        Assert.Null(_shown[^1]);
    }

    [Fact]
    public void AServerThatNeverNamesTheArgumentIsCountedForIt()
    {
        _server.Next = new SignatureHelp(Call.Signatures, 0, null);
        Counted = 1;

        _hints.Typed(Path, At(8), ',');

        Assert.Equal(1, _shown[^1]!.ActiveParameterOf(0));
    }

    [Fact]
    public void AServerThatNamesTheArgumentIsBelievedOverTheCount()
    {
        _server.Next = Call;
        Counted = 1;

        _hints.Invoke(Path, At(4));

        Assert.Equal(0, _shown[^1]!.ActiveParameterOf(0));
    }

    [Fact]
    public void AFileWithNoServerNeverOpens()
    {
        _server.Serves = false;

        _hints.Invoke(Path, At(4));

        Assert.False(_hints.IsOpen);
        Assert.Empty(_server.Asked);
    }

    private sealed class FakeSignatures : ISignatureHelpSource
    {
        public bool Serves { get; set; } = true;

        public SignatureHelp? Next { get; set; }

        public List<SignatureAsk> Asked { get; } = [];

        public bool CanHelpWithSignatures(string absolutePath) => Serves;

        public SignatureHelpSupport SignatureTriggers(string absolutePath) =>
            new SignatureHelpSupport.Offered(['(', ','], [')']);

        public Task<SignatureReply> SignatureHelpAsync(
            string absolutePath, FileLine line, RawColumn column, SignatureAsk ask, CancellationToken ct)
        {
            Asked.Add(ask);
            return Task.FromResult<SignatureReply>(Next is { } help
                ? new SignatureReply.Answered(help)
                : new SignatureReply.Answered(SignatureHelp.Nothing));
        }
    }
}
