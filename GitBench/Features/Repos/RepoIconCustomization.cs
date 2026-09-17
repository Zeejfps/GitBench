using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Git;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>Owns the image and folder-color drafts until Save.</summary>
internal sealed class RepoIconCustomization : IDisposable
{
    private bool _disposed;
    private string? _loadedPath;
    private ImageFrame? _loadedFrame;
    private readonly IDisposable _colorSubscription;
    public ColorPickerModel Color { get; }
    public State<string?> ImagePath { get; }
    public State<int> PreviewVersion { get; } = new(0);
    public Derived<bool> IsFolder { get; }
    public Derived<bool> CanRemoveImage { get; }
    public State<string?> ImageError { get; } = new(null);
    public Derived<bool> CanSave { get; }

    public RepoIconCustomization(Repo repo, uint? defaultColor = null)
    {
        Color = new(repo.CustomColor, defaultColor ?? RepoRailTile.IdentityColor(repo.Id));
        ImagePath = new(repo.CustomIconPath);
        IsFolder = new(() => ImagePath.Value is null);
        CanRemoveImage = new(() => !IsFolder.Value || ImageError.Value is not null);
        CanSave = new(() => ImageError.Value is null && Color.IsValid.Value);
        // Subscription invokes immediately; opening an existing image must not remove it.
        var ready = false;
        _colorSubscription = Color.SelectedColor.Subscribe(_ =>
        {
            if (ready && CanRemoveImage.Value) RemoveImage();
        });
        ready = true;
    }

    public void SelectImage(string path, string invalidMessage)
    {
        // Native pickers are asynchronous; their result may arrive after the dialog closes.
        if (_disposed) return;
        var frame = RepoIconImage.Load(path);
        if (frame is null)
        {
            ImageError.Value = invalidMessage;
            return;
        }
        // Choosing an image abandons any incomplete hex edit, keeping the last valid color.
        if (!Color.IsValid.Value)
        {
            if (Color.SelectedColor.Value is { } color) Color.Select(color);
            else Color.Reset();
        }
        _loadedPath = Path.GetFullPath(path);
        _loadedFrame = frame;
        ImageError.Value = null;
        ImagePath.Value = _loadedPath;
        PreviewVersion.Value++;
    }

    public void RemoveImage()
    {
        if (_disposed) return;
        ImagePath.Value = null;
        ImageError.Value = null;
        PreviewVersion.Value++;
    }

    public void SelectColor(uint color)
    {
        if (_disposed) return;
        RemoveImage();
        Color.Select(color);
    }

    public void ResetColor()
    {
        if (_disposed) return;
        RemoveImage();
        Color.Reset();
    }

    // Rebuilding the preview reuses its decoded frame. Keep this dialog-scoped
    // so closing it releases the pixels and reopening observes changes to the source file.
    public ImageFrame? LoadPreview(string path)
    {
        if (_disposed) return null;
        if (_loadedPath != path)
        {
            _loadedFrame = RepoIconImage.Load(path);
            _loadedPath = path;
        }
        return _loadedFrame;
    }

    public bool Save(IRepoRegistry registry, Guid repoId, string saveErrorMessage)
    {
        if (_disposed || !CanSave.Value) return false;
        if (!IsFolder.Value)
        {
            // Import only on Save; a failed read or write leaves the existing icon intact.
            if (!registry.SetCustomIcon(repoId, ImagePath.Value))
            {
                ImageError.Value = saveErrorMessage;
                return false;
            }
        }
        else
        {
            registry.SetCustomIcon(repoId, null);
            registry.SetCustomColor(repoId, Color.SelectedColor.Value);
        }
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _colorSubscription.Dispose();
        _loadedFrame = null;
        _loadedPath = null;
        CanSave.Dispose();
        CanRemoveImage.Dispose();
        IsFolder.Dispose();
        Color.Dispose();
    }
}
