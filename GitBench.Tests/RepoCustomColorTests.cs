using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public sealed class RepoCustomColorTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-repo-color-");
    private string StatePath => Path.Combine(_dir.Path, "state.json");
    private RepoRegistry NewRegistry() => new(RepoStateStore.Load(StatePath), StatePath);
    private string NewRepo()
    {
        var path = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    [Fact]
    public void CustomColorPersistsClearsAndCoexistsWithCustomIcon()
    {
        Guid id;
        using (var registry = NewRegistry())
        {
            registry.Open(NewRepo());
            id = registry.Active.Value!.Id;
            var image = Path.Combine(_dir.Path, "icon.png");
            PngSharp.Api.Png.EncodeToFile(PngSharp.Api.Png.CreateRgba(1, 1, [255, 0, 0, 255]), image);
            Assert.True(registry.SetCustomIcon(id, image));
            registry.SetCustomColor(id, 0x00123456);
            Assert.Equal(0xFF123456u, registry.Repos.Single().CustomColor);
        }
        using (var registry = NewRegistry())
        {
            Assert.Equal(0xFF123456u, registry.Repos.Single().CustomColor);
            Assert.NotNull(registry.Repos.Single().CustomIconPath);
            registry.SetCustomColor(id, null);
        }
        using var restored = NewRegistry();
        Assert.Null(restored.Repos.Single().CustomColor);
        Assert.NotNull(restored.Repos.Single().CustomIconPath);
    }

    [Fact]
    public void OldStateWithoutColorStillLoads()
    {
        using (var registry = NewRegistry()) registry.Open(NewRepo());
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StatePath))!;
        json["repos"]![0]!.AsObject().Remove("customColor");
        File.WriteAllText(StatePath, json.ToJsonString());
        using var restored = NewRegistry();
        Assert.Null(restored.Repos.Single().CustomColor);
    }

    [Fact]
    public void DialogOnlySavesValidDraftAndCancelDoesNotChangeRepository()
    {
        using var registry = NewRegistry();
        registry.Open(NewRepo());
        var repo = registry.Repos.Single();
        var closed = false;
        var dialog = new RepoCustomizeIconDialog { Repo = repo, OnClose = () => closed = true };
        using (var h = Mount(dialog, registry))
        {
            h.ClickOn("color-picker-swatch-0");
            Assert.Null(registry.Repos.Single().CustomColor);
            h.PressKey(KeyboardKey.Escape);
            Assert.True(closed);
            Assert.Null(registry.Repos.Single().CustomColor);
        }
        closed = false;
        dialog = new RepoCustomizeIconDialog { Repo = repo, OnClose = () => closed = true };
        using (var h = Mount(dialog, registry))
        {
            dialog.State.Color.Hex.Value = "#12";
            ClickSave(h);
            Assert.False(closed);
            Assert.Null(registry.Repos.Single().CustomColor);
            dialog.State.Color.Hex.Value = "#123456";
            ClickSave(h);
            Assert.True(closed);
            Assert.Equal(0xFF123456u, registry.Repos.Single().CustomColor);
        }
        dialog = new RepoCustomizeIconDialog { Repo = registry.Repos.Single(), OnClose = () => { } };
        using (var h = Mount(dialog, registry))
        {
            h.ClickOn(ColorPicker.ResetId);
            Assert.Equal(0xFF123456u, registry.Repos.Single().CustomColor);
            ClickSave(h);
            Assert.Null(registry.Repos.Single().CustomColor);
        }
    }

    private static GuiTestHarness Mount(RepoCustomizeIconDialog dialog, RepoRegistry registry)
    {
        var h = GuiTestHarness.Create(ctx => new Center { Child = dialog }.BuildView(ctx), width: 640, height: 700,
            configure: ctx =>
            {
                ctx.AddService<IRepoRegistry>(registry);
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IClipboard>(new Clipboard());
            });
        h.Layout();
        return h;
    }

    private static void ClickSave(GuiTestHarness h)
    {
        h.Layout();
        h.ClickOn(h.Root.SelfAndDescendants().OfType<TextView>().Single(v => v.Text == "Save"));
    }

    public void Dispose() => _dir.Dispose();
    private sealed class Clipboard : IClipboard
    {
        public string? GetText() => null;
        public void SetText(string text) { }
    }
}
