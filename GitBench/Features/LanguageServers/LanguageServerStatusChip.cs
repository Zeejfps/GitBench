using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Localization;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal sealed record LanguageServerStatusChip : Widget
{
    private const int EdgeGap = 4;

    private const int Reach = 6;

    protected override IWidget Build(Context ctx)
    {
        var store = ctx.Get<ILanguageServerStore>();
        var bus = ctx.Get<IMessageBus>();
        var loc = ctx.Localization();
        var browser = Model;

        if (store is null) return new Row { Children = [] };

        var state = new Derived<ServerState>(() => browser.Preview.Value is FilePreview.Text text
            ? store.Active.Value.StateFor(text.Path)
            : new ServerState.NotConfigured());

        var dot = new ButtonWidget
        {
            Style = ButtonStyle.BareMuted,
            Visible = Prop.Bind(() => ServerStateText.WorthShowing(state.Value)),
            Command = new Command(() => bus?.Broadcast(new ShowDialogMessage(
                onClose => new LanguageServersDialog { OnClose = onClose }))),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Reach, Right = Reach, Top = Reach, Bottom = Reach },
                    Children = [new ServerStatusDot { State = Prop.Bind(() => state.Value) }],
                },
            ],
        }
            .WithTooltip(Prop.Bind<string?>(() =>
                $"{ServerStateText.Detailed(state.Value, loc.Strings.Value)} — " +
                loc.Strings.Value.LanguageServersChipTooltip))
            .WithController<KbmController>();

        return new Padding
        {
            Amount = new PaddingStyle { Right = EdgeGap },
            Children = [dot],
        };
    }

    public required FileBrowserViewModel Model { get; init; }

}
