using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Features.LanguageServers;
using GitBench.Features.Pairing;
using GitBench.Git;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using GitBench.Theming;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The names in the agent's code, and what a language server made of them before the code is in
/// the file: declared already, declared by the code itself, or declared nowhere — the last only
/// from a server that had finished loading.
/// </summary>
public sealed class DraftNamesTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "draft-names-repo"));
    private static readonly string File = Path.Combine(Root, "src", "Main.cs");

    // "var total = Sum(items) + 1; // note"
    private static readonly IReadOnlyList<TokenSpan> Colored =
    [
        new(0, 3, TokenColorSlot.Keyword),
        new(4, 5, TokenColorSlot.Variable),
        new(12, 3, TokenColorSlot.Function),
        new(16, 5, TokenColorSlot.Variable),
        new(25, 1, TokenColorSlot.Number),
        new(28, 7, TokenColorSlot.Comment),
    ];

    [Fact]
    public void OnlyWhatTheColorsCallANameIsAskedAbout()
    {
        var names = DraftNames.In(["var total = Sum(items) + 1; // note"], [Colored]);

        Assert.Equal(["total", "Sum", "items"], names.Select(name => name.Text));
        Assert.Equal(12, names[1].Start.Value);
        Assert.Equal(15, names[1].End.Value);
    }

    // Uncolored words are prose in markup more often than code, and asking about them would mark
    // every word of a paragraph as undeclared.
    [Fact]
    public void AnUncoloredWordIsNotAskedAbout() =>
        Assert.Empty(DraftNames.In(["<p>Hello world</p>"], [[]]));

    [Fact]
    public void ANamePastATabIsFoundAtItsRawColumn()
    {
        var names = DraftNames.In(["\tSum()"], [[new TokenSpan(4, 3, TokenColorSlot.Function)]]);

        var sum = Assert.Single(names);
        Assert.Equal(1, sum.Start.Value);
        Assert.Equal(4, sum.End.Value);
    }

    [Fact]
    public void NamesAreSortedByWhereTheyAreDeclared()
    {
        // File: 6 lines; the code (2 lines) goes in after line 2, so text lines 3–4 are the code
        // and text line 6 is the file's line 4.
        var splice = DraftSplice.Of(["a", "b", "c", "d", "e", "f"], new DraftPlace.InsertAfter(new FileLine(2)), ["x", "y"]);
        DraftNameAt[] names = [Name("Elsewhere"), Name("Local"), Name("Below"), Name("Nowhere"), Name("Refused")];
        DraftDefinition[] answers =
        [
            Declared(new DefinitionTarget.InRepo("src/Lib.cs", AtLine(9))),
            Declared(new DefinitionTarget.InRepo("src/Main.cs", AtLine(3))),
            Declared(new DefinitionTarget.InRepo("src/Main.cs", AtLine(5))),
            DraftDefinition.Undeclared.Instance,
            DraftDefinition.Unanswered.Instance,
        ];

        var kinds = DraftNames.Classify(names, answers, splice, File, Root, settled: true)
            .ToDictionary(name => name.Text, name => name.Kind);

        Assert.Equal(new DraftNameKind.Existing(Path.Combine(Root, "src", "Lib.cs"), new FileLine(10)), kinds["Elsewhere"]);
        Assert.IsType<DraftNameKind.Introduced>(kinds["Local"]);
        Assert.Equal(new DraftNameKind.Existing(File, new FileLine(4)), kinds["Below"]);
        Assert.IsType<DraftNameKind.Missing>(kinds["Nowhere"]);
        Assert.False(kinds.ContainsKey("Refused"));
    }

    // A server that has not read the project yet answers "nowhere" about everything in it.
    [Fact]
    public void NothingIsCalledMissingByAServerStillLoading()
    {
        var splice = DraftSplice.Of([], new DraftPlace.InsertAfter(new FileLine(0)), ["x"]);

        var names = DraftNames.Classify([Name("Nowhere")], [DraftDefinition.Undeclared.Instance], splice, File, Root, settled: false);

        Assert.Empty(names);
    }

    [Fact]
    public void EachMissingNameIsListedOnceInTheOrderUsed()
    {
        DraftName[] names =
        [
            new(0, new RawColumn(0), new RawColumn(1), "B", DraftNameKind.Missing.Instance),
            new(0, new RawColumn(2), new RawColumn(3), "A", DraftNameKind.Missing.Instance),
            new(1, new RawColumn(0), new RawColumn(1), "B", DraftNameKind.Missing.Instance),
            new(1, new RawColumn(2), new RawColumn(3), "C", DraftNameKind.Introduced.Instance),
        ];

        Assert.Equal(["B", "A"], DraftNames.Missing(names));
    }

    [Fact]
    public void TheCheckerAsksAboutTheFileWithTheCodeInIt()
    {
        var fx = new CheckerFixture(new ServerState.Ready());
        fx.Servers.Answer = [DraftDefinition.Undeclared.Instance];

        fx.Check(["class A", "{", "}"], new DraftPlace.InsertAfter(new FileLine(2)), ["  Sum();"], [[new TokenSpan(2, 3, TokenColorSlot.Function)]]);

        var asked = Assert.Single(fx.Servers.Asked);
        Assert.Equal("class A\n{\n  Sum();\n}", asked.Draft);
        Assert.Equal(new TextPosition(new FileLine(3), new RawColumn(2)), Assert.Single(asked.Names));
        Assert.IsType<DraftNameKind.Missing>(Assert.Single(fx.Heard[^1]).Kind);
    }

    // The first answer came from a server still loading, so it says nothing is missing; the one
    // after it turns ready is the one to believe.
    [Fact]
    public void AnAnswerFromAServerStillLoadingIsAskedAgainOnceItIsReady()
    {
        var fx = new CheckerFixture(new ServerState.Indexing(40));
        fx.Servers.Answer = [DraftDefinition.Undeclared.Instance];
        fx.Check(["x"], new DraftPlace.InsertAfter(new FileLine(1)), ["Sum();"], [[new TokenSpan(0, 3, TokenColorSlot.Function)]]);
        Assert.Empty(fx.Heard[^1]);

        fx.Servers.SetState(new ServerState.Indexing(80));
        Assert.Single(fx.Servers.Asked);

        fx.Servers.SetState(new ServerState.Ready());
        Assert.Equal(2, fx.Servers.Asked.Count);
        Assert.IsType<DraftNameKind.Missing>(Assert.Single(fx.Heard[^1]).Kind);

        fx.Servers.SetState(new ServerState.Indexing(null));
        fx.Servers.SetState(new ServerState.Ready());
        Assert.Equal(2, fx.Servers.Asked.Count);
    }

    // Ready is said on the first answer to anything, and the first answers are about the open file
    // alone: an imported name comes back declared at its import line. The later answers are the
    // ones to believe, so each answer given ready is followed by another, twice.
    [Fact]
    public async Task AnAnswerGivenReadyIsAskedAgainTwiceMore()
    {
        var fx = new CheckerFixture(new ServerState.Ready());
        fx.Servers.Answer = [Declared(new DefinitionTarget.InRepo("src/Main.cs", AtLine(0)))];
        fx.Check(["import x", "", "y"], new DraftPlace.InsertAfter(new FileLine(3)), ["Sum();"], [[new TokenSpan(0, 3, TokenColorSlot.Function)]]);
        Assert.Equal(new DraftNameKind.Existing(File, new FileLine(1)), Assert.Single(fx.Heard[^1]).Kind);

        fx.Servers.Answer = [Declared(new DefinitionTarget.InRepo("src/Lib.cs", AtLine(4)))];
        await fx.ElapseWait(expectedAsks: 2);
        Assert.Equal(new DraftNameKind.Existing(Path.Combine(Root, "src", "Lib.cs"), new FileLine(5)), Assert.Single(fx.Heard[^1]).Kind);

        await fx.ElapseWait(expectedAsks: 3);
        Assert.Empty(fx.Waits);
    }

    [Fact]
    public void WhatAHoverShowsRidesWithTheName()
    {
        var fx = new CheckerFixture(new ServerState.Ready());
        fx.Servers.Answer = [new DraftDefinition.Declared(
            [new DefinitionTarget.InRepo("src/Lib.cs", AtLine(4))], new HoverText("function Sum(): number"))];

        fx.Check(["x"], new DraftPlace.InsertAfter(new FileLine(1)), ["Sum();"], [[new TokenSpan(0, 3, TokenColorSlot.Function)]]);

        Assert.Equal("function Sum(): number", Assert.Single(fx.Heard[^1]).Docs);
    }

    // Language servers run for the repository on screen; asked now, the question would go to
    // another repository's server.
    [Fact]
    public void AStopInARepositoryOffScreenIsAskedAboutWhenItComesBack()
    {
        var fx = new CheckerFixture(new ServerState.Ready());
        fx.OnScreen.Value = new Repo(Guid.NewGuid(), Path.Combine(Root, "..", "other"), "other");
        fx.Servers.Answer = [DraftDefinition.Undeclared.Instance];

        fx.Check(["x"], new DraftPlace.InsertAfter(new FileLine(1)), ["Sum();"], [[new TokenSpan(0, 3, TokenColorSlot.Function)]]);
        Assert.Empty(fx.Servers.Asked);

        fx.OnScreen.Value = fx.Repo;
        Assert.Single(fx.Servers.Asked);
    }

    [Fact]
    public void CodeWithNoNamesIsNotAskedAbout()
    {
        var fx = new CheckerFixture(new ServerState.Ready());

        fx.Check(["x"], new DraftPlace.InsertAfter(new FileLine(1)), ["// just a note"], [[new TokenSpan(0, 14, TokenColorSlot.Comment)]]);

        Assert.Empty(fx.Servers.Asked);
    }

    private static DraftNameAt Name(string text) => new(0, new RawColumn(0), new RawColumn(text.Length), text);

    private static DraftDefinition Declared(DefinitionTarget target) => new DraftDefinition.Declared([target]);

    private static LspPosition AtLine(int zeroBased) => new(new LspLine(zeroBased), new LspCharacter(0));

    private sealed class CheckerFixture
    {
        public CheckerFixture(ServerState state)
        {
            Servers = new FakeServers(state);
            OnScreen = new State<Repo?>(Repo);
            Checker = new DraftNameChecker(Servers, OnScreen, Repo, new ImmediateDispatcher(), (_, _) =>
            {
                var wait = new TaskCompletionSource();
                Waits.Add(wait);
                return wait.Task;
            });
        }

        public List<TaskCompletionSource> Waits { get; } = [];

        // The recheck runs on from the wait on whatever thread finishes it: waited for, not assumed.
        public async Task ElapseWait(int expectedAsks)
        {
            var wait = Waits[0];
            Waits.RemoveAt(0);
            wait.SetResult();
            for (var i = 0; i < 500 && Heard.Count < expectedAsks; i++) await Task.Delay(1);
            Assert.Equal(expectedAsks, Servers.Asked.Count);
        }

        public Repo Repo { get; } = new(Guid.NewGuid(), Root, "repo");

        public FakeServers Servers { get; }

        public State<Repo?> OnScreen { get; }

        public DraftNameChecker Checker { get; }

        public List<IReadOnlyList<DraftName>> Heard { get; } = [];

        public void Check(IReadOnlyList<string> file, DraftPlace place, IReadOnlyList<string> code, IReadOnlyList<IReadOnlyList<TokenSpan>> spans) =>
            Checker.Check(File, file, place, code, spans, Heard.Add);
    }

    private sealed class FakeServers : IDraftDefinitionSource
    {
        private static readonly LanguageServerConfig Config = Assert.IsType<ConfigParse.Loaded>(LanguageServerConfig.Parse(
            """
            { "servers": { "csharp": { "command": "csharp-ls", "extensions": [".cs"] } } }
            """)).Config;

        private readonly State<LanguageServerSnapshot> _snapshot;

        public FakeServers(ServerState state) => _snapshot = new State<LanguageServerSnapshot>(Snapshot(state));

        public IReadable<LanguageServerSnapshot> Active => _snapshot;

        public IReadOnlyList<DraftDefinition> Answer { get; set; } = [];

        public List<(string Path, string Draft, IReadOnlyList<TextPosition> Names)> Asked { get; } = [];

        public void SetState(ServerState state) => _snapshot.Value = Snapshot(state);

        public Task<DraftDefinitions> DefineInDraftAsync(
            string absolutePath, string draft, IReadOnlyList<TextPosition> names, CancellationToken ct)
        {
            Asked.Add((absolutePath, draft, names));
            return Task.FromResult<DraftDefinitions>(new DraftDefinitions.Answered(Answer));
        }

        private static LanguageServerSnapshot Snapshot(ServerState state) =>
            new(Config, [new ServerStatus(RepositoryId.New(), LanguageId.Of("csharp"), state)], [], [], ConfigFileExists: true);
    }
}
