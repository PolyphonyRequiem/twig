using System.Reflection;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

/// <summary>
/// A ceiling on command constructor size, so critique finding 8 stops relocating (issue #319).
/// </summary>
/// <remarks>
/// <para>
/// Finding 8 ("Command Layer Bloat", severity High) named <c>StatusCommand</c> and
/// <c>SetCommand</c> at 15–17 constructor parameters. Both were fixed — <c>StatusCommand</c> is
/// gone and <c>SetCommand</c> is down to 7 — and the shape simply moved: re-baselining found
/// <c>WorkspaceCommand</c> at 14, <c>ShowCommand</c> at 15, and <c>UpdateCommand</c> at 14.
/// </para>
/// <para>
/// <b>The critique's prescribed remedy does not fix this.</b> It proposed an aggregate parameter
/// object; <c>WorkspaceCommand</c> already takes <see cref="CommandContext"/> as its first
/// parameter and is still at 14. So the split is a real design slice, not a mechanical
/// refactor, and it is deliberately NOT attempted here.
/// </para>
/// <para>
/// What this test does is stop the bleed. The ceiling is set at the current worst case rather
/// than at an aspirational target: a guard that fails on day one gets suppressed, and a finding
/// that keeps relocating unnoticed is exactly how finding 8 survived being "fixed" twice. New
/// commands cannot exceed today's worst, and the known offenders are listed explicitly so
/// splitting one is a visible, deliberate act.
/// </para>
/// <para>
/// <b>Lowering the ceiling is the point.</b> When an offender is split, drop it from
/// <see cref="KnownOffenders"/> — and when the list empties, lower <see cref="Ceiling"/>. Raising
/// the ceiling to make a new command pass is the failure mode this guard exists to prevent.
/// </para>
/// </remarks>
public sealed class CommandConstructorSizeTests
{
    /// <summary>
    /// The current worst case. Not a target — a ratchet. See the remarks before changing it.
    /// </summary>
    private const int Ceiling = 15;

    /// <summary>
    /// Commands at or near the ceiling as of issue #319. Each is a standing invitation to split;
    /// none may GROW. Listing them means a split shows up as a deliberate edit here rather than
    /// silently changing a number nobody reads.
    /// </summary>
    private static readonly Dictionary<string, int> KnownOffenders = new(StringComparer.Ordinal)
    {
        ["ShowCommand"] = 15,
        ["WorkspaceCommand"] = 14,
        ["UpdateCommand"] = 14,
    };

    private static IReadOnlyList<(string Name, int Count)> DiscoverCommands() =>
        typeof(WorkspaceCommand).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Command", StringComparison.Ordinal)
                        && t.GetConstructors().Length > 0)
            .Select(t => (
                t.Name,
                Count: t.GetConstructors()
                    .Select(c => c.GetParameters().Length)
                    .DefaultIfEmpty(0)
                    .Max()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// No command may exceed the ceiling. A new one that does is finding 8 relocating again.
    /// </summary>
    [Fact]
    public void NoCommandExceedsTheConstructorCeiling()
    {
        var over = DiscoverCommands().Where(c => c.Count > Ceiling).ToList();

        over.ShouldBeEmpty(
            $"critique finding 8 (command layer bloat) has relocated again — these exceed the " +
            $"ceiling of {Ceiling} constructor parameters: " +
            string.Join(", ", over.Select(c => $"{c.Name}({c.Count})")) +
            ". Split the command; do NOT raise the ceiling. Note the aggregate-parameter remedy " +
            "the critique prescribed is already applied to WorkspaceCommand and did not help, " +
            "so this needs a rendering/service seam rather than another context object");
    }

    /// <summary>
    /// The ratchet. A known offender may shrink (split it — then update this list) but never grow.
    /// </summary>
    [Fact]
    public void KnownOffendersDoNotGrow()
    {
        var actual = DiscoverCommands().ToDictionary(c => c.Name, c => c.Count, StringComparer.Ordinal);

        foreach (var (name, recorded) in KnownOffenders)
        {
            actual.ShouldContainKey(name,
                $"{name} no longer exists — remove it from KnownOffenders so the list cannot rot");

            actual[name].ShouldBeLessThanOrEqualTo(recorded,
                $"{name} grew from {recorded} to {actual[name]} constructor parameters. It was " +
                "already listed as a known instance of critique finding 8; adding to it moves in " +
                "the wrong direction");
        }
    }

    /// <summary>
    /// Non-vacuity control. A discovery predicate that matched nothing would make both guards
    /// above pass forever, so pin that the sweep really finds commands and really sees the
    /// offenders it claims to be ratcheting.
    /// </summary>
    [Fact]
    public void TheDiscoverySweepActuallyFindsCommands()
    {
        var discovered = DiscoverCommands();

        discovered.ShouldNotBeEmpty("the type sweep found nothing — both guards would pass vacuously");
        discovered.Select(c => c.Name).ShouldContain(nameof(WorkspaceCommand));
        discovered.Single(c => c.Name == nameof(WorkspaceCommand)).Count
            .ShouldBeGreaterThan(1, "a command resolved to a trivial constructor — the sweep is " +
                                    "reading the wrong constructor and the ratchet is meaningless");
    }
}
