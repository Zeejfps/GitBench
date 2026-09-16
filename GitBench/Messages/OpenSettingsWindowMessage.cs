using GitBench.Features.Settings;

namespace GitBench.Messages;

/// <summary>Opens the settings window on a page — the general one unless a caller has a reason to
/// land somewhere else, the way the assistant's gear lands on its own page.</summary>
internal readonly record struct OpenSettingsWindowMessage(SettingsPage Page = SettingsPage.General);
