using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class CommitsView : MultiChildView, IBind<CommitsViewModel>
{
    private const float HeaderHeight = 28f;
    private const float RowHeight = 26f;
    private const float ColumnGap = 12f;

    private const float SummaryColumnWidth = 0f;
    private const float DefaultAuthorColumnWidth = 140f;
    private const float DefaultHashColumnWidth = 80f;
    private const float DefaultDateColumnWidth = 80f;
    private const float MinColumnWidth = 40f;
    private const float MaxColumnWidth = 600f;
    private const float MinSummaryWidth = 180f;
    private const float DividerThickness = 1f;
    internal const float DividerHitWidth = 6f;
    private const float BadgePaddingX = 6f;
    private const float BadgeHeight = 16f;
    private const float BadgeGap = 4f;

    private float _authorColumnWidth = DefaultAuthorColumnWidth;
    private float _hashColumnWidth = DefaultHashColumnWidth;
    private float _dateColumnWidth = DefaultDateColumnWidth;
    private DividerKind _hoveredDivider = DividerKind.None;

    private CommitsRenderState _renderState = new CommitsRenderState.NoRepo();
    private CommitSnapshot? _snapshot;
    private CommitsViewModel? _vm;

    private readonly VirtualRowListView _list;

    private float _lastNormalizedScroll;
    private float _lastScale = 1f;
    private string? _selectedSha;
    private bool _truncated;

    public event Action<float>? ScrollPositionChanged;
    public event Action<float>? ScaleChanged;

    private readonly TextStyle _rowTextStyle = TextStyles.Row(0u);
    private readonly TextStyle _rowTextActiveStyle = TextStyles.Row(0u);
    private readonly TextStyle _headerTextStyle = TextStyles.Row(0u);
    private readonly TextStyle _placeholderStyle = TextStyles.Centered(0u);
    private readonly TextStyle _badgeTextStyle = TextStyles.Row(0u);
    private readonly TextStyle _badgeIconStyle = TextStyles.Icon(0u, 12f);
    private readonly TextStyle _hashTextStyle = TextStyles.Row(0u);
    private readonly TextStyle _hashTextActiveStyle = TextStyles.Row(0u);

    private CommitsViewStyles _styles = ThemeStyles.Dark.CommitsView;

    public CommitsView()
    {
        _list = new VirtualRowListView
        {
            RowHeight = RowHeight,
            ItemBuilder = DrawCommitRowAt,
        };
        _list.RowClicked += OnRowClicked;
        _list.RowContextRequested += OnRowContextRequested;
        _list.ScrollChanged += NotifyScrollChanged;

        AddChildToSelf(_list);
        _list.UseController(_ => new VirtualRowListController(_list));

        this.BindThemed(s =>
        {
            _styles = s.CommitsView;
            _rowTextStyle.TextColor = _styles.RowText;
            _rowTextActiveStyle.TextColor = _styles.RowTextActive;
            _headerTextStyle.TextColor = _styles.HeaderText;
            _placeholderStyle.TextColor = _styles.PlaceholderText;
            _badgeTextStyle.TextColor = _styles.BadgeText;
            _badgeIconStyle.TextColor = _styles.BadgeText;
            _hashTextStyle.TextColor = _styles.RowTextDim;
            _hashTextActiveStyle.TextColor = _styles.RowTextActive;
            SetDirty();
        });

        this.UseController(ctx => new CommitsViewController(this, ctx));
        this.UseViewModel(this);
    }

    public void Bind(CommitsViewModel vm)
    {
        _vm = vm;
        vm.Render.Subscribe(SetRenderState);
        vm.SelectedSha.Subscribe(SetSelectedSha);
    }

    protected override void OnLayoutChild(in RectF position, View child)
    {
        if (child == _list)
        {
            var bodyHeight = Math.Max(0f, position.Height - HeaderHeight);
            child.LeftConstraint = position.Left;
            child.BottomConstraint = position.Bottom;
            child.WidthConstraint = position.Width;
            child.HeightConstraint = bodyHeight;
            child.LayoutSelf();
            return;
        }
        base.OnLayoutChild(in position, child);
    }

    private void SetRenderState(CommitsRenderState vm)
    {
        var newSnap = (vm as CommitsRenderState.Loaded)?.Snapshot;
        var prevSnap = _snapshot;
        // Preserve scroll only across snapshot-for-same-repo transitions (soft refresh).
        // Any other transition — first load, repo switch, loading/error placeholders —
        // resets to the top.
        var preserveScroll = newSnap != null && prevSnap != null && newSnap.RepoId == prevSnap.RepoId;

        _renderState = vm;
        _snapshot = newSnap;

        _list.ItemCount = newSnap?.Commits.Count ?? 0;
        _list.NotifyItemsChanged();
        if (!preserveScroll) _list.SetScrollY(0f);

        NotifyScrollChanged();

        var newTruncated = newSnap?.Truncated == true;
        if (newTruncated != _truncated)
        {
            _truncated = newTruncated;
            TruncatedChanged?.Invoke(newTruncated);
        }

        SetDirty();
    }

    private void SetSelectedSha(string? sha)
    {
        if (_selectedSha == sha) return;
        _selectedSha = sha;
        ScrollShaIntoView(sha);
        SetDirty();
    }

    private void ScrollShaIntoView(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return;
        var snap = _snapshot;
        if (snap == null) return;

        var idx = -1;
        for (var i = 0; i < snap.Commits.Count; i++)
        {
            if (snap.Commits[i].Sha == sha) { idx = i; break; }
        }
        if (idx < 0) return;
        _list.EnsureRowVisible(idx);
    }

    public bool Truncated => _truncated;
    public event Action<bool>? TruncatedChanged;

    public float Scale
    {
        get
        {
            var snap = _snapshot;
            if (snap == null || snap.Commits.Count == 0) return 1f;
            var bodyHeight = _list.Position.Height;
            if (bodyHeight <= 0) return 1f;
            var contentHeight = snap.Commits.Count * RowHeight;
            if (contentHeight <= bodyHeight) return 1f;
            return bodyHeight / contentHeight;
        }
    }

    public void SetNormalizedScrollPosition(float normalized)
    {
        var snap = _snapshot;
        if (snap == null) return;
        var bodyHeight = _list.Position.Height;
        var contentHeight = snap.Commits.Count * RowHeight;
        var maxScroll = Math.Max(0f, contentHeight - bodyHeight);
        var newScroll = maxScroll * Math.Clamp(normalized, 0f, 1f);
        _list.SetScrollY(newScroll);
    }

    private void NotifyScrollChanged()
    {
        var snap = _snapshot;
        float normalized = 0f;
        float scale = 1f;
        if (snap != null && snap.Commits.Count > 0)
        {
            var bodyHeight = _list.Position.Height;
            var contentHeight = snap.Commits.Count * RowHeight;
            var maxScroll = Math.Max(0f, contentHeight - bodyHeight);
            if (bodyHeight > 0 && maxScroll > 0)
            {
                scale = bodyHeight / contentHeight;
                normalized = Math.Clamp(_list.ScrollY / maxScroll, 0f, 1f);
            }
        }

        if (Math.Abs(scale - _lastScale) > 0.0001f)
        {
            _lastScale = scale;
            ScaleChanged?.Invoke(scale);
        }

        if (Math.Abs(normalized - _lastNormalizedScroll) > 0.0001f)
        {
            _lastNormalizedScroll = normalized;
            ScrollPositionChanged?.Invoke(normalized);
        }
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();

        c.PushClip(pos);
        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = new RectStyle { BackgroundColor = _styles.Background },
            ZIndex = z,
        });

        DrawHeader(c, pos, z + 1);

        var bodyRect = _list.Position;

        // Placeholder text lives in the parent because the widget is row-only.
        // When ItemCount = 0 the widget no-ops; this text shows through.
        switch (_renderState)
        {
            case CommitsRenderState.NoRepo:
                DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), "Select a repository to view its history.", z + 2);
                break;
            case CommitsRenderState.Loading:
                DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), "Loading…", z + 2);
                break;
            case CommitsRenderState.Error err:
                DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), err.Message, z + 2);
                break;
            case CommitsRenderState.Loaded:
                if (_snapshot == null || _snapshot.Commits.Count == 0)
                    DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), "No commits.", z + 2);
                break;
        }

        GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
        var dateXAll = pos.Right - dateW - ColumnGap;
        var hashXAll = dateXAll - hashW - ColumnGap;
        var authorXAll = hashXAll - authorW - ColumnGap;
        DrawColumnDivider(c, authorXAll - ColumnGap, pos.Bottom, pos.Height, DividerKind.Author, z + 100);
        DrawColumnDivider(c, hashXAll - ColumnGap, pos.Bottom, pos.Height, DividerKind.Hash, z + 100);
        DrawColumnDivider(c, dateXAll - ColumnGap, pos.Bottom, pos.Height, DividerKind.Date, z + 100);

        c.PopClip();
    }

    private void DrawHeader(ICanvas c, RectF pos, int z)
    {
        var headerRect = new RectF(pos.Left, pos.Top - HeaderHeight, pos.Width, HeaderHeight);
        c.DrawRect(new DrawRectInputs
        {
            Position = headerRect,
            Style = new RectStyle
            {
                BackgroundColor = _styles.HeaderBackground,
                BorderColor = new BorderColorStyle { Bottom = _styles.HeaderBorderBottom },
                BorderSize = new BorderSizeStyle { Bottom = 1 },
            },
            ZIndex = z,
        });

        GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
        var graphWidth = ComputeGraphColumnWidth();
        var dateX = pos.Right - dateW - ColumnGap;
        var hashX = dateX - hashW - ColumnGap;
        var authorX = hashX - authorW - ColumnGap;

        DrawHeaderText(c, "Commit", pos.Left + CommitGraphRenderer.PaddingLeft, pos.Top - HeaderHeight, graphWidth, z + 1);
        DrawHeaderText(c, "Author", authorX, pos.Top - HeaderHeight, authorW, z + 1);
        DrawHeaderText(c, "Hash", hashX, pos.Top - HeaderHeight, hashW, z + 1);
        DrawHeaderText(c, "Date", dateX, pos.Top - HeaderHeight, dateW, z + 1);
    }

    private static void DrawColumnOverlay(ICanvas c, float left, float bottom, float width, uint color, int z)
    {
        if (width <= 0) return;
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(left, bottom, width, RowHeight),
            Style = new RectStyle { BackgroundColor = color },
            ZIndex = z,
        });
    }

    private void DrawColumnDivider(ICanvas c, float centerX, float bottom, float height, DividerKind kind, int z)
    {
        var hovered = _hoveredDivider == kind;
        if (hovered)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(centerX - DividerHitWidth * 0.5f, bottom, DividerHitWidth, height),
                Style = new RectStyle { BackgroundColor = _styles.ColumnDividerHoverFill },
                ZIndex = z,
            });
        }
        c.DrawRect(new DrawRectInputs
        {
            Position = new RectF(centerX - DividerThickness * 0.5f, bottom, DividerThickness, height),
            Style = new RectStyle { BackgroundColor = hovered ? _styles.ColumnDividerHoverLine : _styles.ColumnDividerIdle },
            ZIndex = z + 1,
        });
    }

    private void DrawHeaderText(ICanvas c, string text, float left, float bottom, float width, int z)
    {
        if (width <= 0) return;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, bottom, width, HeaderHeight),
            Text = text,
            Style = _headerTextStyle,
            ZIndex = z,
        });
    }

    private void DrawPlaceholder(ICanvas c, RectF rect, string text, int z)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        c.DrawText(new DrawTextInputs
        {
            Position = rect,
            Text = text,
            Style = _placeholderStyle,
            ZIndex = z,
        });
    }

    private float ComputeGraphColumnWidth()
        => CommitGraphRenderer.ColumnWidth(_snapshot?.LaneCount ?? 0);

    // The summary (commit message) column has the highest priority: it keeps at least
    // MinSummaryWidth. When the view is too narrow to honor every metadata column at its
    // set width, they shrink to make room — Date first, then Hash, then Author — each
    // down to MinColumnWidth. The set widths (from divider drags) are the upper bound.
    private void GetEffectiveColumnWidths(out float author, out float hash, out float date)
    {
        author = _authorColumnWidth;
        hash = _hashColumnWidth;
        date = _dateColumnWidth;

        var totalWidth = Position.Width;
        if (totalWidth <= 0f) return;

        var available = totalWidth - MinSummaryWidth - ColumnGap * 4f;
        var deficit = author + hash + date - available;
        if (deficit <= 0f) return;

        ShrinkColumn(ref date, ref deficit);
        ShrinkColumn(ref hash, ref deficit);
        ShrinkColumn(ref author, ref deficit);
    }

    private static void ShrinkColumn(ref float width, ref float deficit)
    {
        if (deficit <= 0f) return;
        var take = Math.Min(width - MinColumnWidth, deficit);
        if (take <= 0f) return;
        width -= take;
        deficit -= take;
    }

    private RectF ComputeCommitsColumnRect(RectF body)
    {
        GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
        var dateX = body.Right - dateW - ColumnGap;
        var hashX = dateX - hashW - ColumnGap;
        var authorX = hashX - authorW - ColumnGap;
        var rightEdge = authorX - ColumnGap;
        var width = Math.Max(0f, rightEdge - body.Left);
        return new RectF(body.Left, body.Bottom, width, body.Height);
    }

    private void DrawCommitRowAt(ICanvas c, RectF rowRect, int rowIndex, RowRenderState state, int z)
    {
        var snap = _snapshot;
        if (snap == null || rowIndex < 0 || rowIndex >= snap.Commits.Count) return;
        var node = snap.Commits[rowIndex];

        var body = rowRect; // share names with the original DrawCommits for arithmetic clarity
        var rowBottom = rowRect.Bottom;

        GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
        var graphStartX = body.Left + CommitGraphRenderer.PaddingLeft;
        var dateX = body.Right - dateW - ColumnGap;
        var hashX = dateX - hashW - ColumnGap;
        var authorX = hashX - authorW - ColumnGap;
        var authorPanelLeft = authorX - ColumnGap;
        var hashPanelLeft = hashX - ColumnGap;
        var datePanelLeft = dateX - ColumnGap;

        var isSelected = node.Sha == _selectedSha;
        var isHighlighted = isSelected || state.IsContextHighlighted;

        if (isHighlighted)
        {
            c.DrawRect(new DrawRectInputs
            {
                Position = rowRect,
                Style = new RectStyle { BackgroundColor = _styles.RowSelectedBackground },
                ZIndex = z,
            });
        }

        CommitGraphRenderer.DrawCell(c, node, graphStartX, rowBottom, RowHeight, z + 1);

        var textTop = rowBottom;
        var summaryStartX = CommitGraphRenderer.SummaryStartX(graphStartX, node);
        var refsEndX = DrawBadges(c, node, summaryStartX, textTop, z + 2);
        var summaryDraw = Math.Max(0, body.Right - refsEndX);
        DrawText(c, node.Summary, refsEndX, textTop, summaryDraw, isHighlighted, z + 2);

        var rowOverlayColor = isHighlighted ? _styles.RowSelectedBackground : _styles.Background;
        DrawColumnOverlay(c, authorPanelLeft, rowBottom, hashPanelLeft - authorPanelLeft, rowOverlayColor, z + 3);
        DrawText(c, node.Author, authorX, textTop, authorW, isHighlighted, z + 4);
        DrawColumnOverlay(c, hashPanelLeft, rowBottom, datePanelLeft - hashPanelLeft, rowOverlayColor, z + 5);
        DrawHashText(c, ShortSha(node.Sha), hashX, textTop, hashW, isHighlighted, z + 6);
        DrawColumnOverlay(c, datePanelLeft, rowBottom, body.Right - datePanelLeft, rowOverlayColor, z + 7);
        DrawText(c, FormatRelative(node.When), dateX, textTop, dateW, isHighlighted, z + 8);
    }

    private float DrawBadges(ICanvas c, CommitNode node, float left, float rowBottom, int z)
    {
        if (node.Refs.Count == 0) return left;
        if (Context == null) return left;

        const float IconGap = 4f;

        var x = left;
        var badgeY = rowBottom + (RowHeight - BadgeHeight) * 0.5f;
        foreach (var badge in node.Refs)
        {
            var icon = badge.Kind switch
            {
                RefKind.Stash => LucideIcons.Stash,
                RefKind.LocalBranch => LucideIcons.Branch,
                RefKind.RemoteBranch => LucideIcons.Branch,
                _ => null,
            };
            var iconWidth = icon != null ? Context.Canvas.MeasureTextWidth(icon, _badgeIconStyle) : 0f;
            var textWidth = Context.Canvas.MeasureTextWidth(badge.Name, _badgeTextStyle);
            var badgeW = BadgePaddingX * 2 + textWidth
                       + (icon != null ? iconWidth + IconGap : 0f);
            var bg = badge.Kind switch
            {
                RefKind.LocalBranch => _styles.BadgeLocalBackground,
                RefKind.RemoteBranch => _styles.BadgeRemoteBackground,
                RefKind.Head => _styles.BadgeHeadBackground,
                _ => _styles.BadgeLocalBackground,
            };
            c.DrawRect(new DrawRectInputs
            {
                Position = new RectF(x, badgeY, badgeW, BadgeHeight),
                Style = new RectStyle
                {
                    BackgroundColor = bg,
                    BorderRadius = BorderRadiusStyle.All(3),
                },
                ZIndex = z,
            });
            var contentX = x + BadgePaddingX;
            if (icon != null)
            {
                c.DrawText(new DrawTextInputs
                {
                    Position = new RectF(contentX, badgeY, iconWidth, BadgeHeight),
                    Text = icon,
                    Style = _badgeIconStyle,
                    ZIndex = z + 1,
                });
                contentX += iconWidth + IconGap;
            }
            c.DrawText(new DrawTextInputs
            {
                Position = new RectF(contentX, badgeY, textWidth, BadgeHeight),
                Text = badge.Name,
                Style = _badgeTextStyle,
                ZIndex = z + 1,
            });
            x += badgeW + BadgeGap;
        }
        return x + BadgeGap;
    }

    private void DrawText(ICanvas c, string text, float left, float rowBottom, float width, bool active, int z)
    {
        if (width <= 0 || string.IsNullOrEmpty(text)) return;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, rowBottom, width, RowHeight),
            Text = text,
            Style = active ? _rowTextActiveStyle : _rowTextStyle,
            ZIndex = z,
        });
    }

    private void DrawHashText(ICanvas c, string text, float left, float rowBottom, float width, bool active, int z)
    {
        if (width <= 0 || string.IsNullOrEmpty(text)) return;
        c.DrawText(new DrawTextInputs
        {
            Position = new RectF(left, rowBottom, width, RowHeight),
            Text = text,
            Style = active ? _hashTextActiveStyle : _hashTextStyle,
            ZIndex = z,
        });
    }

    private static string ShortSha(string sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha[..7] : sha);

    internal enum DividerKind
    {
        None,
        Author,
        Hash,
        Date,
    }

    internal DividerKind HitTestDivider(PointF point)
    {
        var pos = Position;
        if (point.X < pos.Left || point.X > pos.Right) return DividerKind.None;
        if (point.Y < pos.Bottom || point.Y > pos.Top) return DividerKind.None;

        GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
        var dateX = pos.Right - dateW - ColumnGap;
        var hashX = dateX - hashW - ColumnGap;
        var authorX = hashX - authorW - ColumnGap;
        var authorDividerX = authorX - ColumnGap;
        var hashDividerX = hashX - ColumnGap;
        var dateDividerX = dateX - ColumnGap;

        if (Math.Abs(point.X - dateDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Date;
        if (Math.Abs(point.X - hashDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Hash;
        if (Math.Abs(point.X - authorDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Author;
        return DividerKind.None;
    }

    internal void ResizeAuthorColumn(float mouseDeltaX)
    {
        _authorColumnWidth = Math.Clamp(_authorColumnWidth - mouseDeltaX, MinColumnWidth, MaxColumnWidth);
    }

    internal void ResizeHashColumn(float mouseDeltaX)
    {
        TradeWidths(ref _hashColumnWidth, ref _authorColumnWidth, mouseDeltaX);
    }

    internal void ResizeDateColumn(float mouseDeltaX)
    {
        TradeWidths(ref _dateColumnWidth, ref _hashColumnWidth, mouseDeltaX);
    }

    private static void TradeWidths(ref float rightCol, ref float leftCol, float mouseDeltaX)
    {
        // Drag right (positive delta) shrinks the right column and grows the left column.
        // Keeps the previous-divider position fixed: only the two adjacent columns change.
        var shrink = mouseDeltaX;
        shrink = Math.Clamp(shrink, -(MaxColumnWidth - rightCol), rightCol - MinColumnWidth);
        shrink = Math.Clamp(shrink, -(leftCol - MinColumnWidth), MaxColumnWidth - leftCol);
        rightCol -= shrink;
        leftCol += shrink;
    }

    internal void SetHoveredDivider(DividerKind kind)
    {
        _hoveredDivider = kind;
    }

    private void OnRowClicked(int rowIndex, InputModifiers _, PointF __)
    {
        var snap = _snapshot;
        if (snap == null || rowIndex < 0 || rowIndex >= snap.Commits.Count) return;
        _vm?.SelectCommit(snap.Commits[rowIndex].Sha);
    }

    private void OnRowContextRequested(int rowIndex, PointF point)
    {
        if (Context == null) return;
        var snap = _snapshot;
        if (snap == null || rowIndex < 0 || rowIndex >= snap.Commits.Count) return;
        var sha = snap.Commits[rowIndex].Sha;

        var items = BuildCommitMenuItems(sha);
        if (items.Count == 0) return;

        _list.SetContextHighlight(rowIndex);
        var opened = RepoBarContextMenu.Show(Context, point, items);
        if (opened == null)
        {
            _list.SetContextHighlight(null);
            return;
        }
        opened.Closed += () => _list.SetContextHighlight(null);
    }

    private IReadOnlyList<RepoBarContextMenu.Item> BuildCommitMenuItems(string sha)
    {
        var capturedSha = sha;
        var head = _snapshot?.HeadBranchName;
        if (head != null)
        {
            return new[]
            {
                new RepoBarContextMenu.Item(
                    $"Reset {head} to here",
                    () => _vm?.RequestReset(capturedSha),
                    LucideIcons.Branch,
                    LabelSegments: BuildResetSegments(head)),
            };
        }
        return new[]
        {
            new RepoBarContextMenu.Item(
                "Reset to this commit",
                () => _vm?.RequestReset(capturedSha),
                LucideIcons.Branch),
        };
    }

    private static IReadOnlyList<MenuLabelSegment> BuildResetSegments(string branch) =>
    [
        new MenuLabelSegment("Reset "),
        new MenuLabelSegment(branch, Bold: true),
        new MenuLabelSegment(" to here"),
    ];

    private static string FormatRelative(DateTimeOffset when)
    {
        var now = DateTimeOffset.UtcNow;
        var delta = now - when;
        if (delta.TotalSeconds < 0) return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalDays < 30) return $"{(int)(delta.TotalDays / 7)}w ago";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)}mo ago";
        return $"{(int)(delta.TotalDays / 365)}y ago";
    }
}
