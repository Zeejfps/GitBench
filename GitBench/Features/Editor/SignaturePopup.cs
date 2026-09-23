using GitBench.Controls;
using GitBench.Lsp;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Fonts;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Editor;

/// <summary>
/// Shows parameter info above the call it describes, clear of the completion list that opens below
/// the caret. Like that list it never takes the keyboard or the pointer, and it repaints in place
/// while what it shows still fits the window it has.
/// </summary>
internal sealed class SignaturePopup : IDisposable
{
    private const int Gap = 2;

    private readonly IPopupWindowFactory _factory;
    private readonly IWindowCoordinates _coordinates;

    private IPopupWindow? _popup;
    private SignatureListView? _view;
    private ScreenRect _anchor;

    public SignaturePopup(IPopupWindowFactory factory, IWindowCoordinates coordinates)
    {
        _factory = factory;
        _coordinates = coordinates;
    }

    /// <param name="at">The canvas rect of a caret standing where the popup's text should begin.</param>
    public void Show(SignatureHelp help, RectF at)
    {
        var anchor = _coordinates.ToScreenPoints(CanvasRect.From(
            at with { Left = at.Left - SignatureListView.TextInset }));
        if (_view is not null && anchor == _anchor && _view.TryShow(help)) return;

        var previous = _popup;
        _anchor = anchor;
        _popup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx =>
            {
                _view = new SignatureListView(ctx.Canvas, help, ctx.Theme().Styles.Value);
                return Direction.Wrap(new Raw { View = _view }).BuildView(ctx);
            },
            Place = size =>
            {
                var above = new ScreenRect(anchor.X, anchor.Y - Gap - size.Height, size.Width, size.Height);
                var below = new ScreenRect(anchor.X, anchor.Y + anchor.Height + Gap, size.Width, size.Height);
                return (above, below);
            },
            MousePassThrough = true,
        });
        if (previous is not null) _factory.Release(previous);
    }

    public void Hide()
    {
        if (_popup is null) return;
        _factory.Release(_popup);
        _popup = null;
        _view = null;
    }

    public void Dispose() => Hide();
}

/// <summary>Every overload of a call, one row each, the active one filled and its current parameter
/// drawn emphasized, with the active overload's documentation under them when the server sent any.</summary>
internal sealed class SignatureListView : View
{
    private const float RowHeight = 22f;
    private const float Pad = 6f;
    private const float MinWidth = 160f;
    private const float MaxWidth = 720f;
    private const int MaxRows = 8;

    /// <summary>How far right of the popup's edge the signatures' text starts.</summary>
    public const float TextInset = Pad + 4f;

    private readonly ICanvas _canvas;
    private readonly ThemeStyles _styles;
    private SignatureHelp _help;
    private readonly float _width;
    private readonly float _height;

    private readonly TextStyle _plain = new()
    {
        FontFamily = MonoFonts.Regular,
        FontSize = FontSize.Body,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };

    private readonly TextStyle _parameter = new()
    {
        FontFamily = MonoFonts.Bold,
        FontSize = FontSize.Body,
        VerticalAlignment = TextAlignment.Center,
        BaseDirection = BidiDirection.Ltr,
    };

    private readonly TextStyle _documentation = new()
    {
        FontSize = FontSize.Caption,
        VerticalAlignment = TextAlignment.Center,
    };

    public SignatureListView(ICanvas canvas, SignatureHelp help, ThemeStyles styles)
    {
        _canvas = canvas;
        _styles = styles;
        _help = help;
        _width = WidthFor(help);
        _height = HeightFor(help);
        Width = _width;
        Height = _height;
    }

    /// <summary>Draws another answer in this one's place, as long as it fits the window already sized
    /// for this one. Returns false when it would not.</summary>
    public bool TryShow(SignatureHelp help)
    {
        if (HeightFor(help) != _height || WidthFor(help) > _width) return false;
        _help = help;
        SetDirty();
        return true;
    }

    private static int Rows(SignatureHelp help) => Math.Min(help.Signatures.Count, MaxRows);

    private float HeightFor(SignatureHelp help) =>
        Rows(help) * RowHeight + (DocumentationOf(help) is null ? 0 : RowHeight) + Pad * 2;

    private float WidthFor(SignatureHelp help)
    {
        var widest = 0f;
        foreach (var signature in help.Signatures.Take(MaxRows))
            widest = MathF.Max(widest, _canvas.MeasureTextWidth(signature.Label, _parameter));
        if (DocumentationOf(help) is { } documentation)
            widest = MathF.Max(widest, _canvas.MeasureTextWidth(documentation, _documentation));
        return Math.Clamp(widest + TextInset * 2, MinWidth, MaxWidth);
    }

    private static string? DocumentationOf(SignatureHelp help)
    {
        var documentation = help.Signatures[help.ActiveSignature].Documentation;
        if (string.IsNullOrWhiteSpace(documentation)) return null;
        var firstLine = documentation.Trim().Split('\n')[0].Trim();
        return firstLine.Length == 0 ? null : firstLine;
    }

    protected override void OnDrawSelf(ICanvas c)
    {
        var pos = Position;
        var z = GetDrawZIndex();
        var menu = _styles.ContextMenu;

        c.DrawRect(new DrawRectInputs
        {
            Position = pos,
            Style = new RectStyle
            {
                BackgroundColor = menu.Background,
                BorderColor = BorderColorStyle.All(menu.Border),
                BorderSize = BorderSizeStyle.All(1),
                BorderRadius = BorderRadiusStyle.All(Radius.Md),
            },
            ZIndex = z,
        });

        var rows = Rows(_help);
        for (var i = 0; i < rows; i++)
        {
            var row = new RectF(pos.Left + Pad, pos.Top - Pad - (i + 1) * RowHeight, pos.Width - Pad * 2, RowHeight);
            var active = i == _help.ActiveSignature;
            if (active && _help.Signatures.Count > 1)
            {
                c.DrawRect(new DrawRectInputs
                {
                    Position = row,
                    Style = new RectStyle
                    {
                        BackgroundColor = menu.ItemSelectedBackground,
                        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                    },
                    ZIndex = z + 1,
                });
            }

            DrawSignature(c, row, _help.Signatures[i], active ? _help.ActiveParameterOf(i) : null, active, z + 2);
        }

        if (DocumentationOf(_help) is not { } documentation) return;

        _documentation.TextColor = menu.ItemTextDisabled;
        var line = new RectF(pos.Left + TextInset, pos.Top - Pad - (rows + 1) * RowHeight, pos.Width - TextInset * 2, RowHeight);
        c.DrawText(new DrawTextInputs
        {
            Position = line,
            Text = TextEllipsis.Truncate(c, documentation, _documentation, line.Width),
            Style = _documentation,
            ZIndex = z + 2,
        });
    }

    private void DrawSignature(ICanvas c, RectF row, SignatureInfo signature, int? activeParameter, bool active, int z)
    {
        var menu = _styles.ContextMenu;
        _plain.TextColor = active ? menu.ItemText : menu.ItemTextDisabled;
        _parameter.TextColor = menu.AccentText;

        var label = signature.Label;
        var span = activeParameter is { } index && index < signature.Parameters.Count
            ? signature.Parameters[index]
            : new ParameterSpan(0, 0);

        var x = row.Left + TextInset - Pad;
        var right = row.Right - Pad;
        foreach (var (text, emphasized) in Pieces(label, span))
        {
            if (text.Length == 0 || x >= right) continue;
            var style = emphasized ? _parameter : _plain;
            var width = c.MeasureTextWidth(text, style);
            c.DrawText(new DrawTextInputs
            {
                Position = new RectF(x, row.Bottom, MathF.Min(width, right - x), row.Height),
                Text = text,
                Style = style,
                ZIndex = z,
            });
            x += width;
        }
    }

    private static IEnumerable<(string Text, bool Emphasized)> Pieces(string label, ParameterSpan span)
    {
        if (span.Length == 0)
        {
            yield return (label, false);
            yield break;
        }

        yield return (label[..span.Start], false);
        yield return (label.Substring(span.Start, span.Length), true);
        yield return (label[(span.Start + span.Length)..], false);
    }
}
