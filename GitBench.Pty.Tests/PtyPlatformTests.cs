using System.Reflection;

namespace GitBench.Pty.Tests;

/// <summary>
/// What the platform gate has to be true of, now that turning it on for Unix unskips two dozen tests
/// at once.
/// </summary>
/// <remarks>
/// A suite that goes from twenty-four skipped to twenty-four passing is indistinguishable, from the
/// outside, from one that went from twenty-four skipped to twenty-four vacuous. These are the tests
/// that tell the two apart: the gate has to agree with the factory about which hosts have a
/// pseudo-terminal, and a test asserting something only one platform does has to say so in its name
/// as well as its attribute.
/// </remarks>
public class PtyPlatformTests
{
    /// <remarks>
    /// Two places now know which platforms have an implementation — this gate and
    /// <see cref="PtySessionFactory"/>'s dispatch — and when they drift the symptom is silence, which
    /// is exactly how two dozen tests sat skipped on macOS without anyone noticing.
    /// </remarks>
    [Fact]
    public void IsSupported_AgreesWithTheFactoryAboutWhetherThisHostHasAPseudoTerminal()
    {
        var options = new PtySessionOptions
        {
            Executable = "gitbench-no-such-program",
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        var thrown = Record.Exception(() => new PtySessionFactory().Start(options));

        if (PtyPlatform.IsSupported)
            Assert.IsType<PtySpawnException>(thrown);
        else
            Assert.IsType<PlatformNotSupportedException>(thrown);
    }

    /// <remarks>
    /// A gate and a name that disagree hide one of two bugs: a test claiming a universal contract it
    /// only ever proves on one host, or a universal contract quietly gated down to one. Neither is
    /// visible in a green run, which is why this is asserted rather than left to review.
    /// </remarks>
    [Fact]
    public void EveryPlatformSpecificTest_SaysSoInItsNameAsWellAsItsGate()
    {
        var offenders = new List<string>();

        foreach (var method in PtyTests())
        {
            var windowsOnly = method.GetCustomAttribute<WindowsPtyFactAttribute>() is not null;
            var unixOnly = method.GetCustomAttribute<UnixPtyFactAttribute>() is not null
                || method.GetCustomAttribute<UnixPtyTheoryAttribute>() is not null;
            var claimsWindows = method.Name.EndsWith(PtyPlatform.WindowsSuffix, StringComparison.Ordinal);
            var claimsUnix = method.Name.EndsWith(PtyPlatform.UnixSuffix, StringComparison.Ordinal);

            if (windowsOnly != claimsWindows || unixOnly != claimsUnix)
                offenders.Add($"{method.DeclaringType!.Name}.{method.Name}");
        }

        Assert.True(
            offenders.Count == 0,
            "A test's gate and its name disagree about which platforms it holds on, so it either claims "
            + "a universal contract it only proves on one host or hides a universal one behind a "
            + "platform gate:\n  " + string.Join("\n  ", offenders));
    }

    static IEnumerable<MethodInfo> PtyTests() =>
        typeof(PtyPlatformTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a is FactAttribute));
}
