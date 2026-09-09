using ZGF.Geometry;
using ZGF.Gui;

namespace GitBench.Controls;

/// <summary>
/// One tab's end of a reorder drag.
/// </summary>
/// <remarks>
/// The strip that owns the tabs implements this; the shared chrome only knows there is something to
/// tell. What a run is, where a tab sits in it and what moving one means all stay with whoever has
/// the run — <see cref="TabChrome"/> would otherwise be carrying the content panel's vocabulary for
/// every other strip that uses it.
/// </remarks>
internal interface ITabDrag
{
    /// <summary>Says which view this tab turned out to be, so a drop can be resolved from where the
    /// tabs were actually laid out.</summary>
    void Register(View view);

    void Unregister(View view);

    void Start(PointF at);

    void Update(PointF at);

    void Complete();

    void Cancel();
}
