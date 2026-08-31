using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Diff;

/// <summary>
/// The declarations a diff touched, on one line of the pane header: what it added, what it removed,
/// what it changed. Answers "what did this commit do" without reading a single hunk.
/// </summary>
/// <remarks>
/// <para>
/// Colour carries the verb, borrowed from the diff's own add/remove palette, so nothing needs a
/// legend or a translation.
/// </para>
/// <para>
/// Header chrome and nothing else. It arrives on the annotation lane, a beat after the rows, and
/// anything near the row stream would change the row count under a reader already scrolling.
/// </para>
/// <para>
/// Fixed slots rather than a data-driven list: the cap is a constant, the framework has no
/// horizontal counterpart to <c>Column&lt;T&gt;</c>, and adding one is a submodule commit for four
/// labels.
/// </para>
/// </remarks>
internal sealed record DiffChangeSummary : Widget
{
    /// <summary>How many declarations are named before the rest become a count. Four fits the
    /// narrowest pane this header appears in; past that the line is a wall, not a summary.</summary>
    private const int MaxNamed = 4;

    public required DiffViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = Vm;
        var children = new List<IWidget>();
        for (var i = 0; i < MaxNamed; i++) children.Add(Slot(vm, i));
        children.Add(new Text
        {
            Value = Prop.Bind<string?>(() => Overflow(ctx, vm)),
            Visible = Prop.Bind(() => Overflow(ctx, vm) != null),
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Center,
            Color = Theme.Color(s => s.DiffView.SummaryMutedText),
        });

        return new Clipped
        {
            Child = new Row
            {
                Gap = Spacing.Sm,
                CrossAxis = CrossAxisAlignment.Center,
                Children = [.. children],
            },
        };
    }

    // The first name never yields and the rest yield harder the further down the list they are, so
    // a narrow pane keeps one declaration readable instead of ellipsizing four into nothing. Without
    // any of this the row measures to its natural width and runs under the header's buttons.
    private static IWidget Slot(DiffViewModel vm, int index) => new Shrink
    {
        Factor = index == 0 ? 0f : index * 4f,
        Child = new Text
        {
            Value = Prop.Bind<string?>(() => At(vm, index)?.Name),
            Visible = Prop.Bind(() => At(vm, index) != null),
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Theme.Color(s => At(vm, index)?.Change switch
            {
                SymbolChangeKind.Added => s.DiffView.SummaryAddedText,
                SymbolChangeKind.Removed => s.DiffView.SummaryRemovedText,
                _ => s.DiffView.SummaryModifiedText,
            }),
        },
    };

    private static SymbolChange? At(DiffViewModel vm, int index)
    {
        var changed = Changed(vm);
        return index < changed.Count ? changed[index] : null;
    }

    private static string? Overflow(Context ctx, DiffViewModel vm)
    {
        var extra = Changed(vm).Count - MaxNamed;
        return extra > 0 ? ctx.Localization().Strings.Value.DiffChangeSummaryMore(extra) : null;
    }

    /// <summary>The summary flattened to the declarations that actually changed. The tree keeps
    /// unchanged ancestors so a reader can see what contains what; on one line the path already
    /// says that, so they would only be noise.</summary>
    private static IReadOnlyList<SymbolChange> Changed(DiffViewModel vm)
    {
        var flat = new List<SymbolChange>();
        Append(vm.ChangeSummary.Value, flat);
        return flat;
    }

    private static void Append(IReadOnlyList<SymbolChange> changes, List<SymbolChange> flat)
    {
        foreach (var change in changes)
        {
            if (change.Change != SymbolChangeKind.Unchanged) flat.Add(change);
            Append(change.Children, flat);
        }
    }
}
