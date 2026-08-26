using Twig.Domain.Common;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Aggregates the two AB#738 storage seams — the worktree-local attachment
/// store and the system-store worktree registry — behind a single public
/// entry point <c>twig init</c> can invoke without dragging the underlying
/// internal interfaces into its public constructor signature. The default
/// implementation runs §6.3 steps 4–7 (local layout markers) followed by
/// §9.5 step 5's connection + worktree upsert, and materializes the
/// AB#736 §4.1 checked-in policy block on <c>twig.json</c> so downstream
/// attach commands see a valid selected-profile binding + primary-scope
/// allow-set (no permanently-unavailable eligibility default).
/// <para>
/// Every §8 storage failure surfaces on <see cref="Result.Error"/> so the
/// init verb can decide whether to abort or degrade. A partial failure is
/// safe to re-run: the underlying primitives are idempotent.
/// </para>
/// </summary>
public interface IManagedWorktreeInitializer
{
    /// <summary>Run local layout + system registration + policy
    /// materialization for the current checkout. <paramref name="organization"/>
    /// and <paramref name="project"/> are the connection binding this run
    /// registers; <paramref name="team"/> is the optional team column on the
    /// connections row. <paramref name="profileIdentity"/> +
    /// <paramref name="profileVersion"/> are the AB#736 §4.1 selected
    /// profile binding this manifest materializes (e.g. the process template
    /// name and a version stamp).</summary>
    Task<Result> InitializeAsync(
        string organization,
        string project,
        string? team,
        string profileIdentity,
        string profileVersion,
        CancellationToken ct = default);
}
