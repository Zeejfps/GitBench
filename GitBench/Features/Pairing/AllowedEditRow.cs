using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Pairing;

/// <summary>An edit let through because its file is allowed: what it was, and what it changed.</summary>
internal sealed record AllowedEditRow : Widget
{
    public required PairingMessage.AllowedEdit Edit { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var edit = Edit;
        var loc = ctx.Localization();
        IWidget[] preview = edit.Preview.Count > 0 ? [new EditPreviewBlock { Lines = edit.Preview }] : [];
        return new Column
        {
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Text
                {
                    Value = Prop.Bind<string?>(() => loc.Strings.Value.PairingAllowedEdit(edit.Title)),
                    FontSize = FontSize.Caption,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                },
                .. preview,
            ],
        };
    }
}
