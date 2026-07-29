namespace GitBench.Messages;

// Broadcast when someone picks an assistant action off a live diff selection. The prompt is already
// composed — the selection quoted with its path, line range and which side of the diff it is —
// because the menu is where the wording is localized and where the rows are still in hand.
//
// AgentName names a preset agent to run one-shot, its answer landing in the overlay without joining
// the repository's thread. Null is the free-form case: the overlay opens with the quote in the
// composer and whatever the person types continues the thread as any other message would.
// Handled by AssistantViewModel.
public readonly record struct AskAssistantAboutSelectionMessage(string? AgentName, string Prompt);
