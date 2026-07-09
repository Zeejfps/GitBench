using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
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
    private readonly PaddingView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly HorizontalScrollBarView _hScrollBar;
    private readonly IReadable<string?>? _selectedPath;
    private readonly Action<FileChange>? _onRowClicked;
    private readonly Action<FileChange, PointF>? _onFileContextMenu;

    // The per-file Viewed tracker, present only in a review window's context (null elsewhere ⇒ no marks,
    // so the History pane and Local Changes stay clean). When present, rows whose (sha, path) is viewed
    // draw a trailing check and dim their label. Reflect-only: the toggle lives on the diff header.
    private readonly IReviewedFileTracker? _reviewedFiles;
    private string? _reviewSha;
    private readonly TextStyle _viewedIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();
    private IReadOnlyList<FileRow> _rows = Array.Empty<FileRow>();
    private FileViewMode _viewMode = FileViewMode.Flat;
    private readonly HashSet<string> _collapsed = new();

    // Selection is painted once as a floating bar that slides between rows (selectable sections
    // only). _selectedIndex is the resolved target row (-1 = none); the bar draws at
    // CurrentSelectionIndex(), which lerps _animFromIndex → _animToIndex by the tween.
    private readonly Tween? _selectionTween;
    private int _selectedIndex = -1;
    private float _animFromIndex;
    private float _animToIndex;
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
    private RowSelectionStyles _rowSelection = ThemeStyles.Dark.RowSelection;

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
        IReadOnlyList<View>? headerActions = null,
        Action<FileChange, PointF>? onFileContextMenu = null)
    {
        _title = title;
        _canvas = ctx.Canvas;
        _registry = ctx.Get<IRepoRegistry>();
        _bus = ctx.Get<IMessageBus>();
        _reviewedFiles = ctx.Get<IReviewedFileTracker>();
        _selectedPath = selectedPath;
        _onRowClicked = onRowClicked;
        _onFileContextMenu = onFileContextMenu;
        var input = ctx.Require<InputSystem>();
        _headerText = FileChangesUI.CreateHeaderText(ctx, title);
        _emptyPlaceholder = FileChangesUI.CreateEmptyPlaceholder(ctx, emptyText);

        // Only selectable sections animate; parks itself when settled so it adds no idle repaints.
        if (_selectedPath != null)
            _selectionTween = new Tween(ctx.Require<IFrameTicker>(), 0.18f, Easings.EaseOutCubic);

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
            SelectionOverlayBuilder = _selectionTween != null ? DrawSelectionOverlay : null,
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.RowClicked += OnRowClicked;
        if (_onFileContextMenu != null)
            _list.RowContextRequested += OnRowContextRequested;
        _list.ScrollChanged += NotifyScrollChanged;

        _bodyContainer = new PaddingView
        {
            Padding = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
        };
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

        if (_selectionTween is { } tween)
        {
            // Retarget the floating bar on selection change; repaint each tick while it slides
            // (the tween goes quiet once it lands).
            this.Bind(_selectedPath!, path => { MoveSelectionTo(IndexOfPath(path)); SetDirty(); });
            this.Bind(tween.Progress, _ => SetDirty());
            this.Use(() => tween);
        }

        this.BindThemed(ctx.Theme(), s =>
        {
            _rowStyles = s.FileChangeRow;
            _rowSelection = s.RowSelection;
            _pathTextStyle.TextColor = _rowStyles.RowText;
            _pathTextActiveStyle.TextColor = _rowSelection.Text;
            _chevronStyle.TextColor = _rowStyles.RowText;
            _folderIconStyle.TextColor = _rowStyles.RowText;
            _viewedIconStyle.TextColor = s.Status.Success;
            SetDirty();
        });

        // Repaint when a file is toggled Viewed on the diff header so its row's check/dim updates live.
        if (_reviewedFiles != null)
            this.Bind(_reviewedFiles.Revision, _ => SetDirty());

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

    // The commit whose files are listed, so the review tracker's per-(sha, path) Viewed marks key
    // correctly. Only meaningful in a review window (where a tracker is in scope); a no-op effect
    // elsewhere. The host sets it alongside the file list when a commit's details load.
    public void SetReviewSha(string? sha)
    {
        if (_reviewSha == sha) return;
        _reviewSha = sha;
        SetDirty();
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

        // Rows shifted under the selection (collapse, reload): re-resolve and park the bar there
        // without sliding — the contents moved, not the user.
        SnapSelectionToPath();
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
                _rowSelection,
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
        var reviewMode = _reviewedFiles != null && _reviewSha != null;
        var isViewed = reviewMode && _reviewedFiles!.IsViewed(file.Path);
        FileChangesUI.DrawFileRow(
            _canvas,
            rowRect,
            file,
            isSelected,
            state.IsHovered,
            _rowSelection,
            _rowStyles,
            _pathTextStyle,
            _pathTextActiveStyle,
            _statusIconStyle,
            z,
            row.DisplayName,
            row.Indent,
            reserveChevronColumn: _viewMode == FileViewMode.Tree,
            isRtl: IsRtl,
            drawSelectionBackground: _selectionTween == null,
            reserveViewedColumn: reviewMode,
            isViewed: isViewed,
            viewedIconStyle: _viewedIconStyle);
    }

    // Retargets the floating selection bar. Slides only between two real rows; first-select and
    // clear snap in place (sliding in from nowhere reads as a glitch).
    private void MoveSelectionTo(int newIndex)
    {
        if (newIndex == _selectedIndex) return;
        if (_selectedIndex >= 0 && newIndex >= 0 && _selectionTween != null)
        {
            _animFromIndex = CurrentSelectionIndex();
            _animToIndex = newIndex;
            _selectedIndex = newIndex;
            _selectionTween.Restart();
        }
        else
        {
            _selectedIndex = newIndex;
            _animFromIndex = newIndex < 0 ? 0f : newIndex;
            _animToIndex = _animFromIndex;
        }
    }

    // Re-resolves the selected row from the current path and parks the bar on it without animating.
    private void SnapSelectionToPath()
    {
        _selectedIndex = IndexOfPath(_selectedPath?.Value);
        _animFromIndex = _selectedIndex < 0 ? 0f : _selectedIndex;
        _animToIndex = _animFromIndex;
    }

    private int IndexOfPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Kind == FileRowKind.File && row.File!.Path == path) return i;
        }
        return -1;
    }

    private float CurrentSelectionIndex()
        => _selectionTween == null
            ? _selectedIndex
            : _animFromIndex + (_animToIndex - _animFromIndex) * _selectionTween.Progress.Value;

    // One selection bar for the whole list, floated below row content by VirtualRowListView so it
    // rides scroll and slides between rows. Shares the RowSelection painter so its look matches the
    // static fill the staged/unstaged panels draw.
    private void DrawSelectionOverlay(ICanvas c, RectF viewport, int z)
    {
        if (_selectedIndex < 0) return;
        var index = CurrentSelectionIndex();
        var rowTop = viewport.Top + _list.ScrollY - index * FileChangesUI.RowHeight;
        var rowRect = new RectF(viewport.Left, rowTop - FileChangesUI.RowHeight, viewport.Width, FileChangesUI.RowHeight);
        RowSelection.DrawBackground(c, rowRect, isSelected: true, isHovered: false, _rowSelection, z, isRtl: IsRtl);
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

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        var row = _rows[rowIndex];
        if (row.Kind != FileRowKind.File) return;
        _onFileContextMenu!(row.File!, point);
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
