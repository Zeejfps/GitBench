using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// Header strip for the embedded diff panes (Local Changes, Commit Details): a collapse chevron and
/// "Diff View" title, an LFS badge, and full-file / open-in-window buttons. The whole bar is the
/// collapse toggle — a press flips <see cref="DiffViewModel.IsCollapsed"/>; the nested buttons consume
/// their own clicks first. Live hover/press state lives on an <see cref="ButtonState"/> exposed
/// as the widget's <see cref="IInteractable"/> surface, so the host attaches the controller
/// (<c>header.WithController&lt;KbmController&gt;()</c>) and provides the <see cref="DiffViewModel"/> the
/// header reads collapse, mode, and LFS status from.
/// </summary>
internal sealed record DiffPaneHeaderWidget : Widget<ButtonState>
{
    // Height of the always-visible header strip. The host pins the collapsed pane to exactly this
    // height, keeping the chevron clickable.
    public const float HeaderHeight = 24f;

    /// <summary>
    /// Whether the bar acts as a collapse toggle. True (default) for the stacked embedded panes
    /// (Local Changes, Commit Details) where the header collapses the diff below it. False for the
    /// tabbed commit-details surface, where the diff fills its own tab and there is nothing to
    /// collapse into — the chevron is dropped and pressing the bar does nothing.
    /// </summary>
    public bool Collapsible { get; init; } = true;

    protected override ButtonState CreateState(Context ctx) =>
        new(new Command(Collapsible ? ctx.Require<DiffViewModel>().ToggleCollapse : static () => { }));

    protected override IWidget Build(Context ctx, ButtonState state)
    {
        var vm = ctx.Require<DiffViewModel>();
        // Present only in a review window's subtree; elsewhere null ⇒ no Viewed toggle.
        var reviewed = ctx.Get<IReviewedFileTracker>();

        return new Box
        {
            Height = HeaderHeight,
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            Background = Theme.Color(s => s.DiffView.HeaderBackground(state)),
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                Top = s.DiffView.HeaderBorderTop,
                Bottom = s.DiffView.HeaderBorderBottom,
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = RowChildren(vm, state, reviewed),
                        },
                    ],
                },
            ],
        };
    }

    private IWidget[] RowChildren(DiffViewModel vm, ButtonState state, IReviewedFileTracker? reviewed)
    {
        var title = new Grow
        {
            Child = new Text
            {
                Value = L.T(s => s.DiffHeaderTitle),
                FontSize = FontSize.Body,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DiffView.HeaderButtonColor(state)),
            },
        };
        var trailing = new List<IWidget>
        {
            title,
            new LfsBadgeWidget { Status = Prop.Bind(vm.LfsStatus) },
            FullFileToggleButton(vm),
            OpenInWindowButton(vm),
        };
        if (reviewed != null)
            trailing.Add(ViewedToggleButton(vm, reviewed));

        if (!Collapsible) return trailing.ToArray();

        var chevron = new Text
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = FontSize.Body,
            Width = 16f,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Value = vm.IsCollapsed.Bind(string? (c) =>
                c ? LucideIcons.ChevronUp : LucideIcons.ChevronDown),
            Color = Theme.Color(s => s.DiffView.HeaderButtonColor(state)),
        };
        return [chevron, .. trailing];
    }

    // Toggles the diff body between the normal diff and the after-side full file. Tinted while active
    // so it reads as an engaged toggle, not a one-shot action.
    private static IWidget FullFileToggleButton(DiffViewModel vm) =>
        new ButtonWidget
        {
            Style = ButtonStyle.Bare(s => Theme.Color(t => vm.Mode.Value == DiffViewMode.FullFile
                ? t.DiffView.HeaderToggleActive
                : t.DiffView.HeaderButtonColor(s))),
            Command = new Command(vm.ToggleFullFile),
            Children = [new ButtonIcon { Value = LucideIcons.FileText, FontSize = FontSize.Body }],
        }.WithTooltip(L.T(s => s.DiffFullfileToggleTooltip))
            .WithController<KbmController>();

    private static IWidget OpenInWindowButton(DiffViewModel vm) =>
        new ButtonWidget
        {
            Style = ButtonStyle.Bare(s => Theme.Color(t => t.DiffView.HeaderButtonColor(s))),
            Command = new Command(vm.RequestOpenInWindow),
            Children = [new ButtonIcon { Value = LucideIcons.ExternalLink, FontSize = FontSize.Body }],
        }.WithTooltip(L.T(s => s.DiffOpenWindowTooltip))
            .WithController<KbmController>();

    // The GitHub-style per-file "Viewed" toggle, shown only when a reviewed-file tracker is in scope
    // (the review window). Tints success and flips the checkbox glyph once the file is marked viewed.
    // Marks key off ReviewFileKey so commit and combined-range diffs each track their own identity.
    private static IWidget ViewedToggleButton(DiffViewModel vm, IReviewedFileTracker reviewed)
    {
        bool IsViewed()
        {
            _ = reviewed.Revision.Value;
            var target = vm.Target.Value;
            return ReviewFileKey.ForTarget(target) is { } key && reviewed.IsViewed(key, target!.Path);
        }

        return new ButtonWidget
        {
            Style = ButtonStyle.Bare(s => Theme.Color(t => IsViewed()
                ? t.Status.Success
                : t.DiffView.HeaderButtonColor(s))),
            Command = new Command(() =>
            {
                var target = vm.Target.Value;
                if (ReviewFileKey.ForTarget(target) is { } key) reviewed.ToggleViewed(key, target!.Path);
            }),
            Children =
            [
                new ButtonIcon
                {
                    FontSize = FontSize.Body,
                    Value = Prop.Bind<string?>(() => IsViewed() ? LucideIcons.CheckSquare : LucideIcons.Square),
                },
                new ButtonLabel { Value = L.T(s => s.ReviewViewed) },
            ],
        }.WithController<KbmController>();
    }
}
