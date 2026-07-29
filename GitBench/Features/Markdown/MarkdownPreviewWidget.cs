using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Markdown;

/// <summary>
/// The dev-only markdown preview surface (Step 8, docs/plans/markdown-renderer.md): the whole
/// window becomes <see cref="MarkdownPreviewFixture"/> rendered through the streaming path —
/// a <see cref="MarkdownBlockList"/> seeded via <c>SetText</c> and bound by
/// <see cref="MarkdownStream"/>, the exact integration the assistant transcript uses — inside the
/// shared dialog scroll region so <c>/verify</c> can reach every construct. Shown only when
/// <see cref="EnvVar"/> is <c>1</c> at launch (<see cref="App.AppWidget"/> gates on
/// <see cref="IsEnabled"/>); without the variable the app composes exactly as before and nothing
/// here is reachable. The sun/moon button flips the app's normal theme state so both palettes can
/// be regressed in one run; icon-only, so the surface adds no user-facing strings.
/// </summary>
internal sealed record MarkdownPreviewWidget : Widget
{
    public const string EnvVar = "DIFFDINO_MARKDOWN_PREVIEW";

    /// <summary>Read at composition time, once per build: the preview is a launch-time mode, not a
    /// runtime toggle.</summary>
    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnvVar) == "1";

    protected override IWidget Build(Context ctx)
    {
        // Seeded through the streaming model rather than a static MarkdownWidget: this is the
        // hand-off shape (MarkdownBlockList + Each-bound MarkdownStream), so the preview exercises
        // the same runtime path the assistant's streamed turns will.
        var list = new MarkdownBlockList(new BasicMarkdownParser(), ctx.Require<IFrameTicker>());
        list.SetText(MarkdownPreviewFixture.Text);

        var themeMode = ctx.Require<State<ThemeMode>>();

        return new Box
        {
            Background = Theme.Color(s => s.Palette.Surface),
            Children =
            [
                new Column
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Padding
                        {
                            Amount = new PaddingStyle
                            {
                                Left = Spacing.Lg, Right = Spacing.Lg,
                                Top = Spacing.Sm, Bottom = Spacing.Sm,
                            },
                            Children = [ThemeToggleRow(themeMode)],
                        },
                        new Grow
                        {
                            Child = new DialogScrollRegion
                            {
                                Content = new Padding
                                {
                                    Amount = new PaddingStyle
                                    {
                                        Left = Spacing.Xl, Right = Spacing.Xl,
                                        Top = Spacing.Md, Bottom = Spacing.Xl,
                                    },
                                    Children = [new MarkdownStream { Source = list }],
                                },
                            },
                        },
                    ],
                },
            ],
        };
    }

    // The same flip the status bar's toggle performs, minus its view model (whose repo
    // dependencies the preview deliberately never composes).
    private static IWidget ThemeToggleRow(State<ThemeMode> themeMode) => new Row
    {
        MainAxis = MainAxisAlignment.End,
        Children =
        [
            new IconButtonWidget
            {
                Icon = Prop.Bind<string?>(() =>
                    themeMode.Value == ThemeMode.Dark ? LucideIcons.Sun : LucideIcons.Moon),
                IconSize = 15f,
                Width = 24,
                Height = 24,
                Command = new Command(() => themeMode.Value =
                    themeMode.Value == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark),
                Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
                Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
            }.WithController<KbmController>(),
        ],
    };
}
