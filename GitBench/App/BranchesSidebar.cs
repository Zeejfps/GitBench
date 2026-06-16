using GitBench.Features.Branches;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.App;

/// <summary>Branches rail: the header over the branch list, with a width-persisting splitter.</summary>
internal sealed record BranchesSidebar : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var preferences = ctx.Require<PreferencesService>();
        return new ResizableSidebar
        {
            Content = new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children =
                [
                    new BranchesHeader(),
                    new Grow { Child = new BranchesView() },
                ],
            },
            InitialWidth = preferences.Current.BranchesWidth,
            OnWidthChanged = preferences.SetBranchesWidth,
        };
    }
}
