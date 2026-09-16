using Xunit;

namespace GitBench.Tests;

/// <summary>Opt-in tests using Metal on macOS or OpenGL on Windows/Linux.</summary>
internal sealed class GpuTheoryAttribute : TheoryAttribute
{
    public GpuTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIFFDINO_GPU_TESTS") != "1")
            Skip = "Set DIFFDINO_GPU_TESTS=1 on a machine with Metal (macOS) or OpenGL 4.1 (Windows/Linux).";
    }
}
