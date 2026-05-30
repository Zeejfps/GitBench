using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal static class DialogFrame
{
    public const float CloseButtonSize = 28f;
    public const int DefaultPadding = 20;
    public const float DefaultBorderRadius = 10f;
    public const float DefaultButtonHeight = 32f;
    public const float DefaultButtonsGap = 8f;

    public static View Build(string title, Action onClose, FlexColumnView body)
    {
        body.Children.Insert(0, Separator());
        body.Children.Insert(0, Header(title, onClose));
        return Wrap(body);
    }

    public static FlexRowView Header(string title, Action onClose)
    {
        var titleText = new TextView
        {
            Text = title,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        titleText.BindThemedTextColor(s => s.DialogFrame.TitleText);

        return new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Height = 28,
            Children =
            {
                new MultiChildView { Width = CloseButtonSize },
                new FlexItem { Grow = 1, Child = titleText },
                new DialogCloseButton(onClose),
            },
        };
    }

    public static RectView Separator()
    {
        var view = new RectView { Height = 1 };
        view.BindThemedBackgroundColor(s => s.DialogFrame.HeaderSeparator);
        return view;
    }

    public static FlexRowView ButtonsRow(MultiChildView cancel, MultiChildView primary, float gap = DefaultButtonsGap) => new()
    {
        Gap = gap,
        CrossAxisAlignment = CrossAxisAlignment.Stretch,
        Children =
        {
            new FlexItem { Grow = 1, Child = cancel },
            new FlexItem { Grow = 1, Child = primary },
        },
    };

    public static TextView ErrorView()
    {
        var view = new TextView
        {
            Text = string.Empty,
            TextWrap = TextWrap.Wrap,
        };
        view.BindThemedTextColor(s => s.DialogFrame.ErrorText);
        return view;
    }

    public static TextView Label(string text)
    {
        var view = new TextView { Text = text };
        view.BindThemedTextColor(s => s.DialogBody.SectionHeaderText);
        return view;
    }

    public static TextView Hint(string text, TextWrap wrap = TextWrap.NoWrap)
    {
        var view = new TextView { Text = text, TextWrap = wrap };
        view.BindThemedTextColor(s => s.DialogBody.RowTextMissing);
        return view;
    }

    public static TextInputView TextInput()
    {
        var view = new TextInputView { TextWrap = TextWrap.NoWrap };
        view.BindThemed(s =>
        {
            view.BackgroundColor = s.TextInput.Background;
            view.TextColor = s.TextInput.Text;
            view.CaretColor = s.TextInput.Caret;
            view.SelectionRectColor = s.TextInput.Selection;
            view.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        return view;
    }

    public static RectView WrapInput(TextInputView input)
    {
        var view = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 6, Right = 6, Top = 4, Bottom = 4 },
            Height = 28,
            Children = { input },
        };
        view.BindThemedBackgroundColor(s => s.TextInput.Background);
        view.BindThemedBorderColor(s => BorderColorStyle.All(s.TextInput.Border));
        return view;
    }

    private static RectView Wrap(View child)
    {
        var view = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(DefaultBorderRadius),
            Padding = PaddingStyle.All(DefaultPadding),
            Children = { child },
        };
        view.BindThemedBackgroundColor(s => s.DialogFrame.Background);
        view.BindThemedBorderColor(s => BorderColorStyle.All(s.DialogFrame.Border));
        return view;
    }
}
