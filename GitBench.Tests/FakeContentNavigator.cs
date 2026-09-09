using GitBench.App;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>The content panel's trail, counted rather than walked, for tests about the things that
/// ask it to move.</summary>
internal sealed class FakeContentNavigator : IContentNavigator
{
    private readonly State<bool> _canGoBack = new(true);
    private readonly State<bool> _canGoForward = new(true);

    public List<ContentPlace> Shown { get; } = [];

    public int Backs { get; private set; }

    public int Forwards { get; private set; }

    public IReadable<bool> CanGoBack => _canGoBack;

    public IReadable<bool> CanGoForward => _canGoForward;

    public void GoBack() => Backs++;

    public void GoForward() => Forwards++;

    public void Show(ContentPlace place) => Shown.Add(place);
}
