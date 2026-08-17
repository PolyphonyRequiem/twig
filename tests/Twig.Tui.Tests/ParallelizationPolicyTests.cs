using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

namespace Twig.Tui.Tests;

/// <summary>
/// AB#524 regression guard.
/// </summary>
/// <remarks>
///     <para>
///     The mitigation for AB#524 is a single assembly-level attribute
///     (<c>CollectionBehavior(DisableTestParallelization = true)</c> in
///     <c>AssemblyAttributes.cs</c>). Nothing else in the suite observes it, so
///     deleting it would go green forever and the twig#311 stall would silently
///     return — the exact "a guard nobody checks is not a guard" shape this repo has
///     been bitten by before.
///     </para>
///     <para>
///     Why the attribute is needed: Terminal.Gui (pinned 2.0.0-develop.5185) runs
///     <c>ConfigProperty.Initialize</c> from a <c>[ModuleInitializer]</c>, which walks
///     every loaded assembly calling <c>GetTypes()</c> — a type-loading reflection walk
///     performed while the CLR holds that module's type-init lock, taking the CLR
///     class-loader lock that other parallel collections need. Captured deadlocked
///     2026-08-16: 20 threads, all sleeping, zero runnable, unchanged over 90s.
///     Full evidence: <c>docs/research/terminal-gui-module-initializer-deadlock.md</c>.
///     </para>
///     <para>
///     🔴 This guard proves the attribute is PRESENT. It cannot prove the deadlock is
///     absent — a timing race cannot be proven absent by a passing test, and this
///     assembly ran green thousands of times before the stall was ever captured. Treat
///     it as pinning the decision, not as verifying the outcome.
///     </para>
/// </remarks>
public class ParallelizationPolicyTests
{
    [Fact]
    public void Assembly_DisablesTestParallelization_SoTheTerminalGuiModuleCctorIsNotRaced()
    {
        var attribute = typeof(ParallelizationPolicyTests).Assembly
            .GetCustomAttributes<CollectionBehaviorAttribute>()
            .SingleOrDefault();

        attribute.ShouldNotBeNull(
            "AB#524: Twig.Tui.Tests must declare [assembly: CollectionBehavior(...)]. " +
            "Without it, every test class runs as its own parallel collection and they " +
            "race the Terminal.Gui module initializer, which deadlocks on the CLR " +
            "class-loader lock. See docs/research/terminal-gui-module-initializer-deadlock.md.");

        attribute.DisableTestParallelization.ShouldBeTrue(
            "AB#524: DisableTestParallelization must remain true. Terminal.Gui's " +
            "[ModuleInitializer] does an unbounded GetTypes() walk under the type-init " +
            "lock; concurrent collections deadlock against the class-loader lock. " +
            "Do NOT 'fix' a recurrence by raising TestSessionTimeout — the captured " +
            "process had ZERO runnable threads, so more time cannot help.");
    }

    [Fact]
    public void EveryTestClassHere_CanReachTerminalGui_WhichIsWhyTheWholeAssemblyIsSerialised()
    {
        // Pins the PREMISE of the mitigation rather than restating its conclusion: the
        // reason this is an assembly-wide switch and not a per-class opt-out is that
        // every test class in here can trigger the Terminal.Gui module cctor — three by
        // naming Terminal.Gui types directly, and DetailDocumentSourceTests /
        // PendingChangeStoreSinkTests transitively through Twig.Tui types.
        //
        // If a future class genuinely cannot reach Terminal.Gui, this test going red is
        // informative rather than wrong: re-run tools/repro-311/ab524-precondition.py and
        // decide deliberately, do not just delete the assertion.
        var testClasses = typeof(ParallelizationPolicyTests).Assembly
            .GetTypes()
            .Where(t => t.IsClass && t.IsPublic && t != typeof(ParallelizationPolicyTests))
            .Where(t => t.GetMethods().Any(m =>
                m.GetCustomAttributes<FactAttribute>().Any() ||
                m.GetCustomAttributes<TheoryAttribute>().Any()))
            .ToList();

        // Guard the sweep itself: an empty set would make the assertion below vacuous.
        testClasses.ShouldNotBeEmpty("reflection sweep found no test classes — the guard would be vacuous");

        var unreachable = testClasses
            .Where(t => !ReferencesTerminalGuiOrTwigTui(t))
            .Select(t => t.Name)
            .ToList();

        unreachable.ShouldBeEmpty(
            "AB#524: every class here was expected to be able to reach Terminal.Gui, " +
            "which is why parallelization is disabled assembly-wide.");
    }

    private static bool ReferencesTerminalGuiOrTwigTui(System.Type testClass)
    {
        // A class reaches Terminal.Gui if any member signature it declares mentions a
        // Terminal.Gui type, or a Twig.Tui type (Twig.Tui is compiled against
        // Terminal.Gui, so touching it loads that module too).
        static bool IsTrigger(System.Type? t) =>
            t?.Assembly.GetName().Name is "Terminal.Gui" or "Twig.Tui";

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static
                               | BindingFlags.DeclaredOnly;

        if (testClass.GetFields(All).Any(f => IsTrigger(f.FieldType)))
        {
            return true;
        }

        if (testClass.GetProperties(All).Any(p => IsTrigger(p.PropertyType)))
        {
            return true;
        }

        return testClass.GetMethods(All).Any(m =>
            IsTrigger(m.ReturnType) || m.GetParameters().Any(p => IsTrigger(p.ParameterType)));
    }
}
