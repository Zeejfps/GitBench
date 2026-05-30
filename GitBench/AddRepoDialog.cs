using ZGF.Gui;
using ZGF.Gui.Views;

namespace GitGui;

public sealed class AddRepoDialog : MultiChildView
{
    public AddRepoDialog(Action onClose)
    {
        Width = 360;
        Height = 230;

        AddChildToSelf(DialogFrame.Build("Add Repository", onClose, new FlexColumnView
        {
            Gap = 14,
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexColumnView
                {
                    Gap = 8,
                    CrossAxisAlignment = CrossAxisAlignment.Stretch,
                    Children =
                    {
                        new DialogButton("Clone", () => { /* TODO */ }) { Height = 40 },
                        new DialogButton("Open", () =>
                        {
                            var shell = Context?.Get<IPlatformShell>();
                            var path = shell?.PickFolder("Open Repository");
                            if (string.IsNullOrEmpty(path)) return;
                            Context?.Get<IRepoRegistry>()?.Open(path);
                            onClose();
                        }) { Height = 40 },
                        new DialogButton("New", () => { /* TODO */ }) { Height = 40 },
                    }
                },
            }
        }));
    }
}