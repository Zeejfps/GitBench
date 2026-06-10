using ZGF.Gui;

namespace GitBench.Messages;

public readonly record struct ShowDialogMessage(Func<Action, View> CreateDialog);
