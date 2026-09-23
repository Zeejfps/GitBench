using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Features.LocalChanges;
using GitBench.Features.Review;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Search;

/// <summary>Search everywhere over the main window, mounted only while it is open, so opening it
/// is what hands its field the caret.</summary>
internal sealed record SearchEverywhereOverlay : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var model = ctx.Require<SearchEverywhereViewModel>();
        return new Show
        {
            When = model.IsOpen,
            Then = () => new SearchEverywherePopup { Model = model },
        };
    }
}

/// <summary>
/// The popup: a card near the top of the window over a dimmed backdrop, holding the tabs, the query
/// and the results. A click on the backdrop or Esc closes it.
/// </summary>
internal sealed record SearchEverywherePopup : Widget
{
    private const float CardWidth = 720f;
    private const float CardHeight = 460f;
    private const float TopGap = 64f;
    private const int OverlayZIndex = 1000;

    public required SearchEverywhereViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var model = Model;

        var card = new Box
        {
            Width = CardWidth,
            Height = CardHeight,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            BorderRadius = BorderRadiusStyle.All(10f),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new SearchTabsBar { Model = model },
                        new SearchQueryField { Model = model },
                        new Grow
                        {
                            Child = new Stack
                            {
                                Children =
                                [
                                    new SearchResultsList { Model = model },
                                    new Show
                                    {
                                        When = model.FoundNothing,
                                        Then = () => new Center
                                        {
                                            Child = new Text
                                            {
                                                Value = L.T(s => s.FileSearchNoMatches),
                                                Color = Theme.Color(s => s.Palette.TextMuted),
                                            },
                                        },
                                    },
                                ],
                            },
                        },
                    ],
                },
            ],
        }.WithController(input, () => new CardController());

        return new Box
        {
            ZIndex = OverlayZIndex,
            Background = Prop.Bind<uint>(() => 0x80000000u),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Center,
                    Children = [new Box { Height = TopGap }, card],
                },
            ],
        }.WithController(input, () => new ScrimController(model.Close));
    }
}

/// <summary>The tabs along the top of the popup, and how far the symbol index has got.</summary>
internal sealed record SearchTabsBar : Widget
{
    private const float BarHeight = 34f;

    public required SearchEverywhereViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var strings = ctx.Localization().Strings;

        return new Box
        {
            Height = BarHeight,
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                Tab(model, SearchTab.All, LucideIcons.Search),
                                Tab(model, SearchTab.Types, LucideIcons.Box),
                                Tab(model, SearchTab.Symbols, LucideIcons.FunctionSquare),
                                Tab(model, SearchTab.Files, LucideIcons.File),
                                new Grow { Child = new Box() },
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => model.Index.Value.Progress is SymbolIndexProgress.Building(var done, var total)
                                        ? strings.Value.SearchEverywhereIndexing(done.ToString("N0"), total.ToString("N0"))
                                        : string.Empty),
                                    FontSize = FontSize.Caption,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                    VAlign = TextAlignment.Center,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget Tab(SearchEverywhereViewModel model, SearchTab tab, string icon) =>
        new UnderlineTab<SearchTab>
        {
            Icon = icon,
            Label = L.T(s => SearchRowPainter.TabTitle(s, tab)),
            Model = new SegmentViewModel<SearchTab>(model.Tab, tab),
        }.WithController<KbmController>();
}

/// <summary>The query, with the keys that drive the list below it while the caret stays here.</summary>
internal sealed record SearchQueryField : Widget
{
    private const float FieldHeight = 36f;

    public required SearchEverywhereViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var inputSystem = ctx.Require<InputSystem>();

        var field = new TextInputView(ctx.Canvas);
        field.BindThemed(ctx.Theme(), s =>
        {
            field.BackgroundColor = 0u;
            field.TextColor = s.TextInput.Text;
            field.CaretColor = s.TextInput.Caret;
            field.SelectionRectColor = s.TextInput.Selection;
            field.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        field.Bind(ctx.Localization().Strings, s => field.PlaceholderText = s.SearchEverywherePlaceholder);
        field.SetText(model.Query.Value);

        var controller = new SearchFieldController(field, inputSystem, ctx.Require<IClipboard>(), model);
        field.UseController(inputSystem, controller);
        field.Bind(field.TextValue, model.SetQuery);

        field.Use(() =>
        {
            void Focus()
            {
                controller.BeginEditing();
                field.SelectAll();
            }

            Focus();
            var subscriptions = new SubscriptionGroup();
            model.FocusRequested += Focus;
            subscriptions.Add(() => model.FocusRequested -= Focus);
            subscriptions.Add(controller.EndEditing);
            return subscriptions;
        });

        return new Box
        {
            Height = FieldHeight,
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Top = s.Palette.Border, Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Lg },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Text
                                {
                                    Value = LucideIcons.Search,
                                    FontFamily = LucideIcons.FontFamily,
                                    FontSize = FontSize.Body,
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                    VAlign = TextAlignment.Center,
                                },
                                new Grow
                                {
                                    Child = new Box
                                    {
                                        Height = 24f,
                                        Children = [new Raw { View = field }],
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// The query field's keys: Esc closes search, the arrows and page keys walk the results, Tab and
/// Shift+Tab switch tabs, Enter opens the selected result and Shift+Enter opens it in a preview tab.
/// </summary>
internal sealed class SearchFieldController : BaseTextInputKbmController
{
    private const int PageRows = 10;

    private readonly TextInputView _input;
    private readonly SearchEverywhereViewModel _model;

    public SearchFieldController(
        TextInputView input, InputSystem inputSystem, IClipboard clipboard, SearchEverywhereViewModel model)
        : base(input, inputSystem, clipboard)
    {
        _input = input;
        _model = model;
    }

    protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
    {
        var shift = (e.Modifiers & InputModifiers.Shift) != 0;
        switch (e.Key)
        {
            case KeyboardKey.Escape:
                e.Consume();
                _model.Close();
                return;
            case KeyboardKey.Enter or KeyboardKey.NumpadEnter:
                e.Consume();
                _model.Activate(shift ? OpenAs.Transient : OpenAs.Pinned);
                return;
            case KeyboardKey.Tab:
                e.Consume();
                _model.CycleTab(shift ? -1 : 1);
                return;
            case KeyboardKey.DownArrow:
                e.Consume();
                _model.Move(1);
                return;
            case KeyboardKey.UpArrow:
                e.Consume();
                _model.Move(-1);
                return;
            case KeyboardKey.PageDown:
                e.Consume();
                _model.Move(PageRows);
                return;
            case KeyboardKey.PageUp:
                e.Consume();
                _model.Move(-PageRows);
                return;
        }

        base.OnKeyboardKeyPressed(ref e);
    }

    protected override void OnFocusLostCore() => _input.StopEditing();
}
