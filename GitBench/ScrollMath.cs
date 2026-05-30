namespace GitGui;

internal static class ScrollMath
{
    /// <summary>
    /// Clamps <paramref name="scrollPos"/> into the valid range for a viewport of
    /// <paramref name="viewportSize"/> showing content of <paramref name="contentSize"/>.
    /// When the content fits within the viewport, the result is 0.
    /// </summary>
    public static float ClampScroll(float scrollPos, float contentSize, float viewportSize)
    {
        var max = MaxScroll(contentSize, viewportSize);
        if (scrollPos < 0f) return 0f;
        return scrollPos > max ? max : scrollPos;
    }

    public static float MaxScroll(float contentSize, float viewportSize)
    {
        var max = contentSize - viewportSize;
        return max < 0f ? 0f : max;
    }
}
