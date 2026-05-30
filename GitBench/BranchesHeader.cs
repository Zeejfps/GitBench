using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

internal sealed class BranchesHeader : MultiChildView, IBind<BranchesHeaderViewModel>
{
    private const float HeaderHeight = 44f;
    private const int HorizontalPadding = 8;

    private readonly TextView _iconView;
    private readonly TextView _prefixView;
    private readonly TextView _nameView;
    private readonly PaddingView _content;

    public BranchesHeader()
    {
        Height = HeaderHeight;

        _iconView = new TextView
        {
            Text = LucideIcons.Branch,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 15f,
            VerticalTextAlignment = TextAlignment.Center,
        };

        _prefixView = new TextView
        {
            VerticalTextAlignment = TextAlignment.Center,
        };
        _prefixView.BindThemedTextColor(s => s.BranchesHeader.PrefixText);

        _nameView = new TextView
        {
            FontSize = 18f,
            FontWeight = FontWeight.Bold,
            VerticalTextAlignment = TextAlignment.Center,
        };

        _content = new PaddingView
        {
            Padding = new PaddingStyle { Left = 6, Right = 6 },
            Children =
            {
                new RowView
                {
                    Gap = 6,
                    Children = { _iconView, _prefixView, _nameView },
                },
            },
        };

        var headerBar = new RectView
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
            Children =
            {
                new FlexRowView
                {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = { _content },
                },
            },
        };
        headerBar.BindThemedBackgroundColor(s => s.BranchesHeader.Background);
        headerBar.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.BranchesHeader.BorderBottom });
        AddChildToSelf(headerBar);

        this.UseViewModel(this);
    }

    public void Bind(BranchesHeaderViewModel vm)
    {
        _iconView.BindThemedTextColor(s =>
            vm.IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText);
        _prefixView.BindText(vm.IsDetached, d => d ? "at" : "on");
        _nameView.BindText(vm.BranchName);
        _nameView.BindThemedTextColor(s =>
            vm.IsDetached.Value ? s.BranchesHeader.DetachedText : s.BranchesHeader.ActiveText);
        _content.BindIsVisible(vm.BranchName, n => !string.IsNullOrEmpty(n));
    }
}
