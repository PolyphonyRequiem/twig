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
///     <c>AssemblyAttributes.cs</c>). Nothing else in the suite observes it, so deleting
///     it would go green forever and the twig#311 stall would silently return — the
///     "a guard nobody checks is not a guard" shape this repo has been bitten by before.
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
///     <para>
///     A second test was written and then deliberately REMOVED: it tried to assert the
///     mitigation's premise (that every test class here can reach Terminal.Gui, which is
///     why the switch is assembly-wide rather than per-class). Three implementations were
///     attempted — member-signature reflection, an IL token scan, and a source-text
///     search — and each was wrong in a way its own first run exposed. The version that
///     finally passed did so by grepping source text, which would pass on a class that
///     merely mentions the name in a comment and fail on one that reaches Terminal.Gui
///     through a helper. That is an assertion pointed at the wrong surface, and a guard
///     that cannot fail for the right reason is worse than no guard, because it implies
///     coverage that does not exist. The premise is documented in
///     <c>AssemblyAttributes.cs</c> and recomputed on demand by
///     <c>tools/repro-311/ab524-precondition.py</c>, which reads the sources directly and
///     is honest about being a static approximation.
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
}
