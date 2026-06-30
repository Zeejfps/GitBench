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

    private readonly Context _ctx;
    private readonly ICanvas _canvas;
    private readonly IThemeService<ThemeStyles> _theme;
    private readonly ILocalizationService _loc;
    private readonly ColumnView _headerInfo;
    private readonly ScrollPane _headerScrollPane;
    private readonly FileChangesSection _changesSection;
    private readonly RectView _panel;
    // The "Content" surface: a draggable split of the file list (top, always visible) over the tabbed
    // metadata/diff region (bottom). Opening a file swaps the bottom region to its diff; the list stays.
    private readonly VerticalSplitContainer _contentView;
    private readonly FlexColumnView _bodyHost;
    private readonly FlexItem _detailsItem;
    private readonly Dictionary<string, FlexItem> _tabBodies = new();
    private readonly FlexColumnView _placeholderHost;
    private readonly TextView _placeholder;
    // Details fade up as a commit's data arrives; the placeholder text blooms in. Both park when
    // settled, and neither replays on a commit-to-commit change of already-shown details.
    private readonly Tween _enterTween;
    private readonly Tween _placeholderTween;
    private enum Shown { None, Content, Skeleton, Placeholder }
    private Shown _shown = Shown.None;
    private readonly State<string?> _selectedPath = new(null);
    private readonly ListArrowKbmController _arrowController;
    private CommitDetailsViewModel? _vm;
    private CommitDetailsRenderState? _lastRenderState;

    public CommitDetailsView(Context ctx)
    {
        _ctx = ctx;
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

        var viewModeIcon = Prop.Bind<string?>(() =>
            vm.ViewMode.Value == FileViewMode.Tree ? LucideIcons.ListTree : LucideIcons.List);
        var viewModeButton = new LocalChangesHeaderActionButton
        {
            Icon = viewModeIcon,
            Command = vm.ToggleViewMode,
            Tooltip = L.T(s => s.LocalchangesToggleViewTooltip),
        }.BuildView(ctx);

        _changesSection = new FileChangesSection(
            ctx,
            "Changes",
            selectedPath: _selectedPath,
            onRowClicked: f =>
            {
                _vm?.SelectFile(f.Path);
                _arrowController.TakeFocus();
            },
            headerActions: [viewModeButton]);

        // The Details tab body = the commit metadata (author / message / sha / parents). The file
        // list is intentionally NOT in here — it is the split's own top pane, so it stays visible
        // when a diff tab is open. The metadata and each open file's diff stack in _bodyHost; only the
        // active one is IsVisible, so switching tabs hides rather than unmounts — each diff keeps its
        // scroll, highlight, and full-file toggle.
        _detailsItem = new FlexItem { Grow = 1, Child = headerWithBars };
        _bodyHost = new FlexColumnView { CrossAxisAlignment = CrossAxisAlignment.Stretch };
        _bodyHost.Children.Add(_detailsItem);

        var tabStrip = new CommitDetailsTabStrip { Vm = vm }.BuildView(ctx);
        var tabbedRegion = new BorderLayoutView
        {
            North = tabStrip,
            Center = _bodyHost,
        };

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(_theme, s =>
            splitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);
        // Top: the file list, always visible. Bottom: the tabbed metadata/diff region (the larger
        // share), shown once a commit is loaded (BottomVisible toggled in ShowDetails/ShowPlaceholder).
        _contentView = new VerticalSplitContainer(_changesSection, tabbedRegion, splitter, bottomFraction: 2f / 3f)
        {
            BottomVisible = false,
        };
        splitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.Y,
            _contentView.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        _placeholder = new TextView(_canvas)
        {
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _placeholder.BindThemedTextColor(_theme, s => s.CommitDetailsView.PlaceholderText);
        _placeholderHost = new FlexColumnView
        {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _placeholder },
        };

        _panel = new RectView
        {
            BorderSize = new BorderSizeStyle { Left = 1 },
            Children = { _contentView },
        };
        _panel.BindThemedBackgroundColor(_theme, s => s.CommitDetailsView.Background);
        _panel.BindThemedBorderColor(_theme, s => new BorderColorStyle { Left = s.CommitDetailsView.BorderLeft });
        AddChildToSelf(_panel);

        var ticker = ctx.Require<IFrameTicker>();
        _enterTween = new Tween(ticker, Transitions.ContentEnterSeconds, Easings.EaseOutCubic);
        _placeholderTween = new Tween(ticker, Transitions.PlaceholderBloomSeconds, Easings.EaseInCubic);
        // Bound before the view model (which drives the first SetRenderState) so the opacities start at
        // zero before the first show, rather than flashing fully opaque for a frame.
        this.Bind(_enterTween.LinearProgress, p => _contentView.Opacity = p);
        this.Bind(_placeholderTween.Progress, p => _placeholderHost.Opacity = p);
        this.Use(() => _enterTween);
        this.Use(() => _placeholderTween);

        this.Use(() => new ScrollSyncController(_headerScrollPane, headerVScrollBar, headerHScrollBar));

        // Up/Down arrow navigation over the Changes list, mirroring the local-changes panels.
        // Single-select with no stage/discard actions; arrows step through the visible file
        // rows only (folder rows are toggled by mouse, skipped by the keyboard).
        _arrowController = new ListArrowKbmController(
            this,
            input,
            (delta, _) =>
            {
                var next = _changesSection.NextFilePath(_selectedPath.Value, delta);
                if (next != null) _vm?.SelectFile(next);
            },
            _ => { },
            () => { },
            () => { });
        _arrowController.OnToggleFullFile = () => _vm?.ActiveDiff?.ToggleFullFile();
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
        this.Bind(vm.ViewMode, _changesSection.SetViewMode);
        this.Bind(vm.SelectedPath, path =>
        {
            _selectedPath.Value = path;
            if (path != null) _changesSection.EnsureRowVisible(path);
            ApplyActiveVisibility();
        });
        this.Use(() => vm.OpenTabs.Subscribe(OnTabsChanged));
    }

    // Mirrors the VM's OpenTabs into _bodyHost: a diff body per open file, plus the always-present
    // Details body. Removed/Cleared/Reset evict the body view (its DiffViewModel is disposed by the
    // VM); visibility is applied after every structural change.
    private void OnTabsChanged(ListChange<CommitFileTab> change)
    {
        switch (change.Kind)
        {
            case ListChangeKind.Added:
                AddTabBody(change.Item!);
                break;
            case ListChangeKind.Removed:
                RemoveTabBody(change.OldItem!.Path);
                break;
            case ListChangeKind.Reset:
            case ListChangeKind.Cleared:
                ClearTabBodies();
                if (_vm != null)
                    foreach (var tab in _vm.OpenTabs)
                        AddTabBody(tab);
                break;
        }
        ApplyActiveVisibility();
    }

    private void AddTabBody(CommitFileTab tab)
    {
        if (_tabBodies.ContainsKey(tab.Path)) return;
        var item = new FlexItem { Grow = 1, Child = BuildDiffBody(tab) };
        item.IsVisible = false;
        _tabBodies[tab.Path] = item;
        _bodyHost.Children.Add(item);
    }

    private void RemoveTabBody(string path)
    {
        if (!_tabBodies.Remove(path, out var item)) return;
        _bodyHost.Children.Remove(item);
    }

    private void ClearTabBodies()
    {
        foreach (var item in _tabBodies.Values)
            _bodyHost.Children.Remove(item);
        _tabBodies.Clear();
    }

    // Shows the body for the active tab and hides the rest (Details body when SelectedPath is null).
    // IsVisible is display:none — the hidden bodies stay mounted, so their diffs keep their state.
    private void ApplyActiveVisibility()
    {
        var active = _vm?.SelectedPath.Value;
        _detailsItem.IsVisible = active == null;
        foreach (var (path, item) in _tabBodies)
            item.IsVisible = path == active;
    }

    private View BuildDiffBody(CommitFileTab tab)
    {
        var diffView = new Provide<DiffViewModel>
        {
            Value = tab.Diff,
            Child = new DiffView(),
        }.BuildView(_ctx);
        var header = new Provide<DiffViewModel>
        {
            Value = tab.Diff,
            Child = new DiffPaneHeaderWidget { Collapsible = false },
        }.BuildView(_ctx);
        return new BorderLayoutView
        {
            North = header,
            Center = diffView,
        };
    }

    private void SetRenderState(CommitDetailsRenderState state)
    {
        _lastRenderState = state;
        switch (state)
        {
            case CommitDetailsRenderState.Loading:
                ShowSkeleton();
                break;
            case CommitDetailsRenderState.Placeholder p:
                ShowPlaceholder(p.Text);
                break;
            case CommitDetailsRenderState.Loaded l:
                ShowDetails(l.Details);
                break;
        }
    }

    private void ShowSkeleton()
    {
        // Stale-while-revalidate: keep the current commit's details visible while the next loads — the
        // skeleton is only for a cold load, where there's nothing to preserve.
        if (_shown is Shown.Content or Shown.Skeleton) return;

        // Rebuilt fresh each time (not cached) so its pulse starts clean: the pulse is disposed when the
        // skeleton is swapped out, and a reused view's disposed pulse would never breathe again. FadeIn
        // blooms it in (ease-in) so a fast cold load doesn't flash it.
        var skeleton = new FadeIn { Child = new CommitDetailsSkeleton(), Bloom = true }.BuildView(_ctx);
        _panel.Children.Clear();
        _panel.Children.Add(skeleton);
        _shown = Shown.Skeleton;
    }

    private void ShowPlaceholder(string text)
    {
        // Centered in the whole details panel, so it swaps in over the split container rather than
        // sitting at the top of the (content-height) header scroll column.
        _placeholder.Text = text;
        if (!_panel.Children.Contains(_placeholderHost))
        {
            _panel.Children.Clear();
            _panel.Children.Add(_placeholderHost);
        }
        // Bloom only when the placeholder first appears, not on a re-render (e.g. a locale switch)
        // while it's already up.
        if (_shown != Shown.Placeholder) _placeholderTween.Restart();
        _shown = Shown.Placeholder;
        _headerInfo.Children.Clear();
        _changesSection.SetFiles(Array.Empty<FileChange>());
        _changesSection.SetReviewSha(null);
        _contentView.BottomVisible = false;
    }

    private void ShowDetails(CommitDetails d)
    {
        if (!_panel.Children.Contains(_contentView))
        {
            _panel.Children.Clear();
            _panel.Children.Add(_contentView);
        }
        // Fade up only when details emerge from a placeholder/skeleton; a commit-to-commit change of
        // already-shown details swaps instantly, matching how the other panels skip the fade on refresh.
        if (_shown != Shown.Content) _enterTween.Restart();
        _shown = Shown.Content;
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
        _changesSection.SetReviewSha(d.Sha);
        _contentView.BottomVisible = true;
        _headerScrollPane.ScrollToOrigin();
    }

    private View BuildAuthorHeader(CommitDetails d)
    {
        var avatarSeed = !string.IsNullOrEmpty(d.AuthorEmail) ? d.AuthorEmail : d.AuthorName;
        var initials = new TextView(_canvas)
        {
            Text = Initials(d.AuthorName, d.AuthorEmail),
            FontSize = FontSize.Heading,
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
    private readonly ScrollPane _pane;

    public ScrollPaneWheelController(ScrollPane pane)
    {
        _pane = pane;
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e)
    {
        if (e.DeltaY != 0f) _pane.ScrollVertical(-e.DeltaY * Scrolling.WheelStep);
        if (e.DeltaX != 0f) _pane.ScrollHorizontal(-e.DeltaX * Scrolling.WheelStep);
        e.Consume();
    }
}
