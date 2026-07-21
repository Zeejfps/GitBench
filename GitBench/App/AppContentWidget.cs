using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

internal sealed record AppContentWidget : Widget<AppViewModel>
{
    protected override IWidget Build(Context ctx, AppViewModel vm)
    {
        return new Column
        {
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new UpdateBannerView(),
                new Grow
                {
                    Child = new Switch<bool>
                    {
                        Value = vm.HasRepos,
                        Case = has => has
                            ? new MainScreenWidget()
                            : new WelcomeScreenWidget(),
                    },
                },
            ],
        };
    }
}
