using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

internal sealed class CommitDetailsView : MultiChildView, IBind<CommitDetailsViewModel>
{
    private const int Padding = 14;
    private const float AvatarSize = 36f;

    private static readonly uint[] AvatarPalette =
    {
        0xFF5865F2,
        0xFFEB459E,
        0xFF57F287,
        0xFFFEE75C,
        0xFFED4245,
        0xFF9B59B6,
        0xFFE67E22,
        0xFF1ABC9C,
    };

    private static uint AvatarColor(string seed)
    {
        if (string.IsNullOrEmpty(seed)) return AvatarPalette[0];
        var h = 0;
        foreach (var ch in seed) h = unchecked(h * 31 + char.ToLowerInvariant(ch));
        var idx = ((h % AvatarPalette.Length) + AvatarPalette.Length) % AvatarPalette.Length;
        return AvatarPalette[idx];
    }

    private readonly ColumnView _headerInfo;
    private readonly ScrollPane _headerScrollPane;
    private readonly FileChangesSection _changesSection;
    private readonly DiffView _diffView;
    private readonly VerticalSplitContainer _splitContainer;
    private readonly VerticalSplitContainer _innerSplit;
    private readonly State<string?> _selectedPath = new(null);
    private CommitDetailsViewModel? _vm;

    public CommitDetailsView()
    {
        _headerInfo = new ColumnView { Gap = 8 };

        _headerScrollPane = new ScrollPane();
        _headerScrollPane.Children.Add(_headerInfo);
        _headerScrollPane.UseController(_ => new ScrollPaneWheelController(_headerScrollPane));

        var headerVScrollBar = ScrollBars.CreateVertical();
        var headerHScrollBar = ScrollBars.CreateHorizontal();
        var headerWithBars = new BorderLayoutView
        {
            Center = _headerScrollPane,
            East = headerVScrollBar,
            South = headerHScrollBar,
        };

        _changesSection = new FileChangesSection(
            "Changes",
            selectedPath: _selectedPath,
            onRowClicked: f => _vm?.SelectFile(f.Path));

        var innerSplitterHovered = new State<bool>(false);
        var innerSplitter = new RectView();
        innerSplitter.BindThemedBackgroundColor(s =>
            innerSplitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);
        _innerSplit = new VerticalSplitContainer(headerWithBars, _changesSection, innerSplitter, bottomFraction: 1f / 2f)
        {
            BottomVisible = false,
        };
        innerSplitter.UseController(ctx => new SplitterController(
            ctx,
            DragAxis.Y,
            _innerSplit.AdjustBottomFractionByPixels,
            h => innerSplitterHovered.Value = h));

        _diffView = new DiffView();

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(s =>
            splitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);

        _splitContainer = new VerticalSplitContainer(_innerSplit, _diffView, splitter, bottomFraction: 1f / 2f);

        splitter.UseController(ctx => new SplitterController(
            ctx,
            DragAxis.Y,
            _splitContainer.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        var panel = new RectView
        {
            BorderSize = new BorderSizeStyle { Left = 1 },
            Children = { _splitContainer },
        };
        panel.BindThemedBackgroundColor(s => s.CommitDetailsView.Background);
        panel.BindThemedBorderColor(s => new BorderColorStyle { Left = s.CommitDetailsView.BorderLeft });
        AddChildToSelf(panel);

        this.UseBehavior(_ => new ScrollSyncController(_headerScrollPane, headerVScrollBar, headerHScrollBar));

        this.UseViewModel(this);
    }

    public void Bind(CommitDetailsViewModel vm)
    {
        _vm = vm;
        vm.RenderState.Subscribe(SetRenderState);
        _diffView.Bind(vm.DiffVm);
        _selectedPath.BindTo(vm.SelectedPath);
        _splitContainer.BindBottomVisible(() => vm.SelectedTarget.Value != null);
        _splitContainer.BindBottomCollapsed(_diffView.IsCollapsed, DiffView.HeaderHeight);
    }

    private void SetRenderState(CommitDetailsRenderState state)
    {
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
        var placeholder = new TextView
        {
            Text = text,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        placeholder.BindThemedTextColor(s => s.CommitDetailsView.PlaceholderText);
        _headerInfo.Children.Add(placeholder);
        _headerScrollPane.ScrollToOrigin();
        _changesSection.SetFiles(Array.Empty<FileChange>());
        _innerSplit.BottomVisible = false;
    }

    private void ShowDetails(CommitDetails d)
    {
        _headerInfo.Children.Clear();

        var topColumn = new ColumnView { Gap = 8 };
        topColumn.Children.Add(BuildAuthorHeader(d));

        if (!string.IsNullOrEmpty(d.MessageShort))
        {
            var subject = new TextView { Text = d.MessageShort };
            subject.BindThemedTextColor(s => s.CommitDetailsView.PrimaryText);
            topColumn.Children.Add(subject);
        }

        var body = ExtractBody(d.Message, d.MessageShort);
        if (!string.IsNullOrEmpty(body))
        {
            var bodyText = new TextView { Text = body };
            bodyText.BindThemedTextColor(s => s.CommitDetailsView.SecondaryText);
            topColumn.Children.Add(bodyText);
        }

        var commitLine = new TextView { Text = $"Commit:  {d.Sha}" };
        commitLine.BindThemedTextColor(s => s.CommitDetailsView.MutedText);
        topColumn.Children.Add(commitLine);

        var parentLine = new TextView
        {
            Text = d.ParentShas.Count == 0
                ? "Parents: (none)"
                : "Parents: " + string.Join(", ", d.ParentShas.Select(ShortSha)),
        };
        parentLine.BindThemedTextColor(s => s.CommitDetailsView.MutedText);
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

    private static View BuildAuthorHeader(CommitDetails d)
    {
        var avatarSeed = !string.IsNullOrEmpty(d.AuthorEmail) ? d.AuthorEmail : d.AuthorName;
        var avatar = new RectView
        {
            Width = AvatarSize,
            Height = AvatarSize,
            BackgroundColor = AvatarColor(avatarSeed),
            BorderRadius = BorderRadiusStyle.All(AvatarSize * 0.5f),
            Children =
            {
                new TextView
                {
                    Text = Initials(d.AuthorName, d.AuthorEmail),
                    TextColor = 0xFFFFFFFF,
                    FontSize = 16f,
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center,
                },
            },
        };

        var authorName = new TextView { Text = FormatAuthor(d.AuthorName, d.AuthorEmail) };
        authorName.BindThemedTextColor(s => s.CommitDetailsView.PrimaryText);

        var date = new TextView { Text = FormatFullDate(d.AuthorWhen) };
        date.BindThemedTextColor(s => s.CommitDetailsView.MutedText);

        var info = new ColumnView
        {
            Gap = 2,
            Children = { authorName, date },
        };

        return new FlexRowView
        {
            Gap = 12f,
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
