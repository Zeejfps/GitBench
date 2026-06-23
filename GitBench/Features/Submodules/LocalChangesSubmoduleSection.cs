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
// listing submodules whose HEAD has drifted from the parent's recorded pointer. Each
// row offers "Stage pointer" (so the pointer update can be committed) and "Reset"
// (re-checkout the recorded SHA). Hidden when there's no drift.
internal sealed class LocalChangesSubmoduleSection : ContainerView
{
    private const int ContentPadding = 10;

    private readonly Context _ctx;
    private readonly ILocalizationService _loc;
    private readonly TextView _headerText;
    private readonly ColumnView _rows;
    private readonly Action<string> _onStage;
    private readonly Action<string> _onReset;

    public LocalChangesSubmoduleSection(Context ctx, Action<string> onStage, Action<string> onReset)
    {
        _ctx = ctx;
        _loc = ctx.Localization();
        _onStage = onStage;
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
    }

    public void SetDrift(IReadOnlyList<SubmoduleInfo> drift)
    {
        _headerText.Text = FileChangesUI.FormatHeader(_loc.Strings.Value.SubmodulesSectionHeader, drift.Count);
        _rows.Children.Clear();
        foreach (var info in drift)
            _rows.Children.Add(BuildRow(info));
    }

    private View BuildRow(SubmoduleInfo info)
    {
        var theme = _ctx.Theme();
        var badgeText = new TextView(_ctx.Canvas)
        {
            Text = "S",
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        badgeText.BindThemedTextColor(theme, s => s.SubmoduleSection.BadgeText);

        var badge = new RectView
        {
            Width = FileChangesUI.BadgeSize,
            Height = FileChangesUI.BadgeSize,
            BorderRadius = BorderRadiusStyle.All(3),
            Children = { badgeText },
        };
        badge.BindThemedBackgroundColor(theme, s => s.SubmoduleSection.BadgeBackground);

        var label = new TextView(_ctx.Canvas)
        {
            Text = $"{info.Path}  ·  {DriftLabel(_loc.Strings.Value, info)}",
        };
        label.BindThemedTextColor(theme, s => s.SubmoduleSection.RowText);

        // Stageable only when the submodule is actually modified (not when it's
        // uninitialized or in a merge conflict — both need different actions).
        var stageButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.ChevronRight,
            Command = new Command(() => _onStage(info.Path), new State<bool>(info.Status == SubmoduleStatus.Modified)),
            Tooltip = _loc.Strings.Value.SubmodulesActionStage,
        }.BuildView(_ctx);

        var resetButton = new LocalChangesHeaderActionButton
        {
            Icon = LucideIcons.X,
            Command = new Command(() => _onReset(info.Path), new State<bool>(info.Status != SubmoduleStatus.NotInitialized)),
            Tooltip = _loc.Strings.Value.SubmodulesActionReset,
        }.BuildView(_ctx);

        return new FlexRowView
        {
            Gap = 8f,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                badge,
                new FlexItem { Grow = 1, Child = label },
                stageButton,
                resetButton,
            },
        };
    }

    private static string DriftLabel(Strings strings, SubmoduleInfo info) => info.Status switch
    {
        SubmoduleStatus.NotInitialized => strings.SubmodulesStatusNotInitialized,
        SubmoduleStatus.MergeConflict  => strings.SubmodulesStatusMergeConflict,
        SubmoduleStatus.Modified       => ShortShaSummary(strings, info),
        _                              => strings.SubmodulesStatusUpToDate,
    };

    private static string ShortShaSummary(Strings strings, SubmoduleInfo info)
    {
        var recorded = ShortSha(info.RecordedSha);
        var current = ShortSha(info.CurrentSha);
        if (string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(current))
            return strings.SubmodulesStatusModified;
        return $"{recorded} → {current}";
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha.Substring(0, 7) : sha);
}
