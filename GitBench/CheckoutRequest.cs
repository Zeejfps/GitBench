namespace GitGui;

internal readonly record struct CheckoutRequest(
    Repo Repo,
    string RemoteName,
    string RemoteBranchName,
    string SuggestedLocalName);