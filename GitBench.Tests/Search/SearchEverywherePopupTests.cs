using GitBench.App;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Features.Search;
using GitBench.Git;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Tests.Terminal;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Search;

/// <summary>
/// The popup mounted headlessly over a window whose keyboard is in a text field: double Shift opens
/// it from there, Esc and a click outside close it, Tab switches tabs, and it reopens on the last
/// query with all of it selected.
/// </summary>
public sealed class SearchEverywherePopupTests : IDisposable
{
    private const int Width = 1000;
    private const int Height = 700;

    private readonly TempDir _dir = new("gitbench-search-popup-");
    private readonly RepoRegistry _registry;
    private readonly SearchEverywhereViewModel _search;
    private readonly FixedSymbolIndex _index = new();
    private readonly GuiTestHarness _harness;
    private TextInputView _field = null!;

    public SearchEverywherePopupTests()
    {
        var root = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(root);
        TestGit.Init(root);
        File.WriteAllText(Path.Combine(root, "Alpha.cs"), "class Alpha { }\n");

        var statePath = Path.Combine(_dir.Path, "state.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        _registry.Open(root);

        _search = new SearchEverywhereViewModel(
            _registry,
            new GitService(new NullActivityTracker()),
            _index,
            new ScriptedWorkspaceSymbols(),
            new NoFileBrowsers(),
            new ImmediateDispatcher(),
            TimeProvider.System);

        var preferences = new PreferencesService(new Preferences(), Path.Combine(_dir.Path, "prefs.json"));
        _harness = GuiTestHarness.Create(
            ctx =>
            {
                var input = ctx.Require<InputSystem>();
                _field = new TextInputView(ctx.Canvas);
                var fieldKeys = new FileFinderInputController(_field, input, ctx.Require<IClipboard>());
                _field.UseController(input, fieldKeys);
                _field.Use(() =>
                {
                    fieldKeys.BeginEditing();
                    return new SubscriptionGroup();
                });

                var localization = ctx.Require<ILocalizationService>();
                var keybind = new AppKeybindController(
                    new KeyMap(), _registry, new GitBench.Features.Repos.RepoHoverState(),
                    new GitBench.Features.Repos.RepoBarCollapseState(preferences), localization, new MessageBus(),
                    AgentChatFixtures.Create(_registry, preferences, localization),
                    new State<MainViewMode>(MainViewMode.LocalChanges), new NoFileBrowsers(),
                    new State<SidebarPane>(SidebarPane.Branches), _search, input, TimeProvider.System);

                return new Stack
                {
                    Children =
                    [
                        new Box { Height = 40f, Children = [new Raw { View = _field }] },
                        new SearchEverywhereOverlay(),
                    ],
                }
                .WithController(input, () => keybind)
                .BuildView(ctx);
            },
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new NoClipboard());
                ctx.AddService<IKeyMap>(new KeyMap());
                ctx.AddService(_search);
            });
        _harness.Layout();
    }

    public void Dispose()
    {
        _harness.Dispose();
        _search.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private void DoubleShift()
    {
        for (var tap = 0; tap < 2; tap++)
        {
            _harness.KeyDown(KeyboardKey.LeftShift, InputModifiers.Shift);
            _harness.KeyUp(KeyboardKey.LeftShift);
        }

        _harness.Layout();
    }

    private TextInputView QueryField() =>
        _harness.Root.SelfAndDescendants().OfType<TextInputView>().Single(v => !ReferenceEquals(v, _field));

    [Fact]
    public void DoubleShift_FromAFocusedTextField_OpensSearch_WithTheCaretInItsField()
    {
        Assert.Same(_harness.Input.GetController(_field), _harness.Input.FocusedComponent);

        DoubleShift();

        Assert.True(_search.IsOpen.Value);
        Assert.Same(_harness.Input.GetController(QueryField()), _harness.Input.FocusedComponent);
        Assert.Equal(string.Empty, _field.Text.ToString());
    }

    [Fact]
    public void ShiftUsedForTyping_DoesNotOpenSearch()
    {
        _harness.KeyDown(KeyboardKey.LeftShift, InputModifiers.Shift);
        _harness.Type("A");
        _harness.KeyUp(KeyboardKey.LeftShift);
        _harness.KeyDown(KeyboardKey.LeftShift, InputModifiers.Shift);
        _harness.Type("B");
        _harness.KeyUp(KeyboardKey.LeftShift);

        Assert.False(_search.IsOpen.Value);
    }

    [Fact]
    public void Escape_ClosesSearch()
    {
        DoubleShift();

        _harness.PressKey(KeyboardKey.Escape);
        _harness.Layout();

        Assert.False(_search.IsOpen.Value);
    }

    [Fact]
    public void AClickOutsideTheCard_ClosesSearch()
    {
        DoubleShift();

        _harness.Click(20f, Height - 20f);
        _harness.Layout();

        Assert.False(_search.IsOpen.Value);
    }

    [Fact]
    public void AClickInsideTheCard_LeavesSearchOpen()
    {
        DoubleShift();

        // Y grows upwards: the card hangs from the top of the window.
        _harness.Click(Width / 2f, Height - 100f);
        _harness.Layout();

        Assert.True(_search.IsOpen.Value);
    }

    [Fact]
    public void TheWheelScrollsTheResults_AndNothingBehindTheBackdrop()
    {
        var repo = _registry.Active.Value!;
        var rows = Enumerable.Range(0, 60)
            .Select(i => new SymbolRow($"Item{i:00}", GitBench.Features.CodeIntel.SymbolKind.Class, null, null, "Alpha.cs",
                new GitBench.Features.Diff.FileLine(1), new GitBench.Features.Diff.RawColumn(0)))
            .ToArray();
        _index.Snapshot.Value = new SymbolIndexSnapshot(repo.Id, [rows], new SymbolIndexProgress.Complete());
        DoubleShift();
        _search.SetTab(SearchTab.Types);
        _search.SetQuery("Item");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_search.Rows.Value.Count < 60 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        _harness.Layout();
        var list = _harness.Root.SelfAndDescendants()
            .OfType<ZGF.Gui.Desktop.Components.VirtualRowList.VirtualRowListView>().Single();

        _harness.MoveTo(Width / 2f, Height - 300f);
        _harness.Scroll(0f, -5f);
        _harness.Layout();

        Assert.True(list.ScrollY > 0f, $"the list did not scroll (ScrollY {list.ScrollY})");
        Assert.True(_search.IsOpen.Value);
    }

    [Fact]
    public void Tab_SwitchesTabs_AndShiftTabGoesBack()
    {
        DoubleShift();

        _harness.PressKey(KeyboardKey.Tab);
        Assert.Equal(SearchTab.Types, _search.Tab.Value);
        _harness.PressKey(KeyboardKey.Tab, InputModifiers.Shift);
        _harness.PressKey(KeyboardKey.Tab, InputModifiers.Shift);
        Assert.Equal(SearchTab.Files, _search.Tab.Value);
    }

    [Fact]
    public void TypingGoesToTheQuery()
    {
        DoubleShift();

        _harness.Type("Alpha");

        Assert.Equal("Alpha", _search.Query.Value);
    }

    [Fact]
    public void Reopening_RestoresTheQuery_AllOfItSelected()
    {
        DoubleShift();
        _harness.Type("Alpha");
        _harness.PressKey(KeyboardKey.Escape);
        _harness.Layout();

        DoubleShift();

        var field = QueryField();
        Assert.Equal("Alpha", field.Text.ToString());
        Assert.Equal("Alpha", field.GetSelectedText());

        _harness.Type("B");
        Assert.Equal("B", _search.Query.Value);
    }

    [Fact]
    public void PrimaryT_AlsoOpensSearch_FromAFocusedTextField()
    {
        // A chord reaches the root along the pointer's path, so the pointer has to be over the window.
        _harness.MoveTo(Width / 2f, Height / 2f);
        _harness.PressKey(KeyboardKey.T, KeyGesture.Primary);
        _harness.Layout();

        Assert.True(_search.IsOpen.Value);
    }

    private sealed class NoClipboard : IClipboard
    {
        private string? _text;
        public void SetText(string text) => _text = text;
        public string? GetText() => _text;
    }
}
