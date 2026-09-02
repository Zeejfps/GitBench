using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Lsp.Configuration;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal sealed record LanguageServersDialog : Widget
{
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = new LanguageServersViewModel(
            ctx.Require<ILanguageServerStore>(),
            ctx.Require<ILocalizationService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Get<IMessageBus>(),
            ctx.Get<IClipboard>());

        return new Dialog
        {
            Title = s.LanguageServersTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            CancelLabel = s.CommonClose,
            Action = (s.LanguageServersReload, DialogButtonRole.Primary),
            Command = vm.ReloadCommand,
            Body =
            [
                new Text
                {
                    Value = s.LanguageServersDescription,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Box
                {
                    Background = Theme.Color(t => t.Palette.SurfaceSunken),
                    BorderSize = BorderSizeStyle.All(1),
                    BorderRadius = BorderRadiusStyle.All(Radius.Md),
                    BorderColor = Theme.BorderColor(t => BorderColorStyle.All(t.Palette.BorderSubtle)),
                    Children =
                    [
                        new Padding
                        {
                            Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Xs, Top = Spacing.Xs, Bottom = Spacing.Xs },
                            Children =
                            [
                                new Row
                                {
                                    Gap = Spacing.Xs,
                                    CrossAxis = CrossAxisAlignment.Center,
                                    Children =
                                    [
                                        new Grow
                                        {
                                            Child = new Text
                                            {
                                                Value = vm.ConfigPath,
                                                Wrap = TextWrap.Wrap,
                                                FontSize = FontSize.Caption,
                                                FontFamily = MonoFonts.Regular,
                                                Color = Theme.Color(t => t.DialogBody.BodyText),
                                            },
                                        },
                                        new CopyIconButton
                                        {
                                            Label = static x => x.FileBrowserCopyPath,
                                            GetText = () => vm.ConfigPath,
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
                new Text
                {
                    Value = s.LanguageServersNoConfig,
                    Wrap = TextWrap.Wrap,
                    Visible = Prop.Bind(() => !vm.HasConfigFile.Value),
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new Show
                {
                    When = new Derived<bool>(() => vm.ReloadResult.Value is not null),
                    Then = () => new Text
                    {
                        Value = Prop.Bind(() => vm.ReloadResult.Value),
                        Wrap = TextWrap.Wrap,
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersConfiguredHeading,
                    Empty = s.LanguageServersNoneConfigured,
                    IsEmpty = new Derived<bool>(() => vm.Servers.Value.Count == 0),
                    Child = new Column<ConfiguredServer>
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Items = Prop.Bind(vm.Servers),
                        Template = server => new LanguageServerRow { Model = vm, Server = server },
                    },
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersSuggestionsHeading,
                    IsEmpty = new Derived<bool>(() => vm.Suggestions.Value.Count == 0),
                    Child = new Column
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new Column<StarterServer>
                            {
                                Gap = Spacing.Xs,
                                CrossAxis = CrossAxisAlignment.Stretch,
                                Items = Prop.Bind(vm.Suggestions),
                                Template = suggestion => new LanguageServerSuggestionRow
                                {
                                    Model = vm,
                                    Server = suggestion,
                                },
                            },
                            new Row
                            {
                                MainAxis = MainAxisAlignment.End,
                                Children =
                                [
                                    new ButtonWidget
                                    {
                                        Style = ButtonStyle.Outline(static t => t.Palette.Accent),
                                        Visible = Prop.Bind(vm.CanCreateConfig),
                                        Command = new Command(vm.CreateConfig),
                                        Children =
                                        [
                                            new ButtonLabel { Value = s.LanguageServersCreateConfig },
                                        ],
                                    }.WithController<KbmController>(),
                                ],
                            },
                        ],
                    },
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersProblemsHeading,
                    IsEmpty = new Derived<bool>(() => vm.Problems.Value.Count == 0),
                    Child = new Column<ConfigProblem>
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Items = Prop.Bind(vm.Problems),
                        Template = problem => new Text
                        {
                            Value = s.LanguageServersProblem(problem.Subject, problem.Message),
                            Wrap = TextWrap.Wrap,
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                        },
                    },
                },
            ],
        };
    }
}

internal sealed record LanguageServerSection : Widget
{
    public required string Heading { get; init; }
    public required IReadable<bool> IsEmpty { get; init; }
    public required IWidget Child { get; init; }

    public string? Empty { get; init; }

    protected override IWidget Build(Context ctx) => new Column
    {
        Gap = Spacing.Xs,
        CrossAxis = CrossAxisAlignment.Stretch,
        Visible = Prop.Bind(() => Empty is not null || !IsEmpty.Value),
        Children =
        [
            new Text
            {
                Value = Heading,
                Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
            },
            new Text
            {
                Value = Empty,
                Wrap = TextWrap.Wrap,
                FontSize = FontSize.Caption,
                Visible = Prop.Bind(() => Empty is not null && IsEmpty.Value),
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            },
            Child,
        ],
    };
}

internal sealed record LanguageServerRow : Widget
{
    public required LanguageServersViewModel Model { get; init; }
    public required ConfiguredServer Server { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = Model;
        var server = Server;

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new ServerStatusDot { State = server.State },
                new Grow
                {
                    Child = new Column
                    {
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new Text
                            {
                                Value = $"{server.Entry.Language.Value} — {server.Entry.Command}",
                                Color = Theme.Color(t => t.DialogBody.BodyText),
                            },
                            new Text
                            {
                                Value = vm.Describe(server.State),
                                Wrap = TextWrap.Wrap,
                                FontSize = FontSize.Caption,
                                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                            },
                        ],
                    },
                },
                new ButtonWidget
                {
                    Style = ButtonStyle.Outline(static t => t.Palette.TextBody),
                    Visible = server.IsRunning,
                    Command = new Command(() => vm.Stop(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersStop }],
                }.WithController<KbmController>(),
                new ButtonWidget
                {
                    Style = ButtonStyle.Outline(static t => t.Palette.TextBody),
                    Visible = !server.IsRunning,
                    Command = new Command(() => vm.Restart(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersRetry }],
                }.WithController<KbmController>(),
            ],
        };
    }
}

internal sealed record LanguageServerSuggestionRow : Widget
{
    public required LanguageServersViewModel Model { get; init; }
    public required StarterServer Server { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = Model;
        var server = Server;

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Grow
                {
                    Child = new Text
                    {
                        Value = s.LanguageServersSuggestion(server.DisplayName, server.Command),
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
                new ButtonWidget
                {
                    Style = ButtonStyle.Plain,
                    Command = new Command(() => vm.CopyEntry(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersCopySnippet }],
                }.WithController<KbmController>(),
            ],
        };
    }
}
