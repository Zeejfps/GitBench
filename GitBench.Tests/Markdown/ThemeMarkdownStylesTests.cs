using System.Reflection;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Theming;
using Xunit;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;

namespace GitBench.Tests.Markdown;

// Pins the MarkdownStyles theme slot: both palettes must define real (non-zero) values, the
// surface-ish slots must actually differ between dark and light, text-bearing slots must be
// opaque, and widgets must read the ACTIVE palette — including re-rendering when the theme mode
// flips at runtime. The all-zero placeholders in ThemeStyles.Markdown.cs keep these red until
// the implementer fills both palettes in.
public class ThemeMarkdownStylesTests
{
    private static IReadOnlyList<PropertyInfo> ColorSlots { get; } =
        typeof(MarkdownStyles).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(uint))
            .ToList();

    private static uint Slot(MarkdownStyles styles, PropertyInfo p) => (uint)p.GetValue(styles)!;

    [Fact]
    public void MarkdownStylesExposesTheExpectedColorSlots()
    {
        // The slot set Step 5 renders from. Step 6 may add table slots; it must not remove these.
        var names = ColorSlots.Select(p => p.Name).ToHashSet();
        Assert.Superset(
            new HashSet<string>
            {
                "Link", "LinkHover", "CodeChipText", "CodeChipBackground",
                "CodeBlockBackground", "CodeBlockBorder", "CodeBlockText",
                "QuoteBar", "QuoteText", "Rule",
            },
            names);
    }

    [Fact]
    public void DarkPaletteDefinesANonZeroValueForEverySlot()
    {
        Assert.All(ColorSlots, p =>
            Assert.True(Slot(ThemeStyles.Dark.Markdown, p) != 0u,
                $"Dark.Markdown.{p.Name} is still the all-zero placeholder"));
    }

    [Fact]
    public void LightPaletteDefinesANonZeroValueForEverySlot()
    {
        Assert.All(ColorSlots, p =>
            Assert.True(Slot(ThemeStyles.Light.Markdown, p) != 0u,
                $"Light.Markdown.{p.Name} is still the all-zero placeholder"));
    }

    [Theory]
    [InlineData("CodeBlockBackground")]
    [InlineData("CodeChipBackground")]
    [InlineData("Link")]
    public void SurfaceAndAccentSlotsDifferBetweenDarkAndLight(string slot)
    {
        var p = ColorSlots.Single(x => x.Name == slot);

        Assert.NotEqual(Slot(ThemeStyles.Dark.Markdown, p), Slot(ThemeStyles.Light.Markdown, p));
    }

    [Theory]
    [InlineData("Link")]
    [InlineData("CodeChipText")]
    [InlineData("CodeBlockText")]
    [InlineData("QuoteText")]
    public void TextBearingSlotsAreFullyOpaqueInBothPalettes(string slot)
    {
        var p = ColorSlots.Single(x => x.Name == slot);

        Assert.Equal(0xFFu, Slot(ThemeStyles.Dark.Markdown, p) >> 24);
        Assert.Equal(0xFFu, Slot(ThemeStyles.Light.Markdown, p) >> 24);
    }

    // ---------- widgets read the active palette ----------

    private static (GuiTestHarness Harness, State<ThemeMode> Mode) CreateCodeBlockHarness(ThemeMode mode)
    {
        var themeMode = new State<ThemeMode>(mode);
        var harness = GuiTestHarness.Create(
            ctx => new MarkdownWidget
            {
                Document = new BasicMarkdownParser().Parse("```\ncontent line\n```"),
            }.BuildView(ctx),
            800, 600,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(themeMode));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
            });
        return (harness, themeMode);
    }

    private static bool HasBackground(RecordingCanvas canvas, uint color) =>
        canvas.Rects.Any(r => r.Inputs.Style.BackgroundColor == color);

    [Fact]
    public void WidgetBuiltUnderTheLightThemeUsesTheLightSlots()
    {
        var (h, _) = CreateCodeBlockHarness(ThemeMode.Light);
        using (h)
        {
            var canvas = h.Render();

            Assert.True(HasBackground(canvas, ThemeStyles.Light.Markdown.CodeBlockBackground),
                "the code block box must be filled with the LIGHT palette's CodeBlockBackground");
        }
    }

    [Fact]
    public void WidgetBuiltUnderTheDarkThemeUsesTheDarkSlots()
    {
        var (h, _) = CreateCodeBlockHarness(ThemeMode.Dark);
        using (h)
        {
            var canvas = h.Render();

            Assert.True(HasBackground(canvas, ThemeStyles.Dark.Markdown.CodeBlockBackground),
                "the code block box must be filled with the DARK palette's CodeBlockBackground");
        }
    }

    [Fact]
    public void FlippingTheThemeModeRestylesAnAlreadyBuiltWidget()
    {
        var (h, mode) = CreateCodeBlockHarness(ThemeMode.Dark);
        using (h)
        {
            Assert.True(HasBackground(h.Render(), ThemeStyles.Dark.Markdown.CodeBlockBackground));

            mode.Value = ThemeMode.Light;

            var canvas = h.Render();
            Assert.True(HasBackground(canvas, ThemeStyles.Light.Markdown.CodeBlockBackground),
                "after a live theme switch the box must repaint in the light palette");
            Assert.False(HasBackground(canvas, ThemeStyles.Dark.Markdown.CodeBlockBackground),
                "the dark fill must be gone after the switch");
        }
    }
}
