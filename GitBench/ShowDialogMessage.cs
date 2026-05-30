using ZGF.Gui;

namespace GitGui;

public readonly record struct ShowDialogMessage(Func<Action, View> CreateDialog);
