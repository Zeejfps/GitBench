using GitBench.Git;

namespace GitBench.Features.Branches;

internal readonly record struct CheckoutRequest(
    Repo Repo,
    string RemoteName,
    string RemoteBranchName,
    string SuggestedLocalName);