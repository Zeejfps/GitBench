using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Views;

namespace GitBench.Controls.Dialogs;

internal static class DialogFrame
{
    public const float CloseButtonSize = 28f;
    public const int DefaultPadding = 20;
    public const float DefaultBorderRadius = 10f;
    public const float DefaultButtonHeight = 32f;
    public const float DefaultButtonsGap = 8f;
    public const float DefaultButtonMinWidth = 96f;

    // The only three dialog widths. Height stays dynamic (content-driven); width is a fixed
    // token so dialogs feel like one family instead of each picking its own number.
    //   Compact  — terse confirmations with a line or two of body.
    //   Standard — the default: single-column forms and most confirmations.
    //   Wide     — labeled-row / commit-preview / file-list / error-output layouts.
    public const float WidthCompact = 440f;
    public const float WidthStandard = 480f;
    public const float WidthWide = 600f;

    // Assembles the standard dialog: a pinned header and separator, the body in a scroll region
    // that absorbs any height the frame is short on, and an optional pinned footer below it. The
    // header and footer stay put while the body scrolls, so a dialog capped to the window by
    // CenterView keeps its title and buttons visible no matter how tall its content is.
    public static View Build(Context ctx, string title, Action onClose, View body, View? footer = null, float width = WidthStandard)
    {
        var column = new FlexColumnView
        {
            Gap = 12,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                Header(ctx, title, onClose),
                Separator(ctx),
                new FlexItem { Grow = 1, Child = new DialogScrollRegion(ctx, body) },
            },
        };
        if (footer != null)
            column.Children.Add(footer);
        return Wrap(ctx, column, width);
    }

    public static FlexRowView Header(Context ctx, string title, Action onClose)
    {
        var titleText = new TextView(ctx.Canvas)
        {
            Text = title,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        titleText.BindThemedTextColor(ctx.Theme(), s => s.DialogFrame.TitleText);

        return new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                new ContainerView { Width = CloseButtonSize },
                new FlexItem { Grow = 1, Child = titleText },
                new DialogCloseButton(ctx, onClose),
            },
        };
    }

    public static RectView Separator(Context ctx)
    {
        var view = new RectView { Height = 1 };
        view.BindThemedBackgroundColor(ctx.Theme(), s => s.DialogFrame.HeaderSeparator);
        return view;
    }

    // Standard dialog footer: buttons sit at the bottom-right (Cancel then the action), each
    // at least DefaultButtonMinWidth wide but free to grow for longer labels. A grow-1 spacer
    // pushes them right. This is the single footer layout for every dialog.
    public static FlexRowView ButtonsRow(View cancel, View primary, float gap = DefaultButtonsGap)
        => ButtonsRow(new ContainerView(), cancel, primary, gap);

    // Footer variant with leading content (e.g. a merge/rebase preview chip) in place of the
    // empty spacer. The lead grows to fill, keeping the buttons pinned bottom-right.
    public static FlexRowView ButtonsRow(View lead, View cancel, View primary, float gap = DefaultButtonsGap)
    {
        cancel.MinWidthConstraint = DefaultButtonMinWidth;
        primary.MinWidthConstraint = DefaultButtonMinWidth;
        return new FlexRowView
        {
            Gap = gap,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = lead },
                cancel,
                primary,
            },
        };
    }

    public static TextView ErrorView(Context ctx)
    {
        var view = new TextView(ctx.Canvas)
        {
            Text = string.Empty,
            TextWrap = TextWrap.Wrap,
        };
        view.BindThemedTextColor(ctx.Theme(), s => s.DialogFrame.ErrorText);
        return view;
    }

    public static TextView Label(Context ctx, string text)
    {
        var view = new TextView(ctx.Canvas) { Text = text };
        view.BindThemedTextColor(ctx.Theme(), s => s.DialogBody.SectionHeaderText);
        return view;
    }

    public static TextView Hint(Context ctx, string text, TextWrap wrap = TextWrap.NoWrap)
    {
        var view = new TextView(ctx.Canvas) { Text = text, TextWrap = wrap };
        view.BindThemedTextColor(ctx.Theme(), s => s.DialogBody.RowTextMissing);
        return view;
    }

    public static TextInputView TextInput(Context ctx)
    {
        var view = new TextInputView(ctx.Canvas);
        view.BindThemed(ctx.Theme(), s =>
        {
            view.BackgroundColor = s.TextInput.Background;
            view.TextColor = s.TextInput.Text;
            view.CaretColor = s.TextInput.Caret;
            view.SelectionRectColor = s.TextInput.Selection;
            view.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        return view;
    }

    public static RectView WrapInput(Context ctx, TextInputView input)
    {
        var view = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Height = 28,
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
                    Children = { input },
                },
            },
        };
        var theme = ctx.Theme();
        view.BindThemedBackgroundColor(theme, s => s.TextInput.Background);
        view.BindThemedBorderColor(theme, s => BorderColorStyle.All(s.TextInput.Border));
        return view;
    }

    private static RectView Wrap(Context ctx, View child, float width)
    {
        var view = new RectView
        {
            Width = width,
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DefaultBorderRadius),
            Children =
            {
                new PaddingView
                {
                    Padding = PaddingStyle.All(DefaultPadding),
                    Children = { child },
                },
            },
        };
        var theme = ctx.Theme();
        view.BindThemedBackgroundColor(theme, s => s.DialogFrame.Background);
        view.BindThemedBorderColor(theme, s => BorderColorStyle.All(s.DialogFrame.Border));
        return view;
    }
}
