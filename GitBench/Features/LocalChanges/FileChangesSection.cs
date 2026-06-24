using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Repos;
using GitBench.Features.Submodules;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.HorizontalScrollBar;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// A titled, virtualized list of file changes used by the commit details panel. The header
/// bar sits edge-to-edge on top; rows scroll independently in a <see cref="VirtualRowListView"/>
/// below. Row drawing is delegated to <see cref="FileChangesUI.DrawFileRow"/> so the visual
/// stays in lockstep with the staged/unstaged panels in <c>LocalChangesView</c>.
///
/// Renders either a flat list or a collapsible folder tree (see <see cref="SetViewMode"/>);
/// the row sequence is produced by the shared <see cref="FileTreeBuilder"/> so both flavors
/// match the local-changes panels. Collapse state is kept locally on the view.
///
/// Optionally selectable: pass <paramref name="selectedPath"/> + <paramref name="onRowClicked"/>
/// to make rows highlight against an external selection and dispatch clicks back. Submodule
/// pointer rows handle their own click (activate the submodule + broadcast
/// <see cref="JumpToSubmoduleCommitMessage"/>) without going through the callback.
/// </summary>
public sealed class FileChangesSection : ContainerView, IScrollableContent
{
    private readonly string _title;
    private readonly ICanvas _canvas;
    private readonly IRepoRegistry? _registry;
    private readonly IMessageBus? _bus;
    private readonly TextView _headerText;
    private readonly TextView _emptyPlaceholder;
    private readonly RectView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly HorizontalScrollBarView _hScrollBar;
    private readonly IReadable<string?>? _selectedPath;
    private readonly Action<FileChange>? _onRowClicked;

    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();
    private IReadOnlyList<FileRow> _rows = Array.Empty<FileRow>();
    private FileViewMode _viewMode = FileViewMode.Flat;
    private readonly HashSet<string> _collapsed = new();
    private View _currentBody = null!;
    private string? _lastChevronTogglePath;
    private int _lastChevronToggleTick;

    private readonly TextStyle _statusIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Default,
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
    private readonly TextStyle _chevronStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _folderIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Body,
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
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

    internal FileChangesSection(
        Context ctx,
        string title,
        string emptyText = "(none)",
        IReadable<string?>? selectedPath = null,
        Action<FileChange>? onRowClicked = null,
        IReadOnlyList<View>? headerActions = null)
    {
        _title = title;
        _canvas = ctx.Canvas;
        _registry = ctx.Get<IRepoRegistry>();
        _bus = ctx.Get<IMessageBus>();
        _selectedPath = selectedPath;
        _onRowClicked = onRowClicked;
        var input = ctx.Require<InputSystem>();
        _headerText = FileChangesUI.CreateHeaderText(ctx, title);
        _emptyPlaceholder = FileChangesUI.CreateEmptyPlaceholder(ctx, emptyText);

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
        };
        _list.RowClicked += OnRowClicked;
        _list.ScrollChanged += NotifyScrollChanged;

        _bodyContainer = new RectView();
        _bodyContainer.Children.Add(_emptyPlaceholder);
        _currentBody = _emptyPlaceholder;

        _scrollBar = ScrollBars.CreateVertical(ctx);
        _hScrollBar = ScrollBars.CreateHorizontal(ctx);

        View headerContent;
        if (headerActions is { Count: > 0 })
        {
            var actionRow = new FlexRowView
            {
                Gap = Spacing.Hair,
                CrossAxisAlignment = CrossAxisAlignment.Center,
            };
            foreach (var action in headerActions)
                actionRow.Children.Add(action);

            headerContent = new FlexRowView
            {
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children =
                {
                    new FlexItem { Grow = 1, Child = _headerText },
                    actionRow,
                },
            };
        }
        else
        {
            headerContent = _headerText;
        }

        AddChildToSelf(new BorderLayoutView
        {
            North = FileChangesUI.CreateHeaderBar(ctx, headerContent),
            Center = _bodyContainer,
            East = _scrollBar,
            South = _hScrollBar,
        });

        _list.UseController(input, () => new VirtualRowListController(_list));

        // Selection changes only flip row visuals; a SetDirty is enough — the ItemBuilder
        // reads the current selection on demand each draw.
        if (_selectedPath != null) this.Bind(_selectedPath, _ => SetDirty());

        this.BindThemed(ctx.Theme(), s =>
        {
            _rowStyles = s.FileChangeRow;
            _pathTextStyle.TextColor = _rowStyles.RowText;
            _pathTextActiveStyle.TextColor = _rowStyles.RowTextActive;
            _chevronStyle.TextColor = _rowStyles.RowText;
            _folderIconStyle.TextColor = _rowStyles.RowText;
            SetDirty();
        });

        this.Use(() => new ScrollSyncController(this, _scrollBar, _hScrollBar));
    }

    public void SetFiles(IReadOnlyList<FileChange> files)
    {
        _files = files;
        _headerText.Text = FileChangesUI.FormatHeader(_title, files.Count);
        RebuildRows();
        _list.SetScrollY(0f);
        NotifyScrollChanged();
    }

    public void SetViewMode(FileViewMode mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        RebuildRows();
        NotifyScrollChanged();
    }

    private void RebuildRows()
    {
        _rows = FileTreeBuilder.BuildRows(_files, DiffSide.Commit, _viewMode, _collapsed);
        // Only swap the body on a real empty↔non-empty transition. Re-adding the list view
        // churns its InputSystem controller registration and drops the hover path, so clicks
        // stop landing until the cursor physically re-enters — keep it mounted across collapses.
        SetBody(_rows.Count == 0 ? _emptyPlaceholder : _list);
        _list.ItemCount = _rows.Count;
        _list.NotifyItemsChanged();
    }

    private void SetBody(View body)
    {
        if (ReferenceEquals(_currentBody, body)) return;
        _bodyContainer.Children.Clear();
        _bodyContainer.Children.Add(body);
        _currentBody = body;
    }

    // Scrolls just enough to bring the row for <paramref name="path"/> into view, so arrow-key
    // navigation that moves the selection past the viewport edge follows the cursor.
    public void EnsureRowVisible(string path)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Kind == FileRowKind.File && row.FullPath == path)
            {
                _list.EnsureRowVisible(i);
                return;
            }
        }
    }

    // Next file path for an Up/Down press, over the visible file rows only (so a file hidden
    // under a collapsed folder is skipped). Null when there are no file rows.
    public string? NextFilePath(string? current, int delta)
    {
        var paths = new List<string>(_rows.Count);
        foreach (var row in _rows)
            if (row.Kind == FileRowKind.File) paths.Add(row.FullPath);
        if (paths.Count == 0) return null;

        var index = current == null ? -1 : paths.IndexOf(current);
        return paths[ListNavigation.NextIndex(paths.Count, index, delta)];
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        // Resync every frame so layout changes (splitter drag, window resize) immediately
        // republish scale/normalized to the scrollbars. NotifyScrollChanged is dedup-protected.
        NotifyScrollChanged();
    }

    private void DrawFileRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var row = _rows[rowIndex];
        if (row.Kind == FileRowKind.Folder)
        {
            FileChangesUI.DrawFolderRow(
                _canvas,
                rowRect,
                row.DisplayName,
                row.Indent,
                row.IsOpen,
                isSelected: false,
                state.IsHovered,
                _rowStyles,
                _chevronStyle,
                _folderIconStyle,
                _pathTextStyle,
                _pathTextActiveStyle,
                z,
                isRtl: IsRtl);
            return;
        }

        var file = row.File!;
        var isSelected = _selectedPath?.Value == file.Path;
        FileChangesUI.DrawFileRow(
            _canvas,
            rowRect,
            file,
            isSelected,
            state.IsHovered,
            _rowStyles,
            _pathTextStyle,
            _pathTextActiveStyle,
            _statusIconStyle,
            z,
            row.DisplayName,
            row.Indent,
            reserveChevronColumn: _viewMode == FileViewMode.Tree,
            isRtl: IsRtl);
    }

    private void OnRowClicked(int rowIndex, InputModifiers _, PointF point)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        var row = _rows[rowIndex];

        if (row.Kind == FileRowKind.Folder)
        {
            // A chevron hit and a click anywhere else on the folder row both toggle it — folders
            // aren't a diff target, so there's nothing else a folder click could mean here.
            if (IsChevronHit(row, point))
            {
                // RowClicked fires on every physical click, so a double-click would toggle twice
                // (a net no-op). Swallow the second click of a double on the same chevron.
                var now = Environment.TickCount;
                if (_lastChevronTogglePath == row.FullPath
                    && unchecked(now - _lastChevronToggleTick) <= _list.DoubleClickThresholdMs)
                {
                    _lastChevronTogglePath = null;
                    return;
                }
                _lastChevronTogglePath = row.FullPath;
                _lastChevronToggleTick = now;
            }
            ToggleFolder(row);
            return;
        }

        var file = row.File!;
        if (file.Status == FileChangeStatus.Submodule && file.PointerChange is { } pc)
        {
            ActivateSubmoduleAndJump(file.Path, pc);
            return;
        }
        _onRowClicked?.Invoke(file);
    }

    private void ToggleFolder(FileRow row)
    {
        if (!_collapsed.Remove(row.FullPath)) _collapsed.Add(row.FullPath);
        RebuildRows();
        NotifyScrollChanged();
    }

    // The chevron occupies the indent + chevron column at the left of a folder row; a hit
    // anywhere from the row's left edge through the chevron toggles (so the small triangle
    // isn't a pixel-perfect target).
    private bool IsChevronHit(FileRow row, PointF point)
    {
        var chevronRight = _list.Position.Left + FileChangesUI.RowPaddingLeft
            + row.Indent + FileChangesUI.ChevronWidth + FileChangesUI.ChevronGap;
        return IsRtl
            ? point.X >= _list.Position.Left + _list.Position.Right - chevronRight
            : point.X <= chevronRight;
    }

    // Compare-by-relative-path: a submodule's absolute path can vary across worktrees, so
    // we resolve relative to the active repo's parent and match by GetFullPath.
    private void ActivateSubmoduleAndJump(string submodulePath, SubmodulePointerChange change)
    {
        var registry = _registry;
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
                _bus?.Broadcast(new JumpToSubmoduleCommitMessage(r.Id, change.FromSha, change.ToSha));
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
        var contentHeight = _rows.Count * FileChangesUI.RowHeight;
        var bodyHeight = _list.Position.Height;
        var range = contentHeight - bodyHeight;
        _list.SetScrollY(range <= 0 ? 0f : Math.Clamp(normalized, 0f, 1f) * range);
    }

    public void SetHorizontalNormalizedScrollPosition(float normalized) { /* no-op */ }

    private void NotifyScrollChanged()
    {
        var contentHeight = _rows.Count * FileChangesUI.RowHeight;
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
