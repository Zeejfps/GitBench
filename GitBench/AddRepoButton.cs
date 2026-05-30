using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class AddRepoButton : HoverableButton
{
    public AddRepoButton()
    {
        Height = 30;

        var label = new TextView
        {
            Text = "+  Add Repository",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        label.BindThemedTextColor(s => s.Palette.TextSecondary);

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(6),
            Children = { label }
        };
        BorderedButtonChrome.Bind(background, IsHovered);
        SetBackground(background);
    }

    protected override void OnClicked()
    {
        var path = Context?.Get<IPlatformShell>()?.PickFolder("Open Repository");
        if (string.IsNullOrEmpty(path)) return;
        Context?.Get<IRepoRegistry>()?.Open(path);
    }
}
