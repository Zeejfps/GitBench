using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitGui;

// Section rendered above the unstaged/staged file panels in LocalChangesContentView,
// listing submodules whose HEAD has drifted from the parent's recorded pointer. Each
// row offers "Stage pointer" (so the pointer update can be committed) and "Reset"
// (re-checkout the recorded SHA). Hidden when there's no drift.
internal sealed class LocalChangesSubmoduleSection : MultiChildView
{
    private const int ContentPadding = 10;

    private readonly TextView _headerText;
    private readonly ColumnView _rows;
    private readonly Action<string> _onStage;
    private readonly Action<string> _onReset;

    public LocalChangesSubmoduleSection(Action<string> onStage, Action<string> onReset)
    {
        _onStage = onStage;
        _onReset = onReset;

        _headerText = FileChangesUI.CreateHeaderText("Submodules");
        _rows = new ColumnView { Gap = FileChangesUI.RowGap };

        AddChildToSelf(new ColumnView
        {
            Children =
            {
                FileChangesUI.CreateHeaderBar(_headerText),
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
        _headerText.Text = FileChangesUI.FormatHeader("Submodules", drift.Count);
        _rows.Children.Clear();
        foreach (var info in drift)
            _rows.Children.Add(BuildRow(info));
    }

    private View BuildRow(SubmoduleInfo info)
    {
        var badgeText = new TextView
        {
            Text = "S",
            FontSize = 11f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        badgeText.BindThemedTextColor(s => s.SubmoduleSection.BadgeText);

        var badge = new RectView
        {
            Width = FileChangesUI.BadgeSize,
            Height = FileChangesUI.BadgeSize,
            BorderRadius = BorderRadiusStyle.All(3),
            Children = { badgeText },
        };
        badge.BindThemedBackgroundColor(s => s.SubmoduleSection.BadgeBackground);

        var label = new TextView
        {
            Text = $"{info.Path}  ·  {DriftLabel(info)}",
        };
        label.BindThemedTextColor(s => s.SubmoduleSection.RowText);

        var stageButton = new LocalChangesHeaderActionButton(
            LucideIcons.ChevronRight, () => _onStage(info.Path), "Stage pointer update");
        // Stageable only when the submodule is actually modified (not when it's
        // uninitialized or in a merge conflict — both need different actions).
        stageButton.IsEnabled.Value = info.Status == SubmoduleStatus.Modified;

        var resetButton = new LocalChangesHeaderActionButton(
            LucideIcons.X, () => _onReset(info.Path), "Reset to recorded SHA");
        resetButton.IsEnabled.Value = info.Status != SubmoduleStatus.NotInitialized;

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

    private static string DriftLabel(SubmoduleInfo info) => info.Status switch
    {
        SubmoduleStatus.NotInitialized => "not initialized",
        SubmoduleStatus.MergeConflict  => "merge conflict",
        SubmoduleStatus.Modified       => ShortShaSummary(info),
        _                              => "up to date",
    };

    private static string ShortShaSummary(SubmoduleInfo info)
    {
        var recorded = ShortSha(info.RecordedSha);
        var current = ShortSha(info.CurrentSha);
        if (string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(current))
            return "modified";
        return $"{recorded} → {current}";
    }

    private static string ShortSha(string? sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha.Substring(0, 7) : sha);
}
