using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using PngSharp.Api;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Desktop;
using ZGF.Geometry;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public sealed class RepoCustomizeIconTests : IDisposable
{
    private readonly TempDir _dir = new("repo-customize-icon-");
    private readonly RepoRegistry _registry;
    private readonly Picker _picker = new();
    private readonly string _image;

    public RepoCustomizeIconTests()
    {
        var state = Path.Combine(_dir.Path, "state.json");
        _registry = new(RepoStateStore.Load(state), state);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "repo", ".git"));
        _registry.Open(Path.Combine(_dir.Path, "repo"));
        _image = Path.Combine(_dir.Path, "icon.png");
        Png.EncodeToFile(Png.CreateRgba(1, 1, [255, 0, 0, 255]), _image);
    }

    [Fact]
    public void BrowsePreviewsImageAndSavePersistsItWithoutChangingTheFolderColor()
    {
        _registry.SetCustomColor(Repo.Id, 0xFF123456);
        var closed = false;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => closed = true };
        using var h = Mount(dialog);
        Click(h, RepoCustomizeIconDialog.BrowseId);
        Assert.Equal(Repo.Path, _picker.InitialDirectory);
        _picker.Complete(_image);
        Assert.Equal(_image, dialog.State.ImagePath.Value);
        Assert.Null(Repo.CustomIconPath);
        Assert.True(dialog.State.CanSave.Value);
        ClickSave(h);
        Assert.True(closed);
        AssertManagedIcon();
        Assert.Equal(0xFF123456u, Repo.CustomColor);
    }

    [Fact]
    public void SelectingAColorReplacesTheImageInTheDraftAndSavePersistsIt()
    {
        _registry.SetCustomIcon(Repo.Id, _image);
        var savedImage = Repo.CustomIconPath;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        Assert.Equal(savedImage, dialog.State.ImagePath.Value);
        Click(h, "color-picker-swatch-1");
        Assert.Equal(ColorPicker.Presets[1], dialog.State.Color.Color);
        Assert.Null(dialog.State.ImagePath.Value);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        ClickSave(h);
        Assert.Null(Repo.CustomIconPath);
        Assert.Equal(ColorPicker.Presets[1], Repo.CustomColor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomColorReplacesImageOnlyWhenAColorIsChosen(bool hex)
    {
        _registry.SetCustomIcon(Repo.Id, _image);
        var savedImage = Repo.CustomIconPath;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        Assert.True(h.Get(ColorPicker.AreaId).IsVisible);
        Assert.Equal(savedImage, dialog.State.ImagePath.Value);
        if (hex) dialog.State.Color.Hex.Value = "#00ff00";
        else dialog.State.Color.SetHsv(new(120, 1, 1));
        Assert.Null(dialog.State.ImagePath.Value);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        ClickSave(h);
        Assert.Null(Repo.CustomIconPath);
        Assert.Equal(0xFF00FF00u, Repo.CustomColor);
    }

    [Fact]
    public void ChoosingTheExistingColorStillRemovesImageAndCancelPreservesSavedImage()
    {
        _registry.SetCustomColor(Repo.Id, ColorPicker.Presets[0]);
        _registry.SetCustomIcon(Repo.Id, _image);
        var savedImage = Repo.CustomIconPath;
        var closed = false;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => closed = true };
        using var h = Mount(dialog);
        Click(h, "color-picker-swatch-0");
        Assert.Null(dialog.State.ImagePath.Value);
        h.PressKey(KeyboardKey.Escape);
        Assert.True(closed);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        Assert.Equal(ColorPicker.Presets[0], Repo.CustomColor);
    }

    [Fact]
    public void CancelDiscardsImageSelectionAndClosedDialogIgnoresLatePickerResult()
    {
        var closed = false;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => closed = true };
        using (var h = Mount(dialog))
        {
            Click(h, RepoCustomizeIconDialog.BrowseId);
            _picker.Complete(_image);
            Click(h, RepoCustomizeIconDialog.BrowseId);
            h.PressKey(KeyboardKey.Escape);
            Assert.True(closed);
        }
        _picker.Complete(Path.Combine(_dir.Path, "missing.png"));
        Assert.Null(dialog.State.ImageError.Value);
        Assert.Null(Repo.CustomIconPath);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "repo-icons")));
    }

    [Fact]
    public void InvalidOrDisappearingImageCannotOverwriteTheSavedIcon()
    {
        _registry.SetCustomIcon(Repo.Id, _image);
        var savedImage = Repo.CustomIconPath;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        Click(h, RepoCustomizeIconDialog.BrowseId);
        var invalid = Path.Combine(_dir.Path, "bad.png");
        File.WriteAllText(invalid, "not an image");
        _picker.Complete(invalid);
        Assert.NotNull(dialog.State.ImageError.Value);
        Assert.False(dialog.State.CanSave.Value);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        Click(h, RepoCustomizeIconDialog.BrowseId);
        _picker.Complete(_image);
        Assert.Null(dialog.State.ImageError.Value);
        File.Delete(_image);
        ClickSave(h);
        Assert.NotNull(dialog.State.ImageError.Value);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        Assert.NotNull(RepoIconImage.Load(savedImage!));
    }

    [Fact]
    public void ChoosingAnImageRestoresTheLastValidColorAfterAnIncompleteHexEdit()
    {
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        dialog.State.Color.Hex.Value = "#12";
        Click(h, RepoCustomizeIconDialog.BrowseId);
        _picker.Complete(_image);
        Assert.True(dialog.State.Color.IsValid.Value);
        Assert.True(dialog.State.CanSave.Value);
        ClickSave(h);
        AssertManagedIcon();
        Assert.Null(Repo.CustomColor);
    }

    [Fact]
    public void CustomColorIsImmediatelyAvailableAndSavesItsDraft()
    {
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        Assert.True(h.Get(ColorPicker.AreaId).IsVisible);
        Assert.True(h.Get(ColorPicker.HexId).IsVisible);
        Assert.DoesNotContain(h.Root.SelfAndDescendants(), v => v.Id is "repo-icon-color-tab" or "repo-icon-image-tab" or "repo-icon-custom-color");
        dialog.State.Color.Hex.Value = "#12ab34";
        Assert.Equal("#12ab34", dialog.State.Color.Hex.Value);
        ClickSave(h);
        Assert.Equal(0xFF12AB34u, Repo.CustomColor);
    }

    [Fact]
    public void UseDefaultResetsBothSavedImageAndFolderColorOnlyOnSave()
    {
        _registry.SetCustomColor(Repo.Id, 0xFF123456);
        _registry.SetCustomIcon(Repo.Id, _image);
        var savedImage = Repo.CustomIconPath;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => { } };
        using var h = Mount(dialog);
        Assert.DoesNotContain(h.Root.SelfAndDescendants(), v => v.Id == "repo-icon-remove-image");
        Click(h, ColorPicker.ResetId);
        Assert.Equal(savedImage, Repo.CustomIconPath);
        Assert.Null(dialog.State.Color.SelectedColor.Value);
        ClickSave(h);
        Assert.Null(Repo.CustomIconPath);
        Assert.Null(Repo.CustomColor);
    }

    [Fact]
    public void StorageFailureKeepsDialogOpenAndDoesNotChangeTheSavedIcon()
    {
        var closed = false;
        var dialog = new RepoCustomizeIconDialog { Repo = Repo, OnClose = () => closed = true };
        using var h = Mount(dialog);
        Click(h, RepoCustomizeIconDialog.BrowseId);
        _picker.Complete(_image);
        // A file in place of the directory reliably simulates a write failure on every platform.
        File.WriteAllText(Path.Combine(_dir.Path, "repo-icons"), "blocked");
        ClickSave(h);
        Assert.False(closed);
        Assert.NotNull(dialog.State.ImageError.Value);
        Assert.Null(Repo.CustomIconPath);
        Assert.True(File.Exists(_image));
    }

    [Fact]
    public void NativeWindowHostUsesModalScaledWindowAndReopensAfterClose()
    {
        var bus = new MessageBus();
        var windows = new WindowFactory();
        using (var h = GuiTestHarness.Create(ctx => new RepoIconWindowHost().BuildView(ctx), configure: ctx =>
        {
            ctx.AddService<IMessageBus>(bus);
            ctx.AddService<ISecondaryWindowFactory>(windows);
            ctx.AddService<IWindowCoordinates>(new Coordinates());
            ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
        }))
        {
            bus.Broadcast(new OpenRepoIconWindowMessage(Repo));
            Assert.Single(windows.Opened);
            Assert.True(windows.Request.IsModal);
            Assert.True(windows.Request.IsUndecorated);
            Assert.True(windows.Request.CenterOnMainWindow);
            Assert.True(windows.Request.Width >= RepoCustomizeIconDialog.DialogWidth * 1.5);
            Assert.True(windows.Request.Height >= RepoCustomizeIconDialog.DialogHeight * 1.5);
            windows.Opened[0].Close();
            bus.Broadcast(new OpenRepoIconWindowMessage(Repo));
            Assert.Equal(2, windows.Opened.Count);
        }
        Assert.True(windows.Opened[1].IsClosed);
        bus.Broadcast(new OpenRepoIconWindowMessage(Repo));
        Assert.Equal(2, windows.Opened.Count);
    }

    private sealed class WindowFactory : ISecondaryWindowFactory
    {
        public SecondaryWindowRequest Request;
        public List<SecondaryWindow> Opened { get; } = [];
        public ISecondaryWindow Open(in SecondaryWindowRequest request)
        {
            Request = request;
            var window = new SecondaryWindow();
            Opened.Add(window);
            return window;
        }
        public void Dispose() { }
    }

    private sealed class SecondaryWindow : ISecondaryWindow
    {
        public IWindow Window => throw new NotSupportedException();
        public event Action? Closed;
        public bool IsClosed { get; private set; }
        public void Close() { IsClosed = true; Closed?.Invoke(); }
    }

    private sealed class Coordinates : IWindowCoordinates
    {
        public ScreenPoint ToScreenPoints(CanvasPoint p) => new((int)(p.X * 1.5f), (int)(p.Y * 1.5f));
        public ScreenRect ToScreenPoints(CanvasRect r) => new(0, 0, (int)(r.Width * 1.5f), (int)(r.Height * 1.5f));
    }

    private Repo Repo => _registry.Repos.Single();

    private void AssertManagedIcon()
    {
        Assert.NotEqual(_image, Repo.CustomIconPath);
        Assert.Equal(Path.Combine(_dir.Path, "repo-icons"), Path.GetDirectoryName(Repo.CustomIconPath));
        Assert.NotNull(RepoIconImage.Load(Repo.CustomIconPath!));
    }

    private GuiTestHarness Mount(RepoCustomizeIconDialog dialog) => GuiTestHarness.Create(
        ctx => new Center { Child = dialog }.BuildView(ctx), width: 640, height: 760, configure: ctx =>
        {
            ctx.AddService<IRepoRegistry>(_registry);
            ctx.AddService<IFilePicker>(_picker);
            ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
            ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            ctx.AddService<IClipboard>(new Clipboard());
        });

    private static void ClickSave(GuiTestHarness h)
    {
        h.Layout();
        h.ClickOn(h.Root.SelfAndDescendants().OfType<TextView>().Single(v => v.Text == "Save"));
    }

    private static void Click(GuiTestHarness h, string id)
    {
        h.Layout();
        h.ClickOn(id);
        h.Layout();
    }

    public void Dispose() { _registry.Dispose(); _dir.Dispose(); }

    private sealed class Picker : IFilePicker
    {
        private Action<string>? _pending;
        public string? InitialDirectory { get; private set; }
        public void Complete(string path) { var callback = _pending; _pending = null; callback?.Invoke(path); }
        public void PickFile(string title, string? initialDirectory, IReadOnlyList<FileFilter>? filters, Action<string> onPicked)
        { InitialDirectory = initialDirectory; _pending = onPicked; }
        public void PickFolder(string title, string? initialDirectory, Action<string> onPicked) => throw new NotSupportedException();
        public void PickSaveFile(string title, string? initialDirectory, string? suggestedFileName, IReadOnlyList<FileFilter>? filters, Action<string> onPicked) => throw new NotSupportedException();
    }

    private sealed class Clipboard : IClipboard
    {
        public string? GetText() => null;
        public void SetText(string text) { }
    }
}
