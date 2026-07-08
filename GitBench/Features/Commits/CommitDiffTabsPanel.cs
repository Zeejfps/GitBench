using GitBench.Controls;
using GitBench.Features.Diff;
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

/// <summary>
/// The tabbed diff region of a commit-details surface, bound to a
/// <see cref="CommitDetailsViewModel"/>: the tab strip across the top, the always-present Details
/// tab (commit metadata), and one body per open file's diff. Switching tabs hides rather than
/// unmounts, so each diff keeps its scroll, highlight, and full-file toggle. Reused wherever that
/// surface appears — the History pane stacks it under the Changes list, the review window fills the
/// right column with it.
/// </summary>
internal sealed record CommitDiffTabsPanel : IWidget
{
    public View BuildView(Context ctx) => new CommitDiffTabsPanelView(ctx, ctx.Require<CommitDetailsViewModel>());
}

internal sealed class CommitDiffTabsPanelView : ContainerView
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
    private readonly CommitDetailsViewModel _vm;
    private readonly ColumnView _headerInfo;
    private readonly ScrollPane _headerScrollPane;
    private readonly FlexColumnView _bodyHost;
    private readonly FlexItem _detailsItem;
    private readonly Dictionary<string, FlexItem> _tabBodies = new();
    private CommitDetails? _lastDetails;

    public CommitDiffTabsPanelView(Context ctx, CommitDetailsViewModel vm)
    {
        _ctx = ctx;
        _vm = vm;
        var input = ctx.Require<InputSystem>();
        _canvas = ctx.Canvas;
        _theme = ctx.Theme();
        _loc = ctx.Localization();

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

        // The Details tab body = the commit metadata (author / message / sha / parents). The file
        // list is intentionally NOT in here — it is the host's own panel, so it stays visible when a
        // diff tab is open. The metadata and each open file's diff stack in _bodyHost; only the
        // active one is IsVisible, so switching tabs hides rather than unmounts — each diff keeps its
        // scroll, highlight, and full-file toggle.
        _detailsItem = new FlexItem { Grow = 1, Child = headerWithBars };
        _bodyHost = new FlexColumnView { CrossAxisAlignment = CrossAxisAlignment.Stretch };
        _bodyHost.Children.Add(_detailsItem);

        var tabStrip = new CommitDetailsTabStrip { Vm = vm }.BuildView(ctx);
        AddChildToSelf(new BorderLayoutView
        {
            North = tabStrip,
            Center = _bodyHost,
        });

        this.Use(() => new ScrollSyncController(_headerScrollPane, headerVScrollBar, headerHScrollBar));

        this.Bind(vm.SelectedPath, _ => ApplyActiveVisibility());
        this.Use(() => vm.OpenTabs.Subscribe(OnTabsChanged));
        // Loaded rebuilds the metadata; a placeholder clears it (the host owns the centered
        // placeholder text). Loading deliberately keeps the previous metadata up
        // (stale-while-revalidate), matching the details host's skeleton rules.
        this.Bind(vm.RenderState, state =>
        {
            switch (state)
            {
                case CommitDetailsRenderState.Loaded l:
                    ShowMetadata(l.Details);
                    break;
                case CommitDetailsRenderState.Placeholder:
                    _lastDetails = null;
                    _headerInfo.Children.Clear();
                    break;
            }
        });

        // The metadata labels ("Commit:", "Parents:") are baked into TextViews when a commit loads,
        // so a live language switch needs an explicit re-render of the current details.
        this.Bind(_loc.Strings, _ =>
        {
            if (_lastDetails != null) ShowMetadata(_lastDetails);
        });
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
        var active = _vm.SelectedPath.Value;
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

    private void ShowMetadata(CommitDetails d)
    {
        _lastDetails = d;
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
