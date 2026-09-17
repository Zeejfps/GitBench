using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

internal sealed record RepoCustomizeIconDialog : Widget<RepoIconCustomization>
{
    public const string BrowseId = "repo-icon-browse";
    internal const float DialogWidth = 460f;
    internal const float DialogHeight = 560f;
    public const string ImagePreviewId = "repo-icon-image-preview";
    public required Repo Repo { get; init; }
    public required Action OnClose { get; init; }

    protected override RepoIconCustomization CreateState(Context ctx) =>
        new(Repo, ctx.Theme().Styles.Value.RepoBarRow.Icon(Repo.Kind, false, false));

    protected override IWidget Build(Context ctx, RepoIconCustomization model)
    {
        var s = ctx.Localization().Strings.Value;
        var ring = new FocusRing();
        var input = ctx.Require<InputSystem>();
        IWidget Button(Widget<ButtonState> button)
        {
            var view = button.BuildView(ctx);
            ColorPicker.WireButton(view, button.State, ring, input, OnClose);
            return new Raw { View = view };
        }
        void Save()
        {
            if (model.Save(ctx.Require<IRepoRegistry>(), Repo.Id, s.ReposErrorCustomIconSave)) OnClose();
        }
        var browse = Button(new ButtonWidget
        {
            Id = BrowseId, Height = 28, ContentInset = new PaddingStyle { Left = 8, Right = 8 },
            Command = new Command(() => ctx.Require<IFilePicker>().PickFile(
                s.ReposPickerChooseCustomIcon,
                model.ImagePath.Value is { } path ? Path.GetDirectoryName(path) : Repo.Path,
                [new FileFilter(s.ReposPickerChooseCustomIcon, ["*.png", "*.jpg", "*.jpeg", "*.ico"])],
                picked => model.SelectImage(picked, s.ReposErrorCustomIconInvalid))),
            Children = [new Text
            {
                Value = Prop.Bind<string?>(() => model.IsFolder.Value ? s.ReposIconChooseImage : s.ReposIconReplaceImage),
                FontSize = 13, VAlign = TextAlignment.Center,
                Color = Theme.Color(t => t.Palette.TextSecondary),
            }],
        });
        var reset = Button(new ButtonWidget
        {
            Id = ColorPicker.ResetId, Height = 28, ContentInset = new PaddingStyle { Left = 8, Right = 8 },
            Command = new Command(model.ResetColor),
            Children = [new Text
            {
                Value = s.ColorPickerUseDefault, FontSize = 13, VAlign = TextAlignment.Center,
                Color = Theme.Color(t => t.Palette.TextSecondary),
            }],
        });
        var swatches = ColorPicker.Presets.Select((color, index) => Button(new ButtonWidget
        {
            Id = $"color-picker-swatch-{index}", Width = 24, Height = 24,
            Style = ButtonStyle.Filled(color),
            Accessibility = new(AccessibilityRole.Button, HsvColor.Hex(color)),
            ContentInset = PaddingStyle.All(0),
            Command = new Command(() => model.SelectColor(color)),
            Children = [new Text
            {
                Value = Prop.Bind<string?>(() => model.IsFolder.Value && model.Color.SelectedColor.Value == color ? LucideIcons.Check : null),
                FontFamily = LucideIcons.FontFamily, FontSize = 13, Width = 24,
                Color = HsvColor.Foreground(color), HAlign = TextAlignment.Center, VAlign = TextAlignment.Center,
            }],
        })).ToArray();
        return new Dialog
        {
            Title = s.ReposCustomizeIconTitle, Width = DialogWidth, Height = DialogHeight, OnClose = OnClose,
            ScrollBody = false,
            Action = (s.CommonSave, DialogButtonRole.Primary, Save), ActionEnabled = model.CanSave,
            ConfirmKeys = true,
            Body =
            [
                new Row
                {
                    Gap = 12, CrossAxis = CrossAxisAlignment.Center,
                    Children =
                    [
                        new Box
                        {
                            Id = ImagePreviewId, Width = 40, Height = 40,
                            Children = [new Switch<int>
                            {
                                Value = model.PreviewVersion,
                                Case = _ => model.ImagePath.Value is { } path
                                    ? new RepoIconImage { Path = path, Size = 40, LoadFrame = model.LoadPreview }
                                    : new Text
                                    {
                                        Value = LucideIcons.FolderGit2, FontFamily = LucideIcons.FontFamily,
                                        FontSize = 32, Width = 40, Height = 40,
                                        HAlign = TextAlignment.Center, VAlign = TextAlignment.Center,
                                        Color = Prop.Bind(() => model.Color.Color),
                                    },
                            }],
                        },
                        new Grow { Child = new Column
                        {
                            Gap = 2, CrossAxis = CrossAxisAlignment.Stretch,
                            Children =
                            [
                                new Text { Value = Repo.DisplayName, Color = Theme.Color(t => t.Palette.TextPrimary) },
                                new Row { Children = [browse] },
                            ],
                        } },
                    ],
                },
                new Column
                {
                    Gap = 8, CrossAxis = CrossAxisAlignment.Stretch,
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = [new Grow { Child = new Text { Value = s.ReposIconFolderColor, Color = Theme.Color(t => t.Palette.TextPrimary) } }, reset],
                        },
                        new Row
                        {
                            MainAxis = MainAxisAlignment.SpaceBetween,
                            Children = swatches,
                        },
                        new ColorPicker
                        {
                            Model = model.Color, ShowPresets = false, ShowReset = false,
                            OnSubmit = Save, OnCancel = OnClose,
                        },
                    ],
                },
                new Text
                {
                    Value = model.ImageError, Wrap = TextWrap.Wrap,
                    Visible = Prop.Bind(() => model.ImageError.Value is not null),
                    Color = Theme.Color(t => t.DialogFrame.ErrorText),
                },
            ],
        };
    }
}
