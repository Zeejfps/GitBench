using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Controls.Dialogs;

internal sealed record DialogFileList : Widget
{
    public required CheckedFileList List { get; init; }
    public required string EmptyText { get; init; }

    protected override View CreateView(Context ctx)
    {
        var column = new ColumnView { Gap = Spacing.None };
        var files = List.Files;
        if (files.Count == 0)
        {
            var theme = ctx.Theme();
            var empty = new TextView(ctx.Canvas)
            {
                Text = EmptyText,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            empty.BindTextColor(() => theme.Styles.Value.FileChangesSection.EmptyPlaceholderText);
            column.Children.Add(empty);
        }
        else
        {
            for (var i = 0; i < files.Count; i++)
            {
                var index = i;
                column.Children.Add(DialogFileRow.Build(
                    ctx, files[i], List.CheckedPaths, modifiers => List.ClickRow(index, modifiers)));
            }
        }

        return new DialogScrollList { Content = column }.BuildView(ctx);
    }
}
