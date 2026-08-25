using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Notifications;

/// <summary>
/// The status bar's toast slot: the live toast, pinned to the bar's trailing edge. Mount it as the
/// top layer of a stack over the bar's own content — the chip covers the ambient readouts it lands
/// on for as long as it's up, and the rest of the slot carries no controller, so clicks there fall
/// through to the bar beneath.
/// </summary>
internal sealed record ToastSlotView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ToastsViewModel>();

        return new Each<ToastItemViewModel>
        {
            Items = vm.Items,
            Template = new ToastChip(),
            ListAxis = Axis.Horizontal,
            MainAxis = MainAxisAlignment.End,
            CrossAxis = CrossAxisAlignment.Center,
        }.BindVm(vm);
    }
}
