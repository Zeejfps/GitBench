using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Platform;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>
/// "About GitBench" modal: app icon, name, version, a link to the repo, and copyright.
/// Opened from the macOS app menu (and reusable from a Help menu on other platforms).
/// </summary>
internal sealed record AboutDialog : Widget
{
    private const string RepoUrl = "https://github.com/Zeejfps/GitBench";

    /// <summary>
    /// Image id of the app icon, set by startup once it's loaded into the canvas. Null when the
    /// icon couldn't be loaded — the dialog then falls back to a glyph rather than referencing an
    /// unknown id (which would throw when the ImageView measures).
    /// </summary>
    public static string? IconImageId { get; set; }

    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var theme = ctx.Theme();

        var closeRow = new FlexRowView
        {
            Height = 28,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = new ContainerView() },
                new DialogCloseButton(ctx, OnClose),
            },
        };

        var name = new Text
        {
            Value = "GitBench",
            FontSize = 22,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.DialogFrame.TitleText),
        }.BuildView(ctx);

        var version = new Text
        {
            Value = $"v{AppVersion.Display}",
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.Palette.TextSecondary),
        }.BuildView(ctx);

        var repoButton = new DialogButton(
            ctx,
            "View on GitHub",
            () => ctx.Get<IPlatformShell>()?.OpenUrl(RepoUrl),
            DialogButtonRole.Primary)
        {
            Height = DialogFrame.DefaultButtonHeight,
            MinWidthConstraint = DialogFrame.DefaultButtonMinWidth,
        };

        var copyright = new Text
        {
            Value = "© 2026 Zee Vasilyev",
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.Palette.TextMuted),
        }.BuildView(ctx);

        var content = new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                BuildLogo(ctx),
                name,
                version,
                repoButton,
                copyright,
            },
        };

        var frame = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(10),
            Padding = PaddingStyle.All(20),
            Children =
            {
                new FlexColumnView
                {
                    Gap = 12,
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Children = { closeRow, content },
                },
            },
        };
        frame.BindBackgroundColor(() => theme.Styles.Value.DialogFrame.Background);
        frame.BindBorderColor(() => BorderColorStyle.All(theme.Styles.Value.DialogFrame.Border));

        var root = new ContainerView { Width = 360 };
        root.Children.Add(frame);
        root.UseController(ctx.Require<InputSystem>(), () => new DialogKbmController(OnClose));
        return root;
    }

    private static View BuildLogo(Context ctx)
    {
        if (IconImageId != null)
            return new ImageView(ctx.Canvas) { ImageId = IconImageId, Width = 84, Height = 84 };

        var glyph = new Text
        {
            Value = LucideIcons.FolderGit2,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 60,
            HAlign = TextAlignment.Center,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.Palette.Accent),
        }.BuildView(ctx);
        return new ContainerView { Width = 84, Height = 84, Children = { glyph } };
    }
}
