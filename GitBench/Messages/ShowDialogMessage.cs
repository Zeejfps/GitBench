using ZGF.Gui;

namespace GitBench;

public readonly record struct ShowDialogMessage(Func<Action, View> CreateDialog);
