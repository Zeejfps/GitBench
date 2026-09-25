using GitBench.Controls;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// A field that is the first thing a dialog asks for takes the caret as it appears, so the dialog
/// opened from a toolbar button can be typed into without a click.
/// </summary>
public sealed class GrowingDescriptionFieldFocusTests
{
    private const string FieldId = "field";

    [Fact]
    public void AnAutoFocusedField_TakesTypingAsSoonAsItAppears()
    {
        using var harness = Create(autoFocus: true);
        harness.Render();

        harness.Type("fix the bug");

        Assert.Equal("fix the bug", Field(harness).Text.ToString());
    }

    [Fact]
    public void AFieldWithoutAutoFocus_WaitsForAClick()
    {
        using var harness = Create(autoFocus: false);
        harness.Render();

        harness.Type("fix the bug");

        Assert.Equal(string.Empty, Field(harness).Text.ToString());
    }

    private static GrowingDescriptionField Field(GuiTestHarness harness) =>
        (GrowingDescriptionField)harness.Get(FieldId);

    private static GuiTestHarness Create(bool autoFocus) => GuiTestHarness.Create(
        ctx => new GrowingDescriptionField(ctx, 72f, 200f) { Id = FieldId, AutoFocus = autoFocus },
        configure: ctx =>
        {
            ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
            ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            ctx.AddService<IClipboard>(new FakeClipboard());
        });
}
