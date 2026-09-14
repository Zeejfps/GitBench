using GitBench.Features.Review.Walkthrough;

namespace GitBench.Messages;

// Broadcast from a review window when the built-in assistant is the one to narrate its walkthrough:
// the reviewer asked for one from the header, or — mid-walkthrough, with the assistant driving —
// stepped past the last step it sent or asked a question. The cue becomes the assistant's next user
// turn in the conversation of the window's repository, which is the one its tools are bound to;
// the main window's active repository plays no part. Handled by AssistantSessionStore.
internal readonly record struct NarrateWalkthroughMessage(Guid RepoId, WalkthroughCue Cue);
