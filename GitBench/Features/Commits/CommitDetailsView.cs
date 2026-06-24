using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Features.LocalChanges;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

internal sealed class CommitDetailsView : ContainerView
{
    private const int Padding = 14;
    private const float AvatarSize = 36f;

    private static uint AvatarColor(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return CategoricalPalette.Avatar(0);
        var h = 0;
        foreach (var ch in seed) h = unchecked(h * 31 + char.ToLowerInvariant(ch));
        return CategoricalPalette.Avatar(h);
    }

    private readonly ICanvas _canvas;
    private readonly IThemeService<ThemeStyles> _theme;
    private readonly ILocalizationService _loc;
    private readonly ColumnView _headerInfo;
    private readonly ScrollPane _headerScrollPane;
    private readonly FileChangesSection _changesSection;
    private readonly View _diffView;
    private readonly VerticalSplitContainer _splitContainer;
    private readonly VerticalSplitContainer _innerSplit;
    private readonly State<string?> _selectedPath = new(null);
    private readonly ListArrowKbmController _arrowController;
    private CommitDetailsViewModel? _vm;
    private CommitDetailsRenderState? _lastRenderState;

    public CommitDetailsView(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        _canvas = ctx.Canvas;
        _theme = ctx.Theme();
        _loc = ctx.Localization();
        var vm = ctx.Require<CommitDetailsViewModel>();

        _headerInfo = new ColumnView { Gap = Spacing.Md };

        _headerScrollPane = new ScrollPane();
        _headerScrollPane.Children.Add(_headerInfo);
        _headerScrollPane.UseController(input, () => new ScrollPaneWheelController(_headerScrollPane));

        var headerVScrollBar = ScrollBars.CreateVertical(ctx);
        var headerHScrollBar = ScrollBars.CreateHorizontal(ctx);
        var headerWithBars = new BorderLayoutView
        {
            Center = _headerScrollPane,
            East = headerVScrollBar,
            South = headerHScrollBar,
        };

        _changesSection = new FileChangesSection(
            ctx,
            "Changes",
            selectedPath: _selectedPath,
            onRowClicked: f =>
            {
                _vm?.SelectFile(f.Path);
                _arrowController.TakeFocus();
            });

        var innerSplitterHovered = new State<bool>(false);
        var innerSplitter = new RectView();
        innerSplitter.BindThemedBackgroundColor(_theme, s =>
            innerSplitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);
        _innerSplit = new VerticalSplitContainer(headerWithBars, _changesSection, innerSplitter, bottomFraction: 1f / 2f)
        {
            BottomVisible = false,
        };
        innerSplitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.Y,
            _innerSplit.AdjustBottomFractionByPixels,
            h => innerSplitterHovered.Value = h));

        _diffView = new Provide<DiffViewModel>
        {
            Value = vm.DiffVm,
            Child = new DiffView(),
        }.BuildView(ctx);

        var diffHeader = new Provide<DiffViewModel>
        {
            Value = vm.DiffVm,
            Child = new DiffPaneHeaderWidget().WithController<KbmController>(),
        }.BuildView(ctx);
        var diffPane = new BorderLayoutView
        {
            North = diffHeader,
            Center = _diffView,
        };
        this.Bind(vm.DiffVm.IsCollapsed, c => diffPane.Center = c ? null : _diffView);

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(_theme, s =>
            splitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);

        _splitContainer = new VerticalSplitContainer(_innerSplit, diffPane, splitter, bottomFraction: 1f / 2f);

        splitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.Y,
            _splitContainer.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        var panel = new RectView
        {
            BorderSize = new BorderSizeStyle { Left = 1 },
            Children = { _splitContainer },
        };
        panel.BindThemedBackgroundColor(_theme, s => s.CommitDetailsView.Background);
        panel.BindThemedBorderColor(_theme, s => new BorderColorStyle { Left = s.CommitDetailsView.BorderLeft });
        AddChildToSelf(panel);

        this.Use(() => new ScrollSyncController(_headerScrollPane, headerVScrollBar, headerHScrollBar));

        // Up/Down arrow navigation over the Changes list, mirroring the local-changes panels.
        // The list is a flat single-select list, so there are no folders to expand and no
        // stage/discard actions — only row movement is wired.
        _arrowController = new ListArrowKbmController(
            this,
            input,
            (delta, _) => _vm?.MoveSelection(delta),
            _ => { },
            () => { },
            () => { });
        _arrowController.OnToggleFullFile = () => _vm?.DiffVm.ToggleFullFile();
        this.UseController(input, _arrowController);

        this.UseViewModel(() => vm, Bind);

        // The header labels ("Commit:", "Parents:") are baked into TextViews when a commit loads,
        // so a live language switch needs an explicit re-render of the current state.
        this.Bind(_loc.Strings, _ =>
        {
            if (_lastRenderState != null) SetRenderState(_lastRenderState);
        });
    }

    private void Bind(CommitDetailsViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        _vm = vm;
        this.Bind(vm.RenderState, SetRenderState);
        this.Bind(vm.SelectedPath, path =>
        {
            _selectedPath.Value = path;
            if (path != null) _changesSection.EnsureRowVisible(path);
        });
        _splitContainer.BindBottomVisible(() => vm.SelectedTarget.Value != null);
        _splitContainer.BindBottomCollapsed(vm.DiffVm.IsCollapsed, DiffPaneHeaderWidget.HeaderHeight);
    }

    private void SetRenderState(CommitDetailsRenderState state)
    {
        _lastRenderState = state;
        switch (state)
        {
            case CommitDetailsRenderState.Placeholder p:
                ShowPlaceholder(p.Text);
                break;
            case CommitDetailsRenderState.Loaded l:
                ShowDetails(l.Details);
                break;
        }
    }

    private void ShowPlaceholder(string text)
    {
        _headerInfo.Children.Clear();
        var placeholder = new TextView(_canvas)
        {
            Text = text,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        placeholder.BindThemedTextColor(_theme, s => s.CommitDetailsView.PlaceholderText);
        _headerInfo.Children.Add(placeholder);
        _headerScrollPane.ScrollToOrigin();
        _changesSection.SetFiles(Array.Empty<FileChange>());
        _innerSplit.BottomVisible = false;
    }

    private void ShowDetails(CommitDetails d)
    {
        _headerInfo.Children.Clear();

        var topColumn = new ColumnView { Gap = Spacing.Md };
        topColumn.Children.Add(BuildAuthorHeader(d));

        if (!string.IsNullOrEmpty(d.MessageShort))
        {
            var subject = new TextView(_canvas) { Text = d.MessageShort };
            subject.BindThemedTextColor(_theme, s => s.CommitDetailsView.PrimaryText);
            topColumn.Children.Add(subject);
        }

        var body = ExtractBody(d.Message, d.MessageShort);
        if (!string.IsNullOrEmpty(body))
        {
            var bodyText = new TextView(_canvas) { Text = body };
            bodyText.BindThemedTextColor(_theme, s => s.CommitDetailsView.SecondaryText);
            topColumn.Children.Add(bodyText);
        }

        var strings = _loc.Strings.Value;
        var commitLine = new TextView(_canvas) { Text = $"{strings.CommitsDetailsCommitLabel}  {d.Sha}" };
        commitLine.BindThemedTextColor(_theme, s => s.CommitDetailsView.MutedText);
        topColumn.Children.Add(commitLine);

        var parentLine = new TextView(_canvas)
        {
            Text = d.ParentShas.Count == 0
                ? $"{strings.CommitsDetailsParentsLabel} {strings.CommitsDetailsParentsNone}"
                : $"{strings.CommitsDetailsParentsLabel} " + string.Join(", ", d.ParentShas.Select(ShortSha)),
        };
        parentLine.BindThemedTextColor(_theme, s => s.CommitDetailsView.MutedText);
        topColumn.Children.Add(parentLine);

        _headerInfo.Children.Add(new PaddingView
        {
            Padding = PaddingStyle.All(Padding),
            Children = { topColumn },
        });

        _changesSection.SetFiles(d.Files);
        _innerSplit.BottomVisible = true;
        _headerScrollPane.ScrollToOrigin();
    }

    private View BuildAuthorHeader(CommitDetails d)
    {
        var avatarSeed = !string.IsNullOrEmpty(d.AuthorEmail) ? d.AuthorEmail : d.AuthorName;
        var initials = new TextView(_canvas)
        {
            Text = Initials(d.AuthorName, d.AuthorEmail),
            FontSize = 16f,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        initials.BindThemedTextColor(_theme, s => s.Palette.TextOnAccent);

        var avatar = new RectView
        {
            Width = AvatarSize,
            Height = AvatarSize,
            BackgroundColor = AvatarColor(avatarSeed),
            BorderRadius = BorderRadiusStyle.All(AvatarSize * 0.5f),
            Children =
            {
                initials,
            },
        };

        var authorName = new TextView(_canvas) { Text = FormatAuthor(d.AuthorName, d.AuthorEmail) };
        authorName.BindThemedTextColor(_theme, s => s.CommitDetailsView.PrimaryText);

        var date = new TextView(_canvas) { Text = FormatFullDate(d.AuthorWhen) };
        date.BindThemedTextColor(_theme, s => s.CommitDetailsView.MutedText);

        var info = new ColumnView
        {
            Gap = Spacing.Hair,
            Children = { authorName, date },
        };

        return new FlexRowView
        {
            Gap = Spacing.Lg,
            CrossAxisAlignment = CrossAxisAlignment.Start,
            Children =
            {
                avatar,
                new FlexItem { Grow = 1, Child = info },
            },
        };
    }

    private static string Initials(string name, string email)
    {
        var source = !string.IsNullOrWhiteSpace(name) ? name : email;
        if (string.IsNullOrWhiteSpace(source)) return "?";
        var parts = source.Split(new[] { ' ', '.', '_', '-', '@' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return char.ToUpperInvariant(parts[0][0]).ToString();
        return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
    }

    private static string FormatAuthor(string name, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return name;
        if (string.IsNullOrWhiteSpace(name)) return email;
        return $"{name} <{email}>";
    }

    private static string FormatFullDate(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue) return string.Empty;
        return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz");
    }

    private static string ShortSha(string sha)
        => string.IsNullOrEmpty(sha) ? string.Empty : (sha.Length >= 7 ? sha[..7] : sha);

    /// <summary>
    /// libgit2's Message includes the subject line. Extract the body (everything after the
    /// blank line after the subject) so we don't show the subject twice.
    /// </summary>
    private static string ExtractBody(string fullMessage, string subject)
    {
        if (string.IsNullOrEmpty(fullMessage)) return string.Empty;
        var normalized = fullMessage.Replace("\r\n", "\n");
        if (!string.IsNullOrEmpty(subject) && normalized.StartsWith(subject))
        {
            var rest = normalized.AsSpan(subject.Length).TrimStart('\n');
            return rest.ToString().TrimEnd();
        }
        return normalized.TrimEnd();
    }
}

internal sealed class ScrollPaneWheelController : KeyboardMouseController
{
    private const float Step = 60f;
    private readonly ScrollPane _pane;

    public ScrollPaneWheelController(ScrollPane pane)
    {
        _pane = pane;
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.DeltaY != 0f) _pane.ScrollVertical(-e.DeltaY * Step);
        if (e.DeltaX != 0f) _pane.ScrollHorizontal(-e.DeltaX * Step);
        e.Consume();
    }
}
