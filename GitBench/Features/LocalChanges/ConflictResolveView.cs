using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

/// <summary>
/// Fork-style merge-conflict resolution header shown in the diff pane when a conflicted
/// working-tree file is selected. Presents the two sides (ours / theirs) as commit cards
/// with a checkbox each, wired by connector lines to a central junction. Clicking either a
/// card or its checkbox selects that side; "Merge" applies the chosen side(s) — one side =
/// take that whole file, both = keep both versions — and "Merge in editor" opens the file
/// for manual resolution. Whole-file resolution only (Phase 1 of the in-app conflict UI);
/// per-hunk selection is a later pass.
/// </summary>
internal sealed class ConflictResolveView : ContainerView
{
    private const float CardWidth = 300f;
    private const float ButtonHeight = 34f;
    private const float ButtonStackWidth = 220f;

    private readonly Context _ctx;
    private readonly IThemeService<ThemeStyles> _theme;
    private readonly Action _onTakeOurs;
    private readonly Action _onTakeTheirs;
    private readonly Action _onTakeBoth;
    private readonly Action _onOpenInEditor;
    private readonly Action _onMarkResolved;

    private readonly ColumnView _column;

    public ConflictResolveView(
        Context ctx,
        Action onTakeOurs,
        Action onTakeTheirs,
        Action onTakeBoth,
        Action onOpenInEditor,
        Action onMarkResolved)
    {
        _ctx = ctx;
        _theme = ctx.Theme();
        _onTakeOurs = onTakeOurs;
        _onTakeTheirs = onTakeTheirs;
        _onTakeBoth = onTakeBoth;
        _onOpenInEditor = onOpenInEditor;
        _onMarkResolved = onMarkResolved;

        _column = new ColumnView { Gap = 14 };

        var background = new RectView
        {
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = 24, Right = 24, Top = 24, Bottom = 24 },
                    Children = { _column },
                },
            },
        };
        background.BindThemedBackgroundColor(_theme, s => s.DiffView.PanelBackground);
        AddChildToSelf(background);
    }

    // Rebuilds the panel for a conflict. Cheap and infrequent (only on selecting a conflicted
    // file), so a full rebuild keeps the wiring simple — fresh state/cards/commands each time.
    public void SetContext(string path, ConflictContext ctx)
    {
        // The two sides' selection state, shared between each card and its checkbox so clicking
        // either toggles the same flag. Theirs is the incoming side (left), ours the current (right).
        var theirsChecked = new State<bool>(false);
        var oursChecked = new State<bool>(false);

        var theirsCard = new ConflictCard(_ctx, ctx.Theirs, theirsChecked);
        var oursCard = new ConflictCard(_ctx, ctx.Ours, oursChecked);
        var junction = new MergeJunctionView(_theme, theirsChecked, oursChecked);

        var mergeButton = new DialogButton(_ctx, "Merge", role: DialogButtonRole.Primary) { Height = ButtonHeight };
        var canMerge = new Derived<bool>(() => theirsChecked.Value || oursChecked.Value);
        mergeButton.BindCommand(new Command(() =>
        {
            var t = theirsChecked.Value;
            var o = oursChecked.Value;
            if (t && o) _onTakeBoth();
            else if (t) _onTakeTheirs();
            else if (o) _onTakeOurs();
        }, canMerge));

        // The button names the action it will take, so the pick is confirmable before clicking:
        // "Choose <branch>" for one side, "Merge both" for both, "Merge" (disabled) when neither.
        void UpdateMergeLabel()
        {
            mergeButton.Label = (theirsChecked.Value, oursChecked.Value) switch
            {
                (true, true) => "Merge both",
                (true, false) => $"Choose {ctx.Theirs.Label}",
                (false, true) => $"Choose {ctx.Ours.Label}",
                _ => "Merge",
            };
        }
        mergeButton.Bind(theirsChecked, _ => UpdateMergeLabel());
        mergeButton.Bind(oursChecked, _ => UpdateMergeLabel());
        UpdateMergeLabel();

        var openButton = new DialogButton(_ctx, "Merge in editor", _onOpenInEditor) { Height = ButtonHeight };
        openButton.Icon = LucideIcons.ExternalLink;

        // For conflicts already resolved outside the app: stages the file as-is so the path
        // clears the unmerged state, no side-pick needed.
        var resolvedButton = new DialogButton(_ctx, "Mark as resolved", _onMarkResolved) { Height = ButtonHeight };
        resolvedButton.Icon = LucideIcons.CheckSquare;

        _column.Children.Clear();
        _column.Children.Add(BuildTitleRow());
        _column.Children.Add(Centered(BuildFileNameRow(path)));
        _column.Children.Add(Centered(new FlexRowView
        {
            // Boxes sit flush against the inner card edges; the junction supplies its own
            // inset, so no inter-item gap here.
            Gap = 0f,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children = { theirsCard, junction, oursCard },
        }));
        _column.Children.Add(Centered(new ColumnView
        {
            Gap = 8,
            Width = ButtonStackWidth,
            Children = { mergeButton, openButton, resolvedButton },
        }));
    }

    private View BuildFileNameRow(string path)
    {
        var icon = new TextView(_ctx.Canvas)
        {
            Text = LucideIcons.File,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 15f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(_theme, s => s.Palette.TextMedium);

        var name = new TextView(_ctx.Canvas) { Text = Leaf(path), VerticalTextAlignment = TextAlignment.Center };
        name.BindThemedTextColor(_theme, s => s.Palette.TextStrong);

        return new FlexRowView
        {
            Gap = 8f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { icon, name },
        };
    }

    private static View Centered(View child) => new FlexRowView
    {
        MainAxisAlignment = MainAxisAlignment.Center,
        CrossAxisAlignment = CrossAxisAlignment.Center,
        Children = { child },
    };

    private View BuildTitleRow()
    {
        var icon = new TextView(_ctx.Canvas)
        {
            Text = LucideIcons.TriangleAlert,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 18f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(_theme, s => s.FileChangeRow.StatusModified);

        var title = new TextView(_ctx.Canvas) { Text = "Merge conflict", FontSize = 16f, VerticalTextAlignment = TextAlignment.Center };
        title.BindThemedTextColor(_theme, s => s.Palette.TextStrong);

        var subtitle = new TextView(_ctx.Canvas) { Text = "Select the changes or merge them manually", HorizontalTextAlignment = TextAlignment.Center };
        subtitle.BindThemedTextColor(_theme, s => s.Palette.TextMuted);

        return new ColumnView
        {
            Gap = 4,
            Children =
            {
                Centered(new FlexRowView
                {
                    Gap = 8f,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = { icon, title },
                }),
                Centered(subtitle),
            },
        };
    }

    private static View BuildChangeBadge(Context ctx, ConflictChangeKind kind)
    {
        var theme = ctx.Theme();
        var status = kind switch
        {
            ConflictChangeKind.Added => FileChangeStatus.Added,
            ConflictChangeKind.Deleted => FileChangeStatus.Deleted,
            _ => FileChangeStatus.Modified,
        };
        var text = kind switch
        {
            ConflictChangeKind.Added => "added",
            ConflictChangeKind.Deleted => "deleted",
            _ => "modified",
        };

        // Use the same tinted Lucide status glyph the unstaged/history file rows draw
        // (FileChangeFormatting.StatusIcon), paired with the spelled-out label in the matching
        // status color.
        var icon = new TextView(ctx.Canvas)
        {
            Text = FileChangeFormatting.StatusIcon(status),
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindThemedTextColor(theme, s => s.FileChangeRow.StatusColor(status));

        var label = new TextView(ctx.Canvas)
        {
            Text = text,
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(theme, s => s.FileChangeRow.StatusColor(status));

        return new FlexRowView
        {
            Gap = 6f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { icon, label },
        };
    }

    private static string FormatDate(DateTimeOffset when)
        => when == DateTimeOffset.MinValue ? string.Empty : when.ToLocalTime().ToString("d MMM yyyy");

    private static string Leaf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    // A clickable commit card representing one side of the conflict. Selection state is owned
    // by the parent and shared with the matching checkbox; clicking the card toggles it, and
    // the accent border reflects it.
    private sealed class ConflictCard : HoverableButton
    {
        private readonly State<bool> _checked;

        public ConflictCard(Context ctx, ConflictSideInfo side, State<bool> checkedState) : base(ctx)
        {
            _checked = checkedState;
            Width = CardWidth;

            var theme = ctx.Theme();
            var branchIcon = new TextView(ctx.Canvas)
            {
                Text = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 13f,
                VerticalTextAlignment = TextAlignment.Center,
            };
            branchIcon.BindThemedTextColor(theme, s => s.Palette.TextMedium);

            var name = new TextView(ctx.Canvas)
            {
                Text = side.Label,
                VerticalTextAlignment = TextAlignment.Center,
                TextOverflow = TextOverflow.Ellipsis,
            };
            name.BindThemedTextColor(theme, s => s.Palette.TextStrong);

            var header = new FlexRowView
            {
                Gap = 8f,
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children =
                {
                    BuildCheckIndicator(ctx, _checked),
                    branchIcon,
                    new FlexItem { Grow = 1, Child = name },
                    BuildChangeBadge(ctx, side.Change),
                },
            };

            var divider = new RectView { Height = 1 };
            divider.BindThemedBackgroundColor(theme, s => s.Palette.Border);

            var commitText = new TextView(ctx.Canvas)
            {
                Text = side.ShortSha.Length > 0 ? $"{side.ShortSha}  {side.Subject}" : side.Subject,
                VerticalTextAlignment = TextAlignment.Center,
                TextOverflow = TextOverflow.Ellipsis,
            };
            commitText.BindThemedTextColor(theme, s => s.Palette.TextSecondary);

            var date = new TextView(ctx.Canvas) { Text = FormatDate(side.When), VerticalTextAlignment = TextAlignment.Center };
            date.BindThemedTextColor(theme, s => s.Palette.TextMuted);

            var commitRow = new FlexRowView
            {
                Gap = 8f,
                CrossAxisAlignment = CrossAxisAlignment.Center,
                Children =
                {
                    new FlexItem { Grow = 1, Child = commitText },
                    date,
                },
            };

            var card = new RectView
            {
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(8),
                Children =
                {
                    new PaddingView
                    {
                        Padding = new PaddingStyle { Left = 14, Right = 14, Top = 12, Bottom = 12 },
                        Children =
                        {
                            new ColumnView { Gap = 10, Children = { header, divider, commitRow } },
                        },
                    },
                },
            };
            card.BindThemedBackgroundColor(theme, s => IsHovered.Value ? s.Palette.SurfaceHover : s.Palette.SurfaceRaised);
            card.BindThemedBorderColor(theme, s => BorderColorStyle.All(
                _checked.Value ? s.Palette.Accent : s.Palette.Border));
            SetBackground(card);
        }

        protected override void OnClicked() => _checked.Value = !_checked.Value;

        // A non-interactive checkbox visual in the card's top-left corner. The whole card is
        // the click target (ConflictCard.OnClicked), so this is purely an indicator — making
        // it its own button would double-toggle the shared flag when clicked.
        private static View BuildCheckIndicator(Context ctx, State<bool> checkedState)
        {
            var theme = ctx.Theme();
            var glyph = new TextView(ctx.Canvas)
            {
                FontSize = 12f,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            glyph.BindText(checkedState, c => c ? "✓" : string.Empty);
            glyph.BindThemedTextColor(theme, s => s.Checkbox.CheckGlyph);

            var box = new RectView
            {
                Width = 16f,
                Height = 16f,
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(3),
                Children = { glyph },
            };
            box.BindThemedBackgroundColor(theme, s => checkedState.Value ? s.Checkbox.BoxFillChecked : 0x00000000u);
            box.BindThemedBorderColor(theme, s => BorderColorStyle.All(
                checkedState.Value ? s.Checkbox.BoxFillChecked : s.Checkbox.BoxBorderIdle));
            return box;
        }
    }

    // The connector between the two cards: a horizontal segment runs toward a card only when
    // that side is selected, and a vertical line drops from the center toward the Merge button
    // once anything is selected — mirroring Fork's merge junction. The checkboxes themselves
    // live in the card corners.
    private sealed class MergeJunctionView : ContainerView
    {
        private const float Width_ = 80f;
        private const float LineThickness = 1.5f;

        private readonly State<bool> _theirs;
        private readonly State<bool> _ours;
        private uint _lineColor;

        public MergeJunctionView(IThemeService<ThemeStyles> theme, State<bool> theirsChecked, State<bool> oursChecked)
        {
            Width = Width_;
            _theirs = theirsChecked;
            _ours = oursChecked;
            // Repaint when either side's selection flips so segments appear/disappear.
            this.Bind(_theirs, _ => SetDirty());
            this.Bind(_ours, _ => SetDirty());
            this.BindThemed(theme, s => { _lineColor = s.Palette.Accent; SetDirty(); });
        }

        // Junction height doesn't drive the row (the cards do); it's stretched to the row.
        protected override float MeasureHeightIntrinsic(float availableWidth) => LineThickness;

        protected override void OnDrawSelf(ICanvas c)
        {
            var p = Position;
            var z = GetDrawZIndex();
            var centerY = p.Bottom + p.Height / 2f;
            var centerX = p.Left + p.Width / 2f;
            var style = new RectStyle { BackgroundColor = _lineColor };

            var theirs = _theirs.Value;
            var ours = _ours.Value;

            // Left segment: center → toward the theirs card. Only when theirs is selected.
            if (theirs)
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(p.Left, centerY - LineThickness / 2f, centerX - p.Left, LineThickness),
                    Style = style,
                    ZIndex = z,
                });

            // Right segment: center → toward the ours card. Only when ours is selected.
            if (ours)
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(centerX, centerY - LineThickness / 2f, p.Right - centerX, LineThickness),
                    Style = style,
                    ZIndex = z,
                });

            // Vertical drop toward the Merge button, shown once either side is selected.
            if (theirs || ours)
                c.DrawRect(new DrawRectInputs
                {
                    Position = new RectF(centerX - LineThickness / 2f, p.Bottom, LineThickness, centerY - p.Bottom),
                    Style = style,
                    ZIndex = z,
                });
        }
    }
}
