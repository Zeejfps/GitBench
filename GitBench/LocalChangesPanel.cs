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
/// One side of the Local Changes split (Unstaged or Staged). Renders a header bar with
/// action buttons, a virtualized list of file rows, and an empty-state placeholder.
/// Selection lives on the view model (one <see cref="GitGui.Selection"/> for both
/// sides); the panel just renders rows reactively against the shared selection and
/// forwards clicks (with modifiers) to a callback that routes into the VM.
///
/// Row scroll/hit-test/wheel/double-click plumbing lives in <see cref="VirtualRowListView"/>.
/// This view owns the per-row drawing (status badge + path text), the empty-state
/// swap, and the <see cref="IScrollableContent"/> surface for the external scroll bars.
/// </summary>
internal sealed class LocalChangesPanel : MultiChildView, IScrollableContent
{
    private readonly string _title;
    private readonly DiffSide _side;
    private readonly IReadable<Selection> _selection;
    private readonly Action<FileRow, InputModifiers> _onRowClick;
    private readonly Action<FileRow>? _onRowActivated;
    private readonly Action? _onEmptyAreaClicked;
    private readonly Action<FileRow>? _onFolderToggle;
    private readonly Func<FileRow?, IReadOnlyList<RepoBarContextMenu.Item>>? _buildContextMenu;
    private readonly TextView _headerText;
    private readonly TextView _emptyPlaceholder;
    private readonly RectView _bodyContainer;
    private readonly VirtualRowListView _list;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly HorizontalScrollBarView _hScrollBar;

    private IReadOnlyList<FileChange> _files = Array.Empty<FileChange>();
    private IReadOnlyList<FileRow> _rows = Array.Empty<FileRow>();
    private FileViewMode _viewMode = FileViewMode.Flat;
    private IReadOnlySet<string> _collapsed = new HashSet<string>();
    private View _currentBody = null!;
    private string? _lastChevronTogglePath;
    private int _lastChevronToggleTick;

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
    private readonly TextStyle _chevronStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = 11f,
        HorizontalAlignment = TextAlignment.Center,
        VerticalAlignment = TextAlignment.Center,
    };
    private readonly TextStyle _folderIconStyle = new()
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = 13f,
        HorizontalAlignment = TextAlignment.Start,
        VerticalAlignment = TextAlignment.Center,
    };

    private FileChangeRowStyles _rowStyles = ThemeStyles.Dark.FileChangeRow;

    // Sentinel start so the first NotifyScrollChanged fires even when the computed scale
    // equals 1 — otherwise the scrollbar thumb's built-in 0.5 default sticks until a real
    // change forces an update. Same root cause as the fix in DiffContentView.
    private float _lastVerticalScale = -1f;
    private float _lastHorizontalScale = -1f;
    private float _lastNormalizedY;

    public IReadOnlyList<FileChange> Files => _files;

    public event Action<float>? VerticalScrollPositionChanged;
    public event Action<float>? HorizontalScrollPositionChanged;
    public float VerticalScale { get; private set; } = 1f;
    public float HorizontalScale { get; private set; } = 1f;

    public LocalChangesPanel(
        string title,
        DiffSide side,
        string emptyText,
        IReadable<Selection> selection,
        Action<FileRow, InputModifiers> onRowClick,
        IReadOnlyList<View>? headerActions = null,
        Action<FileRow>? onRowActivated = null,
        Action? onEmptyAreaClicked = null,
        Action<FileRow>? onFolderToggle = null,
        Func<FileRow?, IReadOnlyList<RepoBarContextMenu.Item>>? buildContextMenu = null)
    {
        _title = title;
        _side = side;
        _selection = selection;
        _onRowClick = onRowClick;
        _onRowActivated = onRowActivated;
        _onEmptyAreaClicked = onEmptyAreaClicked;
        _onFolderToggle = onFolderToggle;
        _buildContextMenu = buildContextMenu;

        _headerText = FileChangesUI.CreateHeaderText(title);
        _emptyPlaceholder = FileChangesUI.CreateEmptyPlaceholder(emptyText);

        View headerContent;
        if (headerActions is { Count: > 0 })
        {
            var actionRow = new FlexRowView
            {
                Gap = 2f,
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

        var headerBar = FileChangesUI.CreateHeaderBar(headerContent);

        _list = new VirtualRowListView
        {
            RowHeight = FileChangesUI.RowHeight,
            ItemBuilder = DrawFileRowAt,
        };
        _list.RowClicked += OnRowClicked;
        if (onRowActivated != null) _list.RowActivated += OnRowActivated;
        if (buildContextMenu != null) _list.RowContextRequested += OnRowContextRequested;
        _list.ScrollChanged += NotifyScrollChanged;

        // Empty placeholder swaps in as the body when there are no files; the widget swaps
        // back in when files arrive. Keeps the layout (header / center / scrollbars) intact.
        _bodyContainer = new RectView();
        _bodyContainer.Children.Add(_emptyPlaceholder);
        _currentBody = _emptyPlaceholder;

        _scrollBar = ScrollBars.CreateVertical();
        _hScrollBar = ScrollBars.CreateHorizontal();

        AddChildToSelf(new BorderLayoutView
        {
            North = headerBar,
            Center = _bodyContainer,
            East = _scrollBar,
            South = _hScrollBar,
        });

        _list.UseController(_ => new VirtualRowListController(_list));

        // Selection changes only affect row visuals; a SetDirty is enough — every frame
        // redraws and the ItemBuilder reads the current selection on demand.
        selection.Subscribe(_ => SetDirty());

        this.BindThemed(s =>
        {
            _rowStyles = s.FileChangeRow;
            _pathTextStyle.TextColor = _rowStyles.RowText;
            _pathTextActiveStyle.TextColor = _rowStyles.RowTextActive;
            _chevronStyle.TextColor = _rowStyles.RowText;
            _folderIconStyle.TextColor = _rowStyles.RowText;
            SetDirty();
        });

        this.UseBehavior(_ => new ScrollSyncController(this, _scrollBar, _hScrollBar));
    }

    public void SetFiles(IReadOnlyList<FileChange> files)
    {
        _files = files;
        _headerText.Text = FileChangesUI.FormatHeader(_title, files.Count);
        RebuildRows();
        // New data: jump back to the top rather than preserving a now-meaningless offset.
        _list.SetScrollY(0f);
        NotifyScrollChanged();
    }

    // Scrolls just enough to bring the keyboard cursor's row into view. Ignores cursors on
    // the other side (each panel renders only its own side's rows).
    public void EnsureRowVisible(FileRowRef row)
    {
        if (row.Side != _side) return;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.FullPath == row.FullPath && (r.Kind == FileRowKind.Folder) == row.IsFolder)
            {
                _list.EnsureRowVisible(i);
                return;
            }
        }
    }

    public void SetViewMode(FileViewMode mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        RebuildRows();
        NotifyScrollChanged();
    }

    public void SetCollapsed(IReadOnlySet<string> collapsed)
    {
        _collapsed = collapsed;
        RebuildRows();
        NotifyScrollChanged();
    }

    private void RebuildRows()
    {
        _rows = FileTreeBuilder.BuildRows(_files, _side, _viewMode, _collapsed);
        // Only swap the body on a real empty↔non-empty transition. Detaching and
        // re-adding the list view (e.g. on every folder collapse) churns its
        // InputSystem controller registration and drops the hover path, so clicks
        // stop landing until the cursor physically re-enters. Keeping it mounted —
        // as BranchesView does — avoids that.
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

    protected override void OnDrawSelf(ICanvas c)
    {
        // Resync every frame so layout changes (splitter drag, window resize) immediately
        // republish scale/normalized to the scrollbars. NotifyScrollChanged is dedup-protected,
        // so this is cheap when nothing actually changed.
        NotifyScrollChanged();
    }

    private void OnRowClicked(int rowIndex, InputModifiers modifiers, PointF point)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count)
        {
            _onEmptyAreaClicked?.Invoke();
            return;
        }
        var row = _rows[rowIndex];
        // The chevron toggles the folder without disturbing the selection — it consumes
        // the click before the row's select handler runs.
        if (row.Kind == FileRowKind.Folder && _onFolderToggle != null && IsChevronHit(row, point))
        {
            // RowClicked fires on every physical click, so a double-click would toggle
            // twice (a net no-op). Swallow the second click of a double on the same
            // chevron so a double-click is a single, predictable toggle.
            var now = Environment.TickCount;
            if (_lastChevronTogglePath == row.FullPath
                && unchecked(now - _lastChevronToggleTick) <= _list.DoubleClickThresholdMs)
            {
                _lastChevronTogglePath = null;
                return;
            }
            _lastChevronTogglePath = row.FullPath;
            _lastChevronToggleTick = now;
            _onFolderToggle(row);
            return;
        }
        _onRowClick(row, modifiers);
    }

    // The chevron occupies the indent + chevron column at the left of a folder row; a hit
    // anywhere from the row's left edge through the chevron toggles (so the small triangle
    // isn't a pixel-perfect target), while the icon and name select.
    private bool IsChevronHit(FileRow row, PointF point)
    {
        var chevronRight = _list.Position.Left + FileChangesUI.RowPaddingLeft
            + row.Indent + FileChangesUI.ChevronWidth + FileChangesUI.ChevronGap;
        return point.X <= chevronRight;
    }

    private void OnRowActivated(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;
        _onRowActivated?.Invoke(_rows[rowIndex]);
    }

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        if (Context == null || _buildContextMenu == null) return;

        var onRow = rowIndex >= 0 && rowIndex < _rows.Count;
        var target = onRow ? _rows[rowIndex] : null;

        var items = _buildContextMenu(target);
        if (items.Count == 0) return;

        if (onRow) _list.SetContextHighlight(rowIndex);
        var opened = RepoBarContextMenu.Show(Context, point, items);
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
        if (Context == null) return;

        var row = _rows[rowIndex];
        var selection = _selection.Value;
        var isSelected = selection.ContainsRow(row.Ref);

        if (row.Kind == FileRowKind.Folder)
        {
            FileChangesUI.DrawFolderRow(
                Context.Canvas,
                rowRect,
                row.DisplayName,
                row.Indent,
                row.IsOpen,
                isSelected,
                state.IsHovered || state.IsContextHighlighted,
                _rowStyles,
                _chevronStyle,
                _folderIconStyle,
                _pathTextStyle,
                _pathTextActiveStyle,
                z);
            return;
        }

        var file = row.File!;
        FileChangesUI.DrawFileRow(
            Context.Canvas,
            rowRect,
            file,
            isSelected,
            state.IsHovered || state.IsContextHighlighted,
            _rowStyles,
            _pathTextStyle,
            _pathTextActiveStyle,
            _statusIconStyle,
            z,
            row.DisplayName,
            row.Indent,
            reserveChevronColumn: _viewMode == FileViewMode.Tree);
    }

    // ---- IScrollableContent ----
    //
    // Horizontal scroll is intentionally inert here: the path text truncates to fit and the
    // status badge has fixed width, so the row never exceeds the viewport. We still wire
    // the bar so the layout slot stays consistent with the rest of the GitGui panels; the
    // bar collapses (PreferredHeight = 0) because Scale is always 1.

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
