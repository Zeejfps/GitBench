using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.HorizontalScrollBar;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// A titled, virtualized list of file changes used by the commit details panel. The header
/// bar sits edge-to-edge on top; rows scroll independently in a <see cref="VirtualRowListView"/>
/// below. Row drawing is delegated to <see cref="FileChangesUI.DrawFileRow"/> so the visual
/// stays in lockstep with the staged/unstaged panels in <c>LocalChangesView</c>.
///
/// Optionally selectable: pass <paramref name="selectedPath"/> + <paramref name="onRowClicked"/>
/// to make rows highlight against an external selection and dispatch clicks back. Submodule
/// pointer rows handle their own click (activate the submodule + broadcast
/// <see cref="JumpToSubmoduleCommitMessage"/>) without going through the callback.
/// </summary>
public sealed class FileChangesSection : MultiChildView, IScrollableContent
{
    private readonly string _title;
    private readonly TextView _headerText;
    private readonly TextView _emptyPlaceholder;
    private readonly RectView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly HorizontalScrollBarView _hScrollBar;
    private readonly IReadable<string?>? _selectedPath;
    private readonly Action<FileChange>? _onRowClicked;

    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();

    private readonly TextStyle _statusIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = 14f,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _pathTextStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };
    private readonly TextStyle _pathTextActiveStyle = new()
    {
        VerticalAlignment = TextAlignment.Center,
        HorizontalAlignment = TextAlignment.Start,
    };

    private FileChangeRowStyles _rowStyles = ThemeStyles.Dark.FileChangeRow;

    // Sentinel start so the first NotifyScrollChanged fires even when the computed scale
    // equals 1 — otherwise the scrollbar thumb's built-in 0.5 default sticks until a real
    // change forces an update.
    private float _lastVerticalScale = -1f;
    private float _lastHorizontalScale = -1f;
    private float _lastNormalizedY;

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;
    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    public FileChangesSection(
        string title,
        string emptyText = "(none)",
        IReadable<string?>? selectedPath = null,
        Action<FileChange>? onRowClicked = null)
    {
        _title = title;
        _selectedPath = selectedPath;
        _onRowClicked = onRowClicked;
        _headerText = FileChangesUI.CreateHeaderText(title);
        _emptyPlaceholder = FileChangesUI.CreateEmptyPlaceholder(emptyText);

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
        };
        _list.RowClicked += OnRowClicked;
        _list.ScrollChanged += NotifyScrollChanged;

        _bodyContainer = new RectView();
        _bodyContainer.Children.Add(_emptyPlaceholder);

        _scrollBar = ScrollBars.CreateVertical();
        _hScrollBar = ScrollBars.CreateHorizontal();

        AddChildToSelf(new BorderLayoutView
        {
            North = FileChangesUI.CreateHeaderBar(_headerText),
            Center = _bodyContainer,
            East = _scrollBar,
            South = _hScrollBar,
        });

        _list.UseController(_ => new VirtualRowListController(_list));

        // Selection changes only flip row visuals; a SetDirty is enough — the ItemBuilder
        // reads the current selection on demand each draw.
        _selectedPath?.Subscribe(_ => SetDirty());

        this.BindThemed(s =>
        {
            _rowStyles = s.FileChangeRow;
            _pathTextStyle.TextColor = _rowStyles.RowText;
            _pathTextActiveStyle.TextColor = _rowStyles.RowTextActive;
            SetDirty();
        });

        this.UseBehavior(_ => new ScrollSyncController(this, _scrollBar, _hScrollBar));
    }

    public void SetFiles(IReadOnlyList<FileChange> files)
    {
        _files = files;
        _headerText.Text = FileChangesUI.FormatHeader(_title, files.Count);

        _bodyContainer.Children.Clear();
        if (files.Count == 0)
        {
            _bodyContainer.Children.Add(_emptyPlaceholder);
            _list.ItemCount = 0;
        }
        else
        {
            _bodyContainer.Children.Add(_list);
            _list.ItemCount = files.Count;
        }
        _list.SetScrollY(0f);
        _list.NotifyItemsChanged();
        NotifyScrollChanged();
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        // Resync every frame so layout changes (splitter drag, window resize) immediately
        // republish scale/normalized to the scrollbars. NotifyScrollChanged is dedup-protected.
        NotifyScrollChanged();
    }

    private void DrawFileRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _files.Count) return;
        if (Context == null) return;

        var file = _files[rowIndex];
        var isSelected = _selectedPath?.Value == file.Path;
        FileChangesUI.DrawFileRow(
            Context.Canvas,
            rowRect,
            file,
            isSelected,
            state.IsHovered,
            _rowStyles,
            _pathTextStyle,
            _pathTextActiveStyle,
            _statusIconStyle,
            z);
    }

    private void OnRowClicked(int rowIndex, InputModifiers _, PointF __)
    {
        if (rowIndex < 0 || rowIndex >= _files.Count) return;
        var file = _files[rowIndex];
        if (file.Status == FileChangeStatus.Submodule && file.PointerChange is { } pc)
        {
            ActivateSubmoduleAndJump(file.Path, pc);
            return;
        }
        _onRowClicked?.Invoke(file);
    }

    // Compare-by-relative-path: a submodule's absolute path can vary across worktrees, so
    // we resolve relative to the active repo's parent and match by GetFullPath.
    private void ActivateSubmoduleAndJump(string submodulePath, SubmodulePointerChange change)
    {
        if (Context == null) return;
        var registry = Context.Get<IRepoRegistry>();
        var bus = Context.Get<IMessageBus>();
        if (registry == null) return;

        var active = registry.Active.Value;
        if (active == null) return;

        var primaryId = active.IsPrimary ? active.Id : (active.ParentRepoId ?? active.Id);
        var parentPath = active.IsPrimary
            ? active.Path
            : (FindParentPath(registry, primaryId) ?? active.Path);
        var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentPath, submodulePath));

        foreach (var r in registry.GetSubmodules(primaryId))
        {
            if (string.Equals(System.IO.Path.GetFullPath(r.Path), target, PathComparison))
            {
                if (!r.IsMissing) registry.SetActive(r.Id);
                bus?.Broadcast(new JumpToSubmoduleCommitMessage(r.Id, change.FromSha, change.ToSha));
                return;
            }
        }
    }

    private static string? FindParentPath(IRepoRegistry registry, Guid primaryId)
    {
        foreach (var r in registry.Repos)
            if (r.Id == primaryId) return r.Path;
        return null;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void SetVerticalNormalizedScrollPosition(float normalized)
    {
        var contentHeight = _files.Count * FileChangesUI.RowHeight;
        var bodyHeight = _list.Position.Height;
        var range = contentHeight - bodyHeight;
        _list.SetScrollY(range <= 0 ? 0f : Math.Clamp(normalized, 0f, 1f) * range);
    }

    public void SetHorizontalNormalizedScrollPosition(float normalized) { /* no-op */ }

    private void NotifyScrollChanged()
    {
        var contentHeight = _files.Count * FileChangesUI.RowHeight;
        var bodyHeight = _list.Position.Height;

        float vScale, normalizedY;
        if (contentHeight <= bodyHeight || bodyHeight <= 0)
        {
            vScale = 1f;
            normalizedY = 0f;
        }
        else
        {
            vScale = bodyHeight / contentHeight;
            var range = contentHeight - bodyHeight;
            normalizedY = Math.Clamp(_list.ScrollY / range, 0f, 1f);
        }

        VerticalScale = vScale;
        HorizontalScale = 1f;

        if (Math.Abs(vScale - _lastVerticalScale) > 0.0001f
            || Math.Abs(normalizedY - _lastNormalizedY) > 0.0001f)
        {
            _lastVerticalScale = vScale;
            _lastNormalizedY = normalizedY;
            VerticalScrollPositionChanged?.Invoke(normalizedY);
        }
        if (Math.Abs(1f - _lastHorizontalScale) > 0.0001f)
        {
            _lastHorizontalScale = 1f;
            HorizontalScrollPositionChanged?.Invoke(0f);
        }
    }
}
