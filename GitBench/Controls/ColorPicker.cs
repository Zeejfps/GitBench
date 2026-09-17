using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>A reusable opaque-color editor. Its owner owns the draft model and save/cancel policy.</summary>
internal sealed record ColorPicker : Widget
{
    public const string AreaId = "color-picker-area";
    public const string HueId = "color-picker-hue";
    public const string HexId = "color-picker-hex";
    public const string ResetId = "color-picker-reset";
    public static readonly IReadOnlyList<uint> Presets = Array.AsReadOnly<uint>(
        [0xFFE66B70, 0xFFE9954E, 0xFFD5AD42, 0xFF70B572, 0xFF48B4A0, 0xFF48A8C3,
         0xFF6195E8, 0xFF8584E8, 0xFFAC7CDE, 0xFFD97BB5, 0xFFAB8875, 0xFF969BA7]);

    public required ColorPickerModel Model { get; init; }
    public Action? OnSubmit { get; init; }
    public Action? OnCancel { get; init; }
    public bool ShowPresets { get; init; } = true;
    public bool ShowReset { get; init; } = true;

    protected override View CreateView(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var input = ctx.Require<InputSystem>();
        var ring = new FocusRing();

        View Swatch(int index)
        {
            var color = Presets[index];
            var button = new ButtonWidget
            {
                Id = $"color-picker-swatch-{index}",
                Width = 32, Height = 32,
                Accessibility = new(AccessibilityRole.Button, HsvColor.Hex(color)),
                Style = ButtonStyle.Filled(color),
                ContentInset = PaddingStyle.All(0),
                Command = new Command(() => Model.Select(color)),
                Children = [new Text
                {
                    Value = Prop.Bind<string?>(() => Model.Color == color ? LucideIcons.Check : string.Empty),
                    FontFamily = LucideIcons.FontFamily, FontSize = 16,
                    Color = HsvColor.Foreground(color),
                    HAlign = TextAlignment.Center, VAlign = TextAlignment.Center, Width = 32,
                }],
            };
            var view = button.BuildView(ctx);
            WireButton(view, button.State, ring, input, OnCancel);
            view.Bind(Model.SelectedColor, _ => view.Accessibility = view.Accessibility with
            { States = Model.Color == color ? AccessibilityStates.Selected : AccessibilityStates.None });
            return view;
        }

        var swatches = ShowPresets ? Enumerable.Range(0, Presets.Count).Select(Swatch).ToArray() : [];
        var area = new ColorPickerSurface(Model, false)
        { Id = AreaId, Height = 148, Accessibility = new(new("slider"), s.ColorPickerSaturationBrightness) };
        var hue = new ColorPickerSurface(Model, true)
        { Id = HueId, Height = 32, Accessibility = new(new("slider"), s.ColorPickerHue) };
        var areaController = WireSurface(area, ring, input);
        WireSurface(hue, ring, input);

        var hex = DialogFrame.TextInput(ctx);
        hex.Id = HexId;
        hex.Accessibility = new(AccessibilityRole.TextBox, s.ColorPickerHex);
        hex.BindTwoWay(Model.Hex);
        var hexController = new DialogTextInputKbmController(hex, input, ctx.Require<IClipboard>(),
            () => { if (Model.IsValid.Value) OnSubmit?.Invoke(); }, () => OnCancel?.Invoke());
        var hexStop = ring.Add(hexController.BeginEditing, hexController.EndEditing);
        hexController.OnTab = () => ring.Next(hexStop);
        hexController.OnShiftTab = () => ring.Previous(hexStop);
        hex.UseController(input, hexController);

        IWidget Reset()
        {
            var reset = new SecondaryDialogButton
            {
                Id = ResetId, Label = s.ColorPickerUseDefault,
                Height = 30, Command = new Command(Model.Reset),
            };
            var resetView = reset.BuildView(ctx);
            WireButton(resetView, reset.State, ring, input, OnCancel);
            return new Raw { View = resetView };
        }

        var view = new Column
        {
            Gap = 8, CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                .. ShowPresets ? new IWidget[] { new Text { Value = s.ColorPickerPresets, Color = Theme.Color(t => t.Palette.TextPrimary) } } : [],
                .. Enumerable.Range(0, ShowPresets ? 2 : 0).Select(row => (IWidget)new Row
                {
                    Gap = 8, MainAxis = MainAxisAlignment.SpaceBetween,
                    Children = swatches.Skip(row * 6).Take(6).Select(v => (IWidget)new Raw { View = v }).ToArray(),
                }),
                .. ShowPresets ? new IWidget[] { new Text { Value = s.ColorPickerCustom, Color = Theme.Color(t => t.Palette.TextPrimary) } } : [],
                new Raw { View = area },
                new Row
                {
                    Gap = 8, CrossAxis = CrossAxisAlignment.Center,
                    Children = [new Text { Value = s.ColorPickerHue, Color = Theme.Color(t => t.Palette.TextPrimary) }, new Grow { Child = new Raw { View = hue } }],
                },
                new Row
                {
                    Gap = 8, CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new Text { Value = s.ColorPickerHex, Color = Theme.Color(t => t.Palette.TextPrimary) },
                        new Grow { Child = new Box
                        {
                            Height = 32,
                            BorderSize = BorderSizeStyle.All(1), BorderRadius = BorderRadiusStyle.All(Radius.Sm),
                            BorderColor = Theme.BorderColor(t => BorderColorStyle.All(Model.IsValid.Value ? t.TextInput.Border : t.DialogFrame.ErrorText)),
                            Background = Theme.Color(t => t.TextInput.Background),
                            Children = [new Padding { Amount = PaddingStyle.All(6), Children = [new Raw { View = hex }] }],
                        } },
                    ],
                },
                new Text
                {
                    Value = s.ColorPickerInvalidHex, Wrap = TextWrap.Wrap,
                    Visible = Prop.Bind(() => !Model.IsValid.Value),
                    Color = Theme.Color(t => t.DialogFrame.ErrorText),
                },
                .. ShowReset ? new[] { Reset() } : [],
            ],
        }.BuildView(ctx);
        view.Behaviors.Add(new SurfaceLifetime(Model, area, hue, input, areaController));
        return view;
    }

    internal static void WireButton(View view, IInteractable target, FocusRing ring, InputSystem input, Action? onCancel)
    {
        var controller = new PickerButtonController(view, target, input, onCancel);
        var stop = ring.Add(() => input.StealFocus(controller), () => input.Blur(controller), () => target.Enabled.Value);
        controller.OnTab = () => ring.Next(stop);
        controller.OnShiftTab = () => ring.Previous(stop);
        view.UseController(input, controller);
    }

    // A tab stop keeps keyboard focus after a press. Focused dispatch runs before hit testing,
    // so an outside click must release that focus before the new target handles it.
    private sealed class PickerButtonController(View view, IInteractable target, InputSystem input, Action? cancel)
        : KeyboardMouseController
    {
        private readonly KbmController _button = new(target);
        private bool _focused;
        public Action? OnTab { set => _button.OnTab = value; }
        public Action? OnShiftTab { set => _button.OnShiftTab = value; }
        public override void OnFocusGained() { _focused = true; _button.OnFocusGained(); }
        public override void OnFocusLost() { _focused = false; _button.OnFocusLost(); }
        public override void OnMouseEnter(ref MouseEnterEvent e) => _button.OnMouseEnter(ref e);
        public override void OnMouseExit(ref MouseExitEvent e) => _button.OnMouseExit(ref e);
        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase != EventPhase.Bubbling || e.Button != MouseButton.Left) return;
            if (e.State == InputState.Pressed)
            {
                if (!view.Position.ContainsPoint(e.Mouse.Point)) { input.Blur(this); return; }
                if (target.Enabled.Value) input.StealFocus(this);
            }
            _button.OnMouseButtonStateChanged(ref e);
        }
        public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
        {
            if (_focused && e.State == InputState.Pressed && e.Key == KeyboardKey.Escape && cancel != null)
            {
                e.Consume();
                cancel();
                return;
            }
            _button.OnKeyboardKeyStateChanged(ref e);
        }
    }

    private ColorPickerSurfaceController WireSurface(ColorPickerSurface view, FocusRing ring, InputSystem input)
    {
        var controller = new ColorPickerSurfaceController(view, input)
        {
            Submit = () => { if (Model.IsValid.Value) OnSubmit?.Invoke(); },
            Cancel = OnCancel,
        };
        var stop = ring.Add(() => input.StealFocus(controller), () => input.Blur(controller));
        controller.Next = () => ring.Next(stop);
        controller.Previous = () => ring.Previous(stop);
        view.UseController(input, controller);
        return controller;
    }

    private sealed class SurfaceLifetime(ColorPickerModel model, ColorPickerSurface area, ColorPickerSurface hue,
        InputSystem input, ColorPickerSurfaceController initial) : IViewBehavior
    {
        private IDisposable? _subscription;
        public void Attach(View view)
        {
            _subscription = model.Hsv.Subscribe(_ => { area.Refresh(); hue.Refresh(); });
            input.StealFocus(initial);
        }
        public void Detach(View view)
        {
            _subscription?.Dispose();
            input.Blur(initial);
            area.Release(); hue.Release();
        }
    }
}
