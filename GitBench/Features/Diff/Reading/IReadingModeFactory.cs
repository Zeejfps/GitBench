namespace GitBench.Features.Diff.Reading;

/// <summary>
/// Builds reading mode for one repository, or declines to.
/// </summary>
/// <remarks>
/// The seam exists so a review surface does not have to know how the assistant is configured. When
/// no provider is set up, or the build has no assistant at all, the factory returns null and the
/// surface simply never offers the toggle — rather than offering one that fails when pressed.
/// </remarks>
internal interface IReadingModeFactory
{
    ReadingModeCoordinator? Create(Guid repoId);
}
