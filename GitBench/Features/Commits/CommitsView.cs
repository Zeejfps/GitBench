using ZGF.Gui.Views;
using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.VirtualRowList;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Commits;

internal sealed record CommitsView : Widget
{
    protected override View CreateView(Context ctx) => new Core(ctx);

    internal enum DividerKind
    {
        None,
        Author,
        Hash,
        Date,
    }

    internal sealed class Core : ContainerView
    {
        private const float HeaderHeight = 28f;
        private const float RowHeight = 26f;
        private const float ColumnGap = 12f;

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

        private readonly Context _ctx;
        private readonly ICanvas _canvas;
        private readonly ILocalizationService _loc;
        private readonly CommitsViewModel _vm;
        private readonly VirtualRowListView _list;
        private readonly ListArrowKbmController _arrowController;

        private float _lastNormalizedScroll;
        private float _lastScale = 1f;
        private string? _selectedSha;
        private bool _truncated;
        // When a search filter is active the graph column is dropped (lanes don't apply to a subset).
        private bool _filtering;
        // Repaints the relative dates ("3m ago"), which go stale with no state change to dirty us.
        private const int DateRefreshMs = 30_000;

        public event Action<float>? ScrollPositionChanged;
        public event Action<float>? ScaleChanged;

        private readonly TextStyle _rowTextStyle = TextStyles.Row(0u);
        private readonly TextStyle _rowTextActiveStyle = TextStyles.Row(0u);
        private readonly TextStyle _headerTextStyle = TextStyles.Row(0u);
        private readonly TextStyle _placeholderStyle = TextStyles.Centered(0u);
        private readonly TextStyle _badgeTextStyle = TextStyles.Row(0u);
        private readonly TextStyle _badgeIconStyle = TextStyles.Icon(0u, 12f);
        // Branch-glyph tints by upstream sync: green when level with the remote, amber when
        // ahead/behind, gray when there's no upstream.
        private readonly TextStyle _badgeIconInSyncStyle = TextStyles.Icon(0u, 12f);
        private readonly TextStyle _badgeIconDivergedStyle = TextStyles.Icon(0u, 12f);
        private readonly TextStyle _badgeIconUntrackedStyle = TextStyles.Icon(0u, 12f);
        // Bold variant for the checked-out branch's name — mirrors how the Branches view marks
        // the current branch (FontWeight.Bold) instead of drawing a separate HEAD marker.
        private readonly TextStyle _badgeTextBoldStyle = new()
        {
            FontWeight = FontWeight.Bold,
            VerticalAlignment = TextAlignment.Center,
            HorizontalAlignment = TextAlignment.Start,
        };
        private readonly TextStyle _hashTextStyle = TextStyles.Row(0u);
        private readonly TextStyle _hashTextActiveStyle = TextStyles.Row(0u);

        // Reused across draws to avoid a fresh RectStyle heap allocation per rect, per row,
        // every frame. DrawRect copies every field out synchronously, so one mutable instance
        // is safe to share — set the varying fields (mostly just BackgroundColor) immediately
        // before each draw. _fillStyle covers the plain solid fills; the header and badge keep
        // their own because they carry a constant border / corner radius.
        private readonly RectStyle _fillStyle = new();
        private readonly RectStyle _headerRectStyle = new()
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
        };
        private readonly RectStyle _badgeRectStyle = new()
        {
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        };

        private CommitsViewStyles _styles = ThemeStyles.Dark.CommitsView;

        public Core(Context ctx)
        {
            _ctx = ctx;
            _canvas = ctx.Canvas;
            _loc = ctx.Localization();
            var vm = ctx.Require<CommitsViewModel>();
            _vm = vm;
            var input = ctx.Require<InputSystem>();
            var theme = ctx.Theme();

            _list = new VirtualRowListView
            {
                RowHeight = RowHeight,
                ItemBuilder = DrawCommitRowAt,
            };
            _list.RowClicked += OnRowClicked;
            _list.RowContextRequested += OnRowContextRequested;
            _list.ScrollChanged += NotifyScrollChanged;

            AddChildToSelf(_list);
            _list.UseController(input, () => new VirtualRowListController(_list));

            this.BindThemed(theme, s =>
            {
                _styles = s.CommitsView;
                _rowTextStyle.TextColor = _styles.RowText;
                _rowTextActiveStyle.TextColor = _styles.RowTextActive;
                _headerTextStyle.TextColor = _styles.HeaderText;
                _placeholderStyle.TextColor = _styles.PlaceholderText;
                _badgeTextStyle.TextColor = _styles.BadgeText;
                _badgeIconStyle.TextColor = _styles.BadgeText;
                _badgeIconInSyncStyle.TextColor = _styles.BadgeBranchInSyncIcon;
                _badgeIconDivergedStyle.TextColor = _styles.BadgeBranchDivergedIcon;
                _badgeIconUntrackedStyle.TextColor = _styles.BadgeBranchUntrackedIcon;
                _badgeTextBoldStyle.TextColor = _styles.BadgeText;
                _hashTextStyle.TextColor = _styles.RowTextDim;
                _hashTextActiveStyle.TextColor = _styles.RowTextActive;
                _headerRectStyle.BorderColor = new BorderColorStyle { Bottom = _styles.HeaderBorderBottom };
                SetDirty();
            });

            this.Bind(_loc.Strings, _ => SetDirty());

            this.UseController(input, () => new CommitsViewController(this, ctx));

            // Up/Down arrow navigation over the commit list, mirroring the commit-details and
            // local-changes file lists. The history is single-select, so Shift is ignored and
            // there are no folder/activate/delete actions — only row movement is wired. Takes
            // focus when a row is clicked (see OnRowClicked).
            _arrowController = new ListArrowKbmController(
                this,
                input,
                (delta, _) => _vm.MoveSelection(delta),
                _ => { },
                () => { },
                () => { });
            // Keyboard shortcuts for the selected commit (cherry-pick, revert, create branch/tag),
            // sharing one definition with the context menu so the keys and hints can't drift.
            _arrowController.RowActions = SelectedCommitActions;
            this.UseController(input, _arrowController);

            this.UseViewModel(() => vm, _ => { });

            this.Bind(vm.Render, SetRenderState);
            this.Bind(vm.SelectedSha, SetSelectedSha);
            this.Bind(vm.IsFiltering, f =>
            {
                if (_filtering == f) return;
                _filtering = f;
                SetDirty();
            });

            var dispatcher = ctx.Get<IUiDispatcher>();
            if (dispatcher != null)
                this.Use(() => StartDateRefresh(dispatcher));
        }

        // Pass-through for the panel's search bar; the VM is owned by this view via UseViewModel.
        public void SetSearchQuery(string? query) => _vm.SetSearchQuery(query);

        private IDisposable StartDateRefresh(IUiDispatcher dispatcher)
        {
            var cts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(DateRefreshMs, cts.Token).ConfigureAwait(false);
                        dispatcher.Post(() =>
                        {
                            if (cts.Token.IsCancellationRequested) return;
                            if (_snapshot?.Commits.Count > 0) SetDirty();
                        });
                    }
                }
                catch (OperationCanceledException) { /* expected */ }
            }, cts.Token);

            return new CancelOnDispose(cts);
        }

        private sealed class CancelOnDispose(CancellationTokenSource cts) : IDisposable
        {
            public void Dispose()
            {
                cts.Cancel();
                cts.Dispose();
            }
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

        protected override void OnLayoutChildren()
        {
            base.OnLayoutChildren();
            // Emit each layout pass (as VerticalScrollPane does) so a resize that changes the
            // viewport/content ratio re-syncs a bound scrollbar's gutter, not just user scrolls.
            NotifyScrollChanged();
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
            _fillStyle.BackgroundColor = _styles.Background;
            c.DrawRect(new DrawRectInputs
            {
                Position = pos,
                Style = _fillStyle,
                ZIndex = z,
            });

            DrawHeader(c, pos, z + 1);

            var bodyRect = _list.Position;

            // Placeholder text lives in the parent because the widget is row-only.
            // When ItemCount = 0 the widget no-ops; this text shows through.
            var strings = _loc.Strings.Value;
            switch (_renderState)
            {
                case CommitsRenderState.NoRepo:
                    DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), strings.CommitsNoRepoSelected, z + 2);
                    break;
                case CommitsRenderState.Loading:
                    DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), strings.CommonLoading, z + 2);
                    break;
                case CommitsRenderState.Error err:
                    DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), err.Message, z + 2);
                    break;
                case CommitsRenderState.Loaded:
                    if (_snapshot == null || _snapshot.Commits.Count == 0)
                        DrawPlaceholder(c, ComputeCommitsColumnRect(bodyRect), strings.CommitsNoCommits, z + 2);
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
            _headerRectStyle.BackgroundColor = _styles.HeaderBackground;
            c.DrawRect(new DrawRectInputs
            {
                Position = headerRect,
                Style = _headerRectStyle,
                ZIndex = z,
            });

            GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
            var graphWidth = ComputeGraphColumnWidth();
            var dateX = pos.Right - dateW - ColumnGap;
            var hashX = dateX - hashW - ColumnGap;
            var authorX = hashX - authorW - ColumnGap;

            var strings = _loc.Strings.Value;
            DrawHeaderText(c, strings.CommitsHeaderCommit, pos.Left + CommitGraphRenderer.PaddingLeft, pos.Top - HeaderHeight, graphWidth, z + 1);
            DrawHeaderText(c, strings.CommitsHeaderAuthor, authorX, pos.Top - HeaderHeight, authorW, z + 1);
            DrawHeaderText(c, strings.CommitsHeaderHash, hashX, pos.Top - HeaderHeight, hashW, z + 1);
            DrawHeaderText(c, strings.CommitsHeaderDate, dateX, pos.Top - HeaderHeight, dateW, z + 1);
        }

        private void DrawColumnOverlay(ICanvas c, float left, float bottom, float width, uint color, int z)
        {
            if (width <= 0) return;
            _fillStyle.BackgroundColor = color;
            c.DrawRect(new DrawRectInputs
            {
                Position = Place(left, bottom, width, RowHeight),
                Style = _fillStyle,
                ZIndex = z,
            });
        }

        private void DrawColumnDivider(ICanvas c, float centerX, float bottom, float height, DividerKind kind, int z)
        {
            var hovered = _hoveredDivider == kind;
            if (hovered)
            {
                _fillStyle.BackgroundColor = _styles.ColumnDividerHoverFill;
                c.DrawRect(new DrawRectInputs
                {
                    Position = Place(centerX - DividerHitWidth * 0.5f, bottom, DividerHitWidth, height),
                    Style = _fillStyle,
                    ZIndex = z,
                });
            }
            _fillStyle.BackgroundColor = hovered ? _styles.ColumnDividerHoverLine : _styles.ColumnDividerIdle;
            c.DrawRect(new DrawRectInputs
            {
                Position = Place(centerX - DividerThickness * 0.5f, bottom, DividerThickness, height),
                Style = _fillStyle,
                ZIndex = z + 1,
            });
        }

        private void DrawHeaderText(ICanvas c, string text, float left, float bottom, float width, int z)
        {
            if (width <= 0) return;
            c.DrawText(new DrawTextInputs
            {
                Position = Place(left, bottom, width, HeaderHeight),
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
                Position = Place(rect.Left, rect.Bottom, rect.Width, rect.Height),
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

        // Reflects an element's horizontal extent within the view when the UI is right-to-left, so the
        // hand-rolled multi-column layout mirrors (graph on the right, date column on the left) without
        // rewriting the column math. In-box text right-aligns on its own via the canvas text base.
        // The list spans the full width, so Position is the shared bound for header, dividers and rows.
        private RectF Place(float left, float bottom, float width, float height) =>
            IsRtl
                ? new RectF(Position.Left + Position.Right - left - width, bottom, width, height)
                : new RectF(left, bottom, width, height);

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
                _fillStyle.BackgroundColor = _styles.RowSelectedBackground;
                c.DrawRect(new DrawRectInputs
                {
                    Position = rowRect,
                    Style = _fillStyle,
                    ZIndex = z,
                });
            }

            // Filtered list is flat: skip the graph cell and start the summary at the graph origin.
            if (!_filtering)
            {
                var rowBackground = isHighlighted ? _styles.RowSelectedBackground : _styles.Background;
                CommitGraphRenderer.DrawCell(c, node, graphStartX, rowBottom, RowHeight, snap.LaneCount, z + 1, rowBackground,
                    IsRtl, Position.Left + Position.Right);
            }

            var textTop = rowBottom;
            var summaryStartX = _filtering ? graphStartX : CommitGraphRenderer.SummaryStartX(graphStartX, node, snap.LaneCount);
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
                    RefKind.Tag => LucideIcons.Tag,
                    _ => null,
                };

                // The checked-out branch's name is bolded (like the Branches view) rather than
                // carrying a separate HEAD marker. The branch glyph is tinted by upstream sync:
                // green = level, amber = ahead/behind, gray = no upstream. A level upstream is
                // already folded into this badge.
                var iconStyle = badge.Sync switch
                {
                    BranchSync.InSync => _badgeIconInSyncStyle,
                    BranchSync.Diverged => _badgeIconDivergedStyle,
                    BranchSync.Untracked => _badgeIconUntrackedStyle,
                    _ => _badgeIconStyle,
                };
                var nameStyle = badge.IsCurrent ? _badgeTextBoldStyle : _badgeTextStyle;
                var iconWidth = icon != null ? _canvas.MeasureTextWidth(icon, iconStyle) : 0f;
                var textWidth = _canvas.MeasureTextWidth(badge.Name, nameStyle);

                var badgeW = BadgePaddingX * 2 + textWidth
                           + (icon != null ? iconWidth + IconGap : 0f);
                var bg = badge.Kind switch
                {
                    RefKind.LocalBranch => _styles.BadgeLocalBackground,
                    RefKind.RemoteBranch => _styles.BadgeRemoteBackground,
                    RefKind.Head => _styles.BadgeHeadBackground,
                    RefKind.Tag => _styles.BadgeTagBackground,
                    _ => _styles.BadgeLocalBackground,
                };
                _badgeRectStyle.BackgroundColor = bg;
                c.DrawRect(new DrawRectInputs
                {
                    Position = Place(x, badgeY, badgeW, BadgeHeight),
                    Style = _badgeRectStyle,
                    ZIndex = z,
                });
                var contentX = x + BadgePaddingX;
                if (icon != null)
                {
                    c.DrawText(new DrawTextInputs
                    {
                        Position = Place(contentX, badgeY, iconWidth, BadgeHeight),
                        Text = icon,
                        Style = iconStyle,
                        ZIndex = z + 1,
                    });
                    contentX += iconWidth + IconGap;
                }
                c.DrawText(new DrawTextInputs
                {
                    Position = Place(contentX, badgeY, textWidth, BadgeHeight),
                    Text = badge.Name,
                    Style = nameStyle,
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
                Position = Place(left, rowBottom, width, RowHeight),
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
                Position = Place(left, rowBottom, width, RowHeight),
                Text = text,
                Style = active ? _hashTextActiveStyle : _hashTextStyle,
                ZIndex = z,
            });
        }

        private static string ShortSha(string sha)
            => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha[..7] : sha);

        internal DividerKind HitTestDivider(PointF point)
        {
            var pos = Position;
            if (point.X < pos.Left || point.X > pos.Right) return DividerKind.None;
            if (point.Y < pos.Bottom || point.Y > pos.Top) return DividerKind.None;

            // Dividers are computed in LTR and reflected on draw, so reflect the pointer back to LTR
            // space to test against the same math.
            var px = IsRtl ? pos.Left + pos.Right - point.X : point.X;

            GetEffectiveColumnWidths(out var authorW, out var hashW, out var dateW);
            var dateX = pos.Right - dateW - ColumnGap;
            var hashX = dateX - hashW - ColumnGap;
            var authorX = hashX - authorW - ColumnGap;
            var authorDividerX = authorX - ColumnGap;
            var hashDividerX = hashX - ColumnGap;
            var dateDividerX = dateX - ColumnGap;

            if (Math.Abs(px - dateDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Date;
            if (Math.Abs(px - hashDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Hash;
            if (Math.Abs(px - authorDividerX) <= DividerHitWidth * 0.5f) return DividerKind.Author;
            return DividerKind.None;
        }

        internal void ResizeAuthorColumn(float mouseDeltaX)
        {
            // Columns are mirrored under RTL, so a rightward drag moves the boundary the other way.
            if (IsRtl) mouseDeltaX = -mouseDeltaX;
            _authorColumnWidth = Math.Clamp(_authorColumnWidth - mouseDeltaX, MinColumnWidth, MaxColumnWidth);
        }

        internal void ResizeHashColumn(float mouseDeltaX)
        {
            if (IsRtl) mouseDeltaX = -mouseDeltaX;
            TradeWidths(ref _hashColumnWidth, ref _authorColumnWidth, mouseDeltaX);
        }

        internal void ResizeDateColumn(float mouseDeltaX)
        {
            if (IsRtl) mouseDeltaX = -mouseDeltaX;
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
            _vm.SelectCommit(snap.Commits[rowIndex].Sha);
            _arrowController.TakeFocus();
        }

        private void OnRowContextRequested(int rowIndex, PointF point)
        {
            var snap = _snapshot;
            if (snap == null || rowIndex < 0 || rowIndex >= snap.Commits.Count) return;
            var node = snap.Commits[rowIndex];

            var items = BuildCommitMenuItems(node);
            if (items.Count == 0) return;

            _list.SetContextHighlight(rowIndex);
            var opened = RepoBarContextMenu.Show(_ctx, point, items);
            if (opened == null)
            {
                _list.SetContextHighlight(null);
                return;
            }
            opened.Closed += () => _list.SetContextHighlight(null);
        }

        private IReadOnlyList<RepoBarContextMenu.Item> BuildCommitMenuItems(CommitNode node)
        {
            var capturedSha = node.Sha;
            var head = _snapshot?.HeadBranchName;
            var s = _loc.Strings.Value;
            var items = new List<RepoBarContextMenu.Item>();

            // Per-tag entries at the top: each tag on this commit opens a submenu (hover) that
            // currently offers "Delete Tag…". More per-tag actions can be added here later.
            var hasTag = false;
            foreach (var badge in node.Refs)
            {
                if (badge.Kind != RefKind.Tag) continue;
                hasTag = true;
                var tagName = badge.Name;
                items.Add(new RepoBarContextMenu.Item(
                    tagName,
                    () => { },
                    LucideIcons.Tag,
                    Submenu:
                    [
                        new RepoBarContextMenu.Item(
                            s.CommitsContextDeleteTag,
                            () => _vm.RequestDeleteTag(tagName),
                            LucideIcons.Trash),
                    ]));
            }
            if (hasTag) items.Add(RepoBarContextMenu.Separator);

            if (head != null)
            {
                items.Add(new RepoBarContextMenu.Item(
                    s.CommitsContextResetBranch(head),
                    () => _vm.RequestReset(capturedSha),
                    LucideIcons.Branch,
                    LabelSegments: BuildResetSegments(s.CommitsContextResetBranch(head), head)));
            }
            else
            {
                // Detached: there's no current branch to reset, so let the user pick which local
                // branch to move to this commit (git branch -f + checkout). Falls back to moving
                // the detached HEAD itself when the repo has no local branches.
                var branches = CollectLocalBranchNames();
                if (branches.Count > 0)
                {
                    var submenu = new List<RepoBarContextMenu.Item>(branches.Count);
                    foreach (var name in branches)
                    {
                        var branch = name;
                        submenu.Add(new RepoBarContextMenu.Item(
                            branch,
                            () => _vm.RequestMoveBranch(branch, capturedSha),
                            LucideIcons.Branch));
                    }
                    items.Add(new RepoBarContextMenu.Item(
                        s.CommitsContextResetBranchDetached,
                        () => { },
                        LucideIcons.Branch,
                        Submenu: submenu));
                }
                else
                {
                    items.Add(new RepoBarContextMenu.Item(
                        s.CommitsContextResetDetached,
                        () => _vm.RequestReset(capturedSha),
                        LucideIcons.Branch));
                }
            }

            // Create / apply actions share one definition with the keyboard (see CommitActionsFor),
            // so their menu shortcut hints derive from the same gestures the list dispatches on.
            var actions = CommitActionsFor(capturedSha);
            items.Add(RepoBarContextMenu.ToItem(actions.CreateBranch));
            items.Add(RepoBarContextMenu.ToItem(actions.CreateTag));

            // Apply-this-commit actions. Both run immediately (no dialog): they're non-destructive
            // and any conflict is recoverable via the operation banner, so no "…" suffix.
            items.Add(RepoBarContextMenu.Separator);
            items.Add(RepoBarContextMenu.ToItem(actions.CherryPick));
            items.Add(RepoBarContextMenu.ToItem(actions.Revert));

            return items;
        }

        // The shortcut-bearing commit actions, defined once for both the context menu (per row under
        // the cursor) and the keyboard (for the selected row). Reset/move-branch stay menu-only: they
        // can act destructively, so they don't get a bare-letter key. Plain letters, active only while
        // the focused list owns the keyboard.
        private (RowAction CreateBranch, RowAction CreateTag, RowAction CherryPick, RowAction Revert) CommitActionsFor(string sha)
        {
            var s = _loc.Strings.Value;
            return (
                new RowAction(s.CommitsContextCreateBranch, () => _vm.RequestCreateBranch(sha), LucideIcons.Branch, new KeyGesture(KeyboardKey.B)),
                new RowAction(s.CommitsContextCreateTag, () => _vm.RequestCreateTag(sha), LucideIcons.Tag, new KeyGesture(KeyboardKey.T)),
                new RowAction(s.CommitsContextCherryPick, () => _vm.RequestCherryPick(sha), LucideIcons.Copy, new KeyGesture(KeyboardKey.C)),
                new RowAction(s.CommitsContextRevert, () => _vm.RequestRevert(sha), LucideIcons.Undo, new KeyGesture(KeyboardKey.V)));
        }

        private IReadOnlyList<RowAction> SelectedCommitActions()
        {
            if (_selectedSha is not { } sha) return Array.Empty<RowAction>();
            var a = CommitActionsFor(sha);
            return [a.CreateBranch, a.CreateTag, a.CherryPick, a.Revert];
        }

        // Distinct local-branch names across the snapshot, sorted for a stable submenu order.
        // Harvested from rendered ref badges so it needs no extra git call; a branch whose tip
        // falls outside the (capped) walk simply won't appear, which is acceptable.
        private IReadOnlyList<string> CollectLocalBranchNames()
        {
            var snap = _snapshot;
            if (snap == null) return Array.Empty<string>();
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var node in snap.Commits)
            {
                foreach (var badge in node.Refs)
                {
                    if (badge.Kind == RefKind.LocalBranch)
                        names.Add(badge.Name);
                }
            }
            return names.Count == 0 ? Array.Empty<string>() : new List<string>(names);
        }

        // Bolds the branch name inside the already-localized "Reset <branch> to here" label by
        // re-finding the interpolated value in the formatted string, so the wording translates
        // freely while the emphasis stays on the branch name.
        private static IReadOnlyList<MenuLabelSegment> BuildResetSegments(string text, string branch)
        {
            var idx = string.IsNullOrEmpty(branch) ? -1 : text.IndexOf(branch, StringComparison.Ordinal);
            if (idx < 0)
                return [new MenuLabelSegment(text)];

            var segments = new List<MenuLabelSegment>();
            if (idx > 0)
                segments.Add(new MenuLabelSegment(text.Substring(0, idx)));
            segments.Add(new MenuLabelSegment(branch, Bold: true));
            var tail = idx + branch.Length;
            if (tail < text.Length)
                segments.Add(new MenuLabelSegment(text.Substring(tail)));
            return segments;
        }

        private string FormatRelative(DateTimeOffset when) =>
            Format.RelativeTime(_ctx.Localization().Strings.Value, when);
    }
}
