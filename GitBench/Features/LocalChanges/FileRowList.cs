using GitBench.App;
using GitBench.Controls;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.ContextMenu;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// A titled, virtualized list of file changes — the staged/unstaged panels and the commit-details
/// file list. Rows come from <see cref="FileTreeBuilder"/> (flat or collapsible tree); selection is
/// painted from <see cref="Highlight"/> with one floating bar that slides between rows. The viewed
/// column appears when an <see cref="IReviewedFileTracker"/> is in scope.
/// </summary>
internal sealed record FileRowList : Widget
{
    public required Prop<string?> Title { get; init; }
    public required DiffSide Side { get; init; }
    public required Prop<IReadOnlyList<FileChange>> Files { get; init; }
    public required Prop<FileViewMode> ViewMode { get; init; }
    public required Prop<IReadOnlySet<string>> Collapsed { get; init; }
    public required Prop<RowHighlight> Highlight { get; init; }
    public required Prop<FileRowRef?> ScrollTo { get; init; }

    /// <summary>Paths with an index move still running in git: their rows draw dimmed with a loader.
    /// Unset, the list reads them from the <see cref="IReviewedFileTracker"/> in scope, if any.</summary>
    public Prop<IReadOnlySet<string>> Pending { get; init; }

    /// <summary>Identity of the listed content; the list scrolls back to the top when it changes.</summary>
    public required Prop<object?> ContentKey { get; init; }

    public required IWidget EmptyState { get; init; }
    public IReadOnlyList<IWidget> HeaderActions { get; init; } = [];

    /// <summary>Whether the header is the top of the content panel: it then drops its own top rule
    /// and wears the panel's fill, so the tab strip's join is the only line there.</summary>
    public bool HeadsContentPanel { get; init; }

    public required Action<FileRow, InputModifiers> OnRowClick { get; init; }
    public required Action<FileRow> OnFolderToggle { get; init; }
    public Action<FileRow>? OnRowActivated { get; init; }
    public Action? OnEmptyAreaClicked { get; init; }

    /// <summary>Opens the row's menu (null row = the space below the last row). The list keeps the
    /// row highlighted until the returned menu closes.</summary>
    public Func<FileRow?, PointF, IOpenedContextMenu?>? ContextMenu { get; init; }

    protected override View CreateView(Context ctx) => new FileRowListView(ctx, this);
}

/// <summary>What a <see cref="FileRowList"/> paints as selected: the row the floating bar sits on,
/// every row that fills, and the subset of those that wears the leading accent.</summary>
internal readonly record struct RowHighlight(
    FileRowRef? Bar,
    IReadOnlySet<FileRowRef> Selected,
    IReadOnlySet<FileRowRef> Accented)
{
    private static readonly IReadOnlySet<FileRowRef> None = new HashSet<FileRowRef>();

    public static readonly RowHighlight Empty = new(null, None, None);
}

internal sealed class FileRowListView : ContainerView
{
    private readonly FileRowList _w;
    private readonly ICanvas _canvas;
    private readonly IReviewedFileTracker? _reviewedFiles;
    private readonly SpinnerAnimation _pendingSpinner;
    private IReadOnlySet<string> _pending = new HashSet<string>();
    private readonly TextView _headerText;
    private readonly View _emptyPlaceholder;
    private readonly PaddingView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly ListSelectionBar _selectionBar;

    private string _title = string.Empty;
    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();
    private IReadOnlyList<FileRow> _rows = Array.Empty<FileRow>();
    private FileViewMode _viewMode = FileViewMode.Flat;
    private IReadOnlySet<string> _collapsed = new HashSet<string>();
    private RowHighlight _highlight = RowHighlight.Empty;
    private View _currentBody;
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
    private readonly TextStyle _viewedIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };

    private FileChangeRowStyles _rowStyles = ThemeStyles.Dark.FileChangeRow;
    private RowSelectionStyles _rowSelection = ThemeStyles.Dark.RowSelection;

    public FileRowListView(Context ctx, FileRowList w)
    {
        _w = w;
        _canvas = ctx.Canvas;
        _reviewedFiles = ctx.Get<IReviewedFileTracker>();
        var input = ctx.Require<InputSystem>();

        _headerText = FileChangesUI.CreateHeaderText(ctx, string.Empty);
        _emptyPlaceholder = w.EmptyState.BuildView(ctx);
        _selectionBar = new ListSelectionBar(ctx.Require<IFrameTicker>());
        _pendingSpinner = new SpinnerAnimation(ctx.Require<IFrameTicker>());

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
            SelectionOverlayBuilder = (c, viewport, z) =>
                _selectionBar.Draw(c, viewport, _list.ScrollY, FileChangesUI.RowHeight, _rowSelection, z, IsRtl),
            ScrollWheelStep = Scrolling.WheelStep,
        };
        _list.RowClicked += OnRowClicked;
        if (w.OnRowActivated != null) _list.RowActivated += OnRowActivated;
        if (w.ContextMenu != null) _list.RowContextRequested += OnRowContextRequested;

        _bodyContainer = new PaddingView
        {
            Padding = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = Spacing.Sm },
        };
        _bodyContainer.Children.Add(_emptyPlaceholder);
        _currentBody = _emptyPlaceholder;

        var scrollBar = ScrollBars.CreateVertical(ctx);

        var headerContent = FileChangesUI.CreateHeaderContent(
            _headerText, [.. w.HeaderActions.Select(a => a.BuildView(ctx))]);

        AddChildToSelf(new BorderLayoutView
        {
            North = w.HeadsContentPanel
                ? FileChangesUI.CreateHeaderBar(
                    ctx, headerContent, topBorder: false, background: RepoContentTabs.Content)
                : FileChangesUI.CreateHeaderBar(ctx, headerContent),
            Center = _bodyContainer,
            East = scrollBar,
        });

        _list.UseController(input, () => new VirtualRowListController(_list));
        this.Use(() => new ScrollSyncController(_list, scrollBar));

        this.Bind(_selectionBar.Progress, _ => SetDirty());
        this.Use(() => _selectionBar);
        this.Bind(_pendingSpinner.Rotation, _ => SetDirty());
        this.Use(() => _pendingSpinner);
        if (_reviewedFiles != null)
            this.Bind(_reviewedFiles.Revision, _ => SetDirty());
        if (w.Pending.IsSet)
            w.Pending.Apply(ctx, this, static (v, pending) => v.SetPending(pending));
        else if (_reviewedFiles != null)
            this.Bind(_reviewedFiles.InFlight, SetPending);

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

        w.Title.Apply(ctx, this, static (v, t) => { v._title = t ?? string.Empty; v.UpdateHeaderText(); });
        w.Files.Apply(ctx, this, static (v, files) =>
        {
            v._files = files;
            v.UpdateHeaderText();
            v.RebuildRows();
            v.SyncPendingSpinner();
        });
        w.ViewMode.Apply(ctx, this, static (v, mode) =>
        {
            if (v._viewMode == mode) return;
            v._viewMode = mode;
            v.RebuildRows();
        });
        w.Collapsed.Apply(ctx, this, static (v, collapsed) => { v._collapsed = collapsed; v.RebuildRows(); });
        w.Highlight.Apply(ctx, this, static (v, h) =>
        {
            v._highlight = h;
            v._selectionBar.MoveTo(v.IndexOf(h.Bar));
            v.SetDirty();
        });
        w.ScrollTo.Apply(ctx, this, static (v, target) =>
        {
            var index = v.IndexOf(target);
            if (index >= 0) v._list.EnsureRowVisible(index);
        });
        w.ContentKey.Apply(ctx, this, static (v, _) => v._list.SetScrollY(0f));
    }

    private void UpdateHeaderText() =>
        _headerText.Text = FileChangesUI.FormatHeader(_title, _files.Count);

    private void RebuildRows()
    {
        _rows = FileTreeBuilder.BuildRows(_files, _w.Side, _viewMode, _collapsed);
        // Only swap the body on a real empty↔non-empty transition: re-adding the list view churns its
        // InputSystem controller registration and drops the hover path, so clicks stop landing until
        // the cursor physically re-enters.
        SetBody(_rows.Count == 0 ? _emptyPlaceholder : _list);
        _list.ItemCount = _rows.Count;
        _list.NotifyItemsChanged();
        _selectionBar.Snap(IndexOf(_highlight.Bar));
    }

    private void SetBody(View body)
    {
        if (ReferenceEquals(_currentBody, body)) return;
        _bodyContainer.Children.Clear();
        _bodyContainer.Children.Add(body);
        _currentBody = body;
    }

    private int IndexOf(FileRowRef? target)
    {
        if (target is not { } t) return -1;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Ref.Equals(t)) return i;
        return -1;
    }

    private void OnRowClicked(int rowIndex, InputModifiers modifiers, PointF point)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            _w.OnEmptyAreaClicked?.Invoke();
            return;
        }
        var row = _rows[rowIndex];
        // The chevron folds the folder without disturbing the selection; a click anywhere else on
        // the row selects it.
        if (row is FileRow.Folder && IsChevronHit(row, point))
        {
            // RowClicked fires on every physical click, so a double-click would toggle twice (a net
            // no-op). Swallow the second click of a double on the same chevron.
            var now = Environment.TickCount;
            if (_lastChevronTogglePath == row.FullPath
                && unchecked(now - _lastChevronToggleTick) <= _list.DoubleClickThresholdMs)
            {
                _lastChevronTogglePath = null;
                return;
            }
            _lastChevronTogglePath = row.FullPath;
            _lastChevronToggleTick = now;
            _w.OnFolderToggle(row);
            return;
        }
        _w.OnRowClick(row, modifiers);
    }

    // A hit anywhere from the row's left edge through the chevron column toggles, so the small
    // triangle isn't a pixel-perfect target. The column mirrors to the right edge under RTL.
    private bool IsChevronHit(FileRow row, PointF point)
    {
        var chevronRight = _list.Position.Left + FileChangesUI.RowPaddingLeft
            + row.Indent + FileChangesUI.ChevronWidth + FileChangesUI.ChevronGap;
        return IsRtl
            ? point.X >= _list.Position.Left + _list.Position.Right - chevronRight
            : point.X <= chevronRight;
    }

    private void OnRowActivated(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        _w.OnRowActivated?.Invoke(_rows[rowIndex]);
    }

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        if (_w.ContextMenu == null) return;
        var onRow = rowIndex >= 0 && rowIndex < _rows.Count;
        if (onRow) _list.SetContextHighlight(rowIndex);
        var opened = _w.ContextMenu(onRow ? _rows[rowIndex] : null, point);
        if (opened == null)
        {
            _list.SetContextHighlight(null);
            return;
        }
        opened.Closed += () => _list.SetContextHighlight(null);
    }

    private void DrawFileRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        var row = _rows[rowIndex];
        var isSelected = _highlight.Selected.Contains(row.Ref);
        var isHovered = state.IsHovered || state.IsContextHighlighted;
        // The floating bar owns its row's fill; every other selected row paints its own.
        var floatsBar = rowIndex == _selectionBar.Index;

        if (row is FileRow.Folder folder)
        {
            FileChangesUI.DrawFolderRow(
                _canvas,
                rowRect,
                row.DisplayName,
                row.Indent,
                folder.IsOpen,
                isSelected,
                isHovered,
                _rowSelection,
                _chevronStyle,
                _folderIconStyle,
                _pathTextStyle,
                _pathTextActiveStyle,
                z,
                isRtl: IsRtl,
                drawSelectionBackground: !floatsBar,
                guides: row.Guides);
            return;
        }

        if (row is not FileRow.File { Change: var file }) return;
        FileChangesUI.DrawFileRow(
            _canvas,
            rowRect,
            file,
            isSelected,
            isHovered,
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
            drawSelectionBackground: !floatsBar,
            reserveViewedColumn: _reviewedFiles != null,
            isViewed: _reviewedFiles is { } tracker && tracker.IsViewed(file.Path),
            viewedIconStyle: _viewedIconStyle,
            drawSelectionAccent: _highlight.Accented.Contains(row.Ref),
            guides: row.Guides,
            isPending: _pending.Count > 0 && _pending.Contains(file.Path),
            pendingRotation: _pendingSpinner.Rotation.Value);
    }

    private void SetPending(IReadOnlySet<string> pending)
    {
        _pending = pending;
        SyncPendingSpinner();
        SetDirty();
    }

    private void SyncPendingSpinner()
    {
        if (HasPendingRow()) _pendingSpinner.Start();
        else _pendingSpinner.Stop();
    }

    private bool HasPendingRow()
    {
        if (_pending.Count == 0) return false;
        foreach (var f in _files)
            if (_pending.Contains(f.Path)) return true;
        return false;
    }
}
