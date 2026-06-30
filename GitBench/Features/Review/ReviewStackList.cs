using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review;

/// <summary>
/// The stack rail: the review's increments as a dense vertical list, oldest (base) at the top and
/// newest (tip) at the bottom, with the selected row marked "you are here". Clicking a row selects
/// its increment, driving the reused commit-details surface. Reads the pinned
/// <see cref="ReviewWindowViewModel"/> from the build context and renders each increment through
/// <see cref="ReviewStackRow"/>.
/// </summary>
internal sealed record ReviewStackList : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ReviewWindowViewModel>();

        return new ScrollArea
        {
            Style = Theme.ScrollBar(),
            AutoHide = true,
            WheelStep = Scrolling.WheelStep,
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Top = Spacing.Sm, Bottom = Spacing.Sm },
                    Children =
                    [
                        new Column<ReviewIncrement>
                        {
                            Items = Prop.Bind(vm.Increments),
                            Template = inc => new ReviewStackRow
                            {
                                Increment = inc,
                                SelectedSha = vm.SelectedSha,
                                ReviewedShas = vm.ReviewedShas,
                                Mode = vm.Mode,
                                OnClick = () => vm.SelectIncrement(inc.Sha),
                                OnToggleReviewed = () => vm.ToggleReviewed(inc.Sha),
                            },
                            Gap = Spacing.Hair,
                            CrossAxis = CrossAxisAlignment.Stretch,
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// One increment row: a reviewed indicator, the short sha, the commit summary, optional churn, and a
/// secondary author·date line. The selected row takes the shared row-selection fill plus a leading
/// accent bar; a hovered row takes the hover fill. The leading indicator is a click target: a hollow
/// ring when unreviewed, a filled success dot once marked reviewed.
/// </summary>
internal sealed record ReviewStackRow : Widget
{
    private const float RowHeight = 46f;
    private const float IndicatorSize = 12f;

    public required ReviewIncrement Increment { get; init; }
    public required IReadable<string?> SelectedSha { get; init; }
    public required IReadable<IReadOnlySet<string>> ReviewedShas { get; init; }
    public required IReadable<ReviewDiffMode> Mode { get; init; }
    public required Action OnClick { get; init; }
    public required Action OnToggleReviewed { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        var hover = new State<bool>(false);
        var inc = Increment;

        bool IsSelected() => SelectedSha.Value == inc.Sha;
        bool IsReviewed() => ReviewedShas.Value.Contains(inc.Sha);
        // In Combined mode the rail is context only — no single increment is "here", so drop the
        // selected fill + accent (rows still hover and click, which switches back to ByIncrement).
        bool ShowSelected() => IsSelected() && Mode.Value == ReviewDiffMode.ByIncrement;

        var accentBar = new Box
        {
            Width = 3f,
            Background = Prop.Bind(() => ShowSelected() ? theme.Styles.Value.RowSelection.AccentBar : 0u),
        };

        // Hollow ring while unreviewed; fills with the success color once the reviewer marks it. The
        // surrounding padding (below) widens the click target past the 12px dot.
        var indicator = new Box
        {
            Width = IndicatorSize,
            Height = IndicatorSize,
            Background = Prop.Bind(() => IsReviewed() ? theme.Styles.Value.Status.Success : 0u),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Prop.Bind(() => BorderColorStyle.All(
                IsReviewed() ? theme.Styles.Value.Status.Success : theme.Styles.Value.Palette.TextMuted)),
            BorderRadius = BorderRadiusStyle.All(IndicatorSize / 2f),
        };
        var indicatorHit = new Padding
        {
            Amount = PaddingStyle.All(Spacing.Xs),
            Children = [indicator],
        }.WithController(input, () => new ReviewToggleController(OnToggleReviewed));

        var firstLine = new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children = FirstLineChildren(inc),
        };

        var secondLine = new Text
        {
            Value = SecondaryLine(inc),
            FontSize = FontSize.Caption,
            Color = Theme.Color(s => s.Palette.TextDim),
            Overflow = TextOverflow.Ellipsis,
        };

        var textColumn = new Column
        {
            Gap = Spacing.Hair,
            Children = [firstLine, secondLine],
        };

        var content = new Row
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                accentBar,
                new Grow
                {
                    Child = new Padding
                    {
                        Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Md },
                        Children =
                        [
                            new Row
                            {
                                Gap = Spacing.Sm,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children = [indicatorHit, new Grow { Child = textColumn }],
                            },
                        ],
                    },
                },
            ],
        };

        var row = new Box
        {
            Height = RowHeight,
            Background = Prop.Bind(() =>
            {
                var styles = theme.Styles.Value.RowSelection;
                if (ShowSelected()) return styles.Fill;
                return hover.Value ? styles.FillHover : 0u;
            }),
            Children = [content],
        };

        return row.WithController(input, () => new ReviewRowController(hover, OnClick));
    }

    private static IWidget[] FirstLineChildren(ReviewIncrement inc)
    {
        var sha = new Text
        {
            Value = inc.ShortSha,
            FontSize = FontSize.Caption,
            Color = Theme.Color(s => s.Palette.TextMuted),
        };
        var summary = new Grow
        {
            Child = new Text
            {
                Value = inc.Summary,
                FontSize = FontSize.Body,
                Color = Theme.Color(s => s.Palette.TextPrimary),
                Overflow = TextOverflow.Ellipsis,
            },
        };

        if (inc.Added == 0 && inc.Removed == 0)
            return [sha, summary];

        var churn = new Text
        {
            Value = $"+{inc.Added} −{inc.Removed}",
            FontSize = FontSize.Caption,
            Color = Theme.Color(s => s.Palette.TextDim),
        };
        return [sha, summary, churn];
    }

    private static string SecondaryLine(ReviewIncrement inc)
    {
        if (inc.When == DateTimeOffset.MinValue)
            return inc.Author;
        var date = inc.When.ToLocalTime().ToString("yyyy-MM-dd");
        return string.IsNullOrEmpty(inc.Author) ? date : $"{inc.Author} · {date}";
    }
}

// Left-click on the leading indicator toggles the increment's reviewed mark. It sits deeper than the
// row, so it consumes the click on bubbling before the row's selection controller sees it.
internal sealed class ReviewToggleController : KeyboardMouseController
{
    private readonly Action _onToggle;

    public ReviewToggleController(Action onToggle) => _onToggle = onToggle;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left || e.State != InputState.Released) return;
        _onToggle();
        e.Consume();
    }
}

// Hover tracking + left-click selection for a stack row. Right-click context menu is a later phase.
internal sealed class ReviewRowController : KeyboardMouseController
{
    private readonly State<bool> _hover;
    private readonly Action _onClick;

    public ReviewRowController(State<bool> hover, Action onClick)
    {
        _hover = hover;
        _onClick = onClick;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _hover.Value = true;
    public override void OnMouseExit(ref MouseExitEvent e) => _hover.Value = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left || e.State != InputState.Released) return;
        _onClick();
        e.Consume();
    }
}
