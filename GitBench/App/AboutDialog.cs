using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;

namespace GitBench.App;

/// <summary>
/// "About GitBench" modal: app icon, name, version, a link to the repo, and copyright.
/// Opened from the macOS app menu (and reusable from a Help menu on other platforms).
/// </summary>
public sealed class AboutDialog : MultiChildView
{
    private const string RepoUrl = "https://github.com/Zeejfps/GitBench";

    /// <summary>
    /// Image id of the app icon, set by startup once it's loaded into the canvas. Null when the
    /// icon couldn't be loaded — the dialog then falls back to a glyph rather than referencing an
    /// unknown id (which would throw when the ImageView measures).
    /// </summary>
    public static string? IconImageId { get; set; }

    public AboutDialog(Action onClose)
    {
        Width = 360;

        var closeRow = new FlexRowView
        {
            Height = 28,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = new MultiChildView() },
                new DialogCloseButton(onClose),
            },
        };

        var name = new TextView
        {
            Text = "GitBench",
            FontSize = 22,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        name.BindThemedTextColor(s => s.DialogFrame.TitleText);

        var version = new TextView
        {
            Text = $"v{AppVersion.Display}",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        version.BindThemedTextColor(s => s.Palette.TextSecondary);

        // Pull IPlatformShell lazily off the context — the dialog is constructed before it's
        // attached, so the click closure resolves it at invoke time (same pattern as the copy
        // button in OperationErrorDialog).
        var repoButton = new DialogButton(
            "View on GitHub",
            () => Context?.Get<IPlatformShell>()?.OpenUrl(RepoUrl),
            DialogButtonRole.Primary)
        {
            Height = DialogFrame.DefaultButtonHeight,
            MinWidthConstraint = DialogFrame.DefaultButtonMinWidth,
        };

        var copyright = new TextView
        {
            Text = "© 2026 Zee Vasilyev",
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        copyright.BindThemedTextColor(s => s.Palette.TextMuted);

        var content = new FlexColumnView
        {
            Gap = 10,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                BuildLogo(),
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
        frame.BindThemedBackgroundColor(s => s.DialogFrame.Background);
        frame.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));
        AddChildToSelf(frame);

        this.UseController(_ => new DialogKbmController(onClose));
    }

    private static View BuildLogo()
    {
        if (IconImageId != null)
            return new ImageView { ImageId = IconImageId, Width = 84, Height = 84 };

        var glyph = new TextView
        {
            Text = LucideIcons.FolderGit2,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 60,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        glyph.BindThemedTextColor(s => s.Palette.Accent);
        return new MultiChildView { Width = 84, Height = 84, Children = { glyph } };
    }
}
