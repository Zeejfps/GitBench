using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

/// <summary>
/// Picks the identity profile an operation runs under. <see cref="Selected"/> null means
/// "auto-detect from the remote"; when that auto-detection has already landed on a profile,
/// <see cref="AutoMatched"/> shows its name so the user can see the choice was made for them.
/// </summary>
internal sealed record IdentityProfileDropdown : Widget
{
    /// <summary>Explicitly chosen profile id; null is auto-detect.</summary>
    public required State<Guid?> Selected { get; init; }

    /// <summary>The profile that will actually apply — the chosen one, or the auto match.</summary>
    public required IReadable<IdentityProfile?> Effective { get; init; }

    /// <summary>True while <see cref="Effective"/> came from the remote rather than a choice.</summary>
    public required IReadable<bool> AutoMatched { get; init; }

    public required IReadOnlyList<IdentityProfile> Profiles { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;

        return new DropdownWidget
        {
            Height = 30,
            Gap = Spacing.Sm,
            Children =
            [
                new Text
                {
                    Value = LucideIcons.PencilLine,
                    FontFamily = LucideIcons.FontFamily,
                    FontSize = FontSize.Default,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Grow
                {
                    Child = new Text
                    {
                        VAlign = TextAlignment.Center,
                        Value = Prop.Bind<string?>(() => Label(s)),
                        Color = Theme.Color(t => Effective.Value == null
                            ? t.DialogBody.RowTextMissing
                            : t.DialogFrame.TitleText),
                    },
                },
            ],
        }.WithMenuController(rect =>
        {
            var items = new List<RepoBarContextMenu.Item>(Profiles.Count + 2)
            {
                new(s.StatusbarIdentityAutoDetect, () => Selected.Value = null, Checked: Selected.Value == null),
            };
            if (Profiles.Count > 0) items.Add(RepoBarContextMenu.Separator);
            foreach (var profile in Profiles)
            {
                var captured = profile;
                items.Add(new RepoBarContextMenu.Item(
                    captured.DisplayName,
                    () => Selected.Value = captured.Id,
                    Checked: Selected.Value == captured.Id));
            }
            RepoBarContextMenu.Show(ctx, rect.BottomLeft, items);
        });
    }

    private string Label(Strings s)
    {
        var effective = Effective.Value;
        if (effective == null) return s.StatusbarIdentityAutoDetect;
        return AutoMatched.Value ? s.IdentityAutoMatched(effective.DisplayName) : effective.DisplayName;
    }
}
