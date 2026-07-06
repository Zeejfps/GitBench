using GitBench.Controls;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

// Section rendered above the unstaged/staged file panels in LocalChangesContentView,
// listing submodules that need attention from the parent: not-initialized ones (offer
// "Initialize" — clone/check out via `git submodule update --init`) and merge-conflicted
// ones (offer "Reset" — re-checkout the recorded SHA). Plain pointer drift shows in the
// file list instead (see RepoSnapshotStore.BuildLocalData). Hidden when empty.
internal sealed class LocalChangesSubmoduleSection : ContainerView
{
    private const int ContentPadding = 10;

    private readonly Context _ctx;
    private readonly ILocalizationService _loc;
    private readonly TextView _headerText;
    private readonly ColumnView _rows;
    private readonly Action<string> _onInit;
    private readonly Action<string> _onReset;

    private IReadOnlyList<SubmoduleInfo> _drift = Array.Empty<SubmoduleInfo>();

    public LocalChangesSubmoduleSection(Context ctx, Action<string> onInit, Action<string> onReset)
    {
        _ctx = ctx;
        _loc = ctx.Localization();
        _onInit = onInit;
        _onReset = onReset;

        _headerText = FileChangesUI.CreateHeaderText(ctx, _loc.Strings.Value.SubmodulesSectionHeader);
        _rows = new ColumnView { Gap = FileChangesUI.RowGap };

        AddChildToSelf(new ColumnView
        {
            Children =
            {
                FileChangesUI.CreateHeaderBar(ctx, _headerText),
                new PaddingView
                {
                    Padding = new PaddingStyle
                    {
                        Left = ContentPadding,
                        Right = ContentPadding,
                        Top = ContentPadding,
                        Bottom = ContentPadding,
                    },
                    Children = { _rows },
                },
            },
        });

        // Header, drift-row labels, and the action tooltips are all localized; re-render the
        // whole section on a live locale switch by replaying the current drift list.
        this.Bind(_loc.Strings, _ => Render());
    }

    public void SetDrift(IReadOnlyList<SubmoduleInfo> drift)
    {
        _drift = drift;
        Render();
    }

    private void Render()
    {
        _headerText.Text = FileChangesUI.FormatHeader(_loc.Strings.Value.SubmodulesSectionHeader, _drift.Count);
        _rows.Children.Clear();
        foreach (var info in _drift)
            _rows.Children.Add(BuildRow(info));
    }

    private View BuildRow(SubmoduleInfo info)
    {
        var theme = _ctx.Theme();
        var badgeText = new TextView(_ctx.Canvas)
        {
            Text = "S",
            FontSize = FontSize.Caption,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        badgeText.BindThemedTextColor(theme, s => s.SubmoduleSection.BadgeText);

        var badge = new RectView
        {
            Width = FileChangesUI.BadgeSize,
            Height = FileChangesUI.BadgeSize,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children = { badgeText },
        };
        badge.BindThemedBackgroundColor(theme, s => s.SubmoduleSection.BadgeBackground);

        var label = new TextView(_ctx.Canvas)
        {
            Text = $"{info.Path}  ·  {DriftLabel(_loc.Strings.Value, info)}",
        };
        label.BindThemedTextColor(theme, s => s.SubmoduleSection.RowText);

        var actionButton = info.Status == SubmoduleStatus.NotInitialized
            ? new LocalChangesHeaderActionButton
            {
                Icon = LucideIcons.Pull,
                Command = new Command(() => _onInit(info.Path)),
                Tooltip = _loc.Strings.Value.SubmodulesActionInit,
            }.BuildView(_ctx)
            : new LocalChangesHeaderActionButton
            {
                Icon = LucideIcons.X,
                Command = new Command(() => _onReset(info.Path)),
                Tooltip = _loc.Strings.Value.SubmodulesActionReset,
            }.BuildView(_ctx);

        return new FlexRowView
        {
            Gap = Spacing.Md,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                badge,
                new FlexItem { Grow = 1, Child = label },
                actionButton,
            },
        };
    }

    private static string DriftLabel(Strings strings, SubmoduleInfo info) => info.Status switch
    {
        SubmoduleStatus.NotInitialized => strings.SubmodulesStatusNotInitialized,
        SubmoduleStatus.MergeConflict  => strings.SubmodulesStatusMergeConflict,
        _                              => strings.SubmodulesStatusUpToDate,
    };
}
