using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Answers "which iteration covers now?" from LOCAL state — the cached iteration list plus the
/// local clock. Never a network call.
/// <para>
/// 🔴 This exists because a Bench must evaluate offline (docs/specs/bench.spec.md). Storing a
/// named sprint on the Bench would freeze the view when the sprint ends; storing "ask which
/// sprint is current" would make every look a network round trip and break ruling 0004 §3.
/// Splitting the two settles it: the Bench stores the stable RULE ("the sprint covering today"),
/// and the iteration-path-to-date-range mapping is cached local data that the refresh path
/// updates when twig already talks to ADO.
/// </para>
/// <para>
/// The mapping lives in the DISPOSABLE mirror, not the durable store, by 0005's test: ADO can
/// rebuild it, because it is a copy of ADO's own iteration list. Nothing is lost if it is
/// dropped — the next refresh restores it.
/// </para>
/// <para>
/// Consequence, accepted deliberately: if somebody moves an iteration's dates in ADO and the
/// person has not refreshed, twig answers from the last known dates. That is the same staleness
/// every other cached read already has, rather than a new exemption.
/// </para>
/// </summary>
public interface IIterationCalendar
{
    /// <summary>
    /// The iterations whose date range covers now, from cached data. Empty when the calendar has
    /// never been populated or no iteration covers today.
    /// </summary>
    Task<IReadOnlyList<IterationPath>> GetCurrentIterationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the cached iteration list. Called by the refresh path, which already holds an ADO
    /// connection — evaluation never triggers this.
    /// </summary>
    Task SaveAsync(IReadOnlyList<TeamIteration> iterations, CancellationToken ct = default);
}
