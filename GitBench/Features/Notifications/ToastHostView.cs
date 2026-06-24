using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Notifications;

/// <summary>
/// The toast overlay layer: a bottom-right-anchored stack of cards mirroring the live toasts. Mount
/// once near the top of the app's z-order. Empty regions carry no controller, so clicks fall through
/// to the content beneath — only the cards themselves are interactive.
/// </summary>
internal sealed record ToastHostView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ToastsViewModel>();

        return new Column
        {
            MainAxis = MainAxisAlignment.End,
            CrossAxis = CrossAxisAlignment.End,
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Lg),
                    Children =
                    [
                        new Each<ToastItemViewModel>
                        {
                            Items = vm.Items,
                            Template = new ToastCard(),
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.End,
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}
