using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Commits;
using GitBench.Features.Diff;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
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
internal sealed record ConflictResolveView : Widget
{
    private const float CardWidth = 300f;
    private const float ButtonHeight = 34f;
    private const float ButtonStackWidth = 220f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();

        return new Box
        {
            Background = Theme.Color(s => s.DiffView.PanelBackground),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 24, Right = 24, Top = 24, Bottom = 24 },
                    Children =
                    [
                        // The panel is rebuilt per conflict (fresh selection state, cards, commands).
                        // Keyed on the conflict itself so re-selecting the same file is a no-op while a
                        // different conflicted file re-seeds. Non-conflict states render nothing — the
                        // diff pane only mounts this view while a conflict is active.
                        new Switch<DiffRenderState.Conflict?>
                        {
                            Value = new Derived<DiffRenderState.Conflict?>(
                                () => vm.RenderState.Value as DiffRenderState.Conflict),
                            Case = conflict => conflict is null
                                ? Empty.Widget
                                : BuildPanel(ctx, vm, conflict.Path, conflict.Context),
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget BuildPanel(Context ctx, DiffViewModel vm, string path, ConflictContext conflict)
    {
        var loc = ctx.Localization();

        // The two sides' selection state, shared between each card and its checkbox so clicking
        // either toggles the same flag. Theirs is the incoming side (left), ours the current (right).
        var theirsChecked = new State<bool>(false);
        var oursChecked = new State<bool>(false);
        var canMerge = new Derived<bool>(() => theirsChecked.Value || oursChecked.Value);

        return new Column
        {
            Gap = 14,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                BuildTitleRow(),
                new Center { Child = BuildFileNameRow(path) },
                new Center
                {
                    Child = new Row
                    {
                        // Cards sit flush against the inner junction edges; the junction supplies its
                        // own inset, so no inter-item gap here.
                        Gap = 0f,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new ConflictCard { Side = conflict.Theirs, Checked = theirsChecked }
                                .WithController<KbmController>(),
                            new Raw { View = new MergeJunctionView(ctx.Theme(), theirsChecked, oursChecked) },
                            new ConflictCard { Side = conflict.Ours, Checked = oursChecked }
                                .WithController<KbmController>(),
                        ],
                    },
                },
                new Center
                {
                    Child = new Column
                    {
                        Gap = 8,
                        Width = ButtonStackWidth,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            MergeButton(loc, vm, conflict, theirsChecked, oursChecked, canMerge),
                            new SecondaryDialogButton
                            {
                                Label = L.T(s => s.LocalchangesConflictMergeInEditor),
                                Icon = LucideIcons.ExternalLink,
                                Command = new Command(vm.OpenConflictInEditor),
                                Height = ButtonHeight,
                            }.WithController<KbmController>(),
                            // For conflicts already resolved outside the app: stages the file as-is so
                            // the path clears the unmerged state, no side-pick needed.
                            new SecondaryDialogButton
                            {
                                Label = L.T(s => s.LocalchangesConflictMarkResolved),
                                Icon = LucideIcons.CheckSquare,
                                Command = new Command(vm.ResolveMarkResolved),
                                Height = ButtonHeight,
                            }.WithController<KbmController>(),
                        ],
                    },
                },
            ],
        };
    }

    // The button names the action it will take, so the pick is confirmable before clicking:
    // "Choose <branch>" for one side, "Merge both" for both, "Merge" (disabled) when neither.
    private static IWidget MergeButton(
        ILocalizationService loc, DiffViewModel vm, ConflictContext conflict,
        State<bool> theirsChecked, State<bool> oursChecked, IReadable<bool> canMerge) =>
        new ActionDialogButton
        {
            Label = Prop.Bind<string?>(() =>
            {
                var s = loc.Strings.Value;
                return (theirsChecked.Value, oursChecked.Value) switch
                {
                    (true, true) => s.LocalchangesConflictMergeBoth,
                    (true, false) => s.LocalchangesConflictChoose(conflict.Theirs.Label),
                    (false, true) => s.LocalchangesConflictChoose(conflict.Ours.Label),
                    _ => s.LocalchangesConflictMerge,
                };
            }),
            Role = DialogButtonRole.Primary,
            Command = new Command(() =>
            {
                var t = theirsChecked.Value;
                var o = oursChecked.Value;
                if (t && o) vm.ResolveTakeBoth();
                else if (t) vm.ResolveTakeTheirs();
                else if (o) vm.ResolveTakeOurs();
            }, canMerge),
            Height = ButtonHeight,
        }.WithController<KbmController>();

    private static IWidget BuildTitleRow() => new Column
    {
        Gap = 4,
        CrossAxis = CrossAxisAlignment.Stretch,
        Children =
        [
            new Center
            {
                Child = new Row
                {
                    Gap = 8f,
                    CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new Text
                        {
                            Value = LucideIcons.TriangleAlert,
                            FontFamily = LucideIcons.FontFamily,
                            FontSize = 18f,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(s => s.FileChangeRow.StatusModified),
                        },
                        new Text
                        {
                            Value = L.T(s => s.LocalchangesConflictTitle),
                            FontSize = 16f,
                            VAlign = TextAlignment.Center,
                            Color = Theme.Color(s => s.Palette.TextStrong),
                        },
                    ],
                },
            },
            new Center
            {
                Child = new Text
                {
                    Value = L.T(s => s.LocalchangesConflictSubtitle),
                    HAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                },
            },
        ],
    };

    private static IWidget BuildFileNameRow(string path) => new Row
    {
        Gap = 8f,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.File,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 15f,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.Palette.TextMedium),
            },
            new Text
            {
                Value = Leaf(path),
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.Palette.TextStrong),
            },
        ],
    };

    private static IWidget BuildChangeBadge(ConflictChangeKind kind)
    {
        var status = kind switch
        {
            ConflictChangeKind.Added => FileChangeStatus.Added,
            ConflictChangeKind.Deleted => FileChangeStatus.Deleted,
            _ => FileChangeStatus.Modified,
        };
        var text = kind switch
        {
            ConflictChangeKind.Added => L.T(s => s.LocalchangesConflictBadgeAdded),
            ConflictChangeKind.Deleted => L.T(s => s.LocalchangesConflictBadgeDeleted),
            _ => L.T(s => s.LocalchangesConflictBadgeModified),
        };

        // The same tinted Lucide status glyph the unstaged/history file rows draw, paired with the
        // spelled-out label in the matching status color.
        return new Row
        {
            Gap = 6f,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Text
                {
                    Value = FileChangeFormatting.StatusIcon(status),
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = 14f,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.FileChangeRow.StatusColor(status)),
                },
                new Text
                {
                    Value = text,
                    FontSize = 11f,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.FileChangeRow.StatusColor(status)),
                },
            ],
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
    private sealed record ConflictCard : Widget<ButtonState>
    {
        public required ConflictSideInfo Side { get; init; }
        public required State<bool> Checked { get; init; }

        protected override ButtonState CreateState(Context ctx) =>
            new(new Command(() => Checked.Value = !Checked.Value));

        protected override IWidget Build(Context ctx, ButtonState state) => new Box
        {
            Width = CardWidth,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(8),
            Background = Theme.Color(s => state.Hovered.Value ? s.Palette.SurfaceHover : s.Palette.SurfaceRaised),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(
                Checked.Value ? s.Palette.Accent : s.Palette.Border)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = 14, Right = 14, Top = 12, Bottom = 12 },
                    Children =
                    [
                        new Column
                        {
                            Gap = 10,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Row
                                {
                                    Gap = 8,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        BuildCheckIndicator(Checked),
                                        new Text
                                        {
                                            Value = LucideIcons.Branch,
                                            FontFamily = LucideIcons.FontFamily,
                                            FontSize = 13f,
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.Palette.TextMedium),
                                        },
                                        new Grow
                                        {
                                            Child = new Text
                                            {
                                                Value = Side.Label,
                                                VAlign = TextAlignment.Center,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Theme.Color(s => s.Palette.TextStrong),
                                            },
                                        },
                                        BuildChangeBadge(Side.Change),
                                    ],
                                },
                                new Box { Height = 1, Background = Theme.Color(s => s.Palette.Border) },
                                new Row
                                {
                                    Gap = 8,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new Grow
                                        {
                                            Child = new Text
                                            {
                                                Value = Side.ShortSha.Length > 0 ? $"{Side.ShortSha}  {Side.Subject}" : Side.Subject,
                                                VAlign = TextAlignment.Center,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Theme.Color(s => s.Palette.TextSecondary),
                                            },
                                        },
                                        new Text
                                        {
                                            Value = FormatDate(Side.When),
                                            VAlign = TextAlignment.Center,
                                            Color = Theme.Color(s => s.Palette.TextMuted),
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        // A non-interactive checkbox visual in the card's top-left corner. The whole card is
        // the click target (ConflictCard's command), so this is purely an indicator — making
        // it its own button would double-toggle the shared flag when clicked.
        private static IWidget BuildCheckIndicator(State<bool> checkedState) => new Box
        {
            Width = 16f,
            Height = 16f,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Background = Theme.Color(s => checkedState.Value ? s.Checkbox.BoxFillChecked : 0x00000000u),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(
                checkedState.Value ? s.Checkbox.BoxFillChecked : s.Checkbox.BoxBorderIdle)),
            Children =
            [
                new Text
                {
                    Value = checkedState.Bind(string? (c) => c ? "✓" : string.Empty),
                    FontSize = 12f,
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.Checkbox.CheckGlyph),
                },
            ],
        };
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
