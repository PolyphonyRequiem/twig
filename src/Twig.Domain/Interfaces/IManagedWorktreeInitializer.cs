using Twig.Domain.Common;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Aggregates the two AB#738 storage seams — the worktree-local attachment
/// store and the system-store worktree registry — behind a single public
/// entry point <c>twig init</c> can invoke without dragging the underlying
/// internal interfaces into its public constructor signature. The default
/// implementation runs §6.3 steps 4–7 (local layout markers) followed by
/// §9.5 step 5's connection + worktree upsert as one composed action.
/// <para>
/// Every §8 storage failure surfaces on <see cref="Result.Error"/> so the
/// init verb can decide whether to abort or degrade. A partial failure is
/// safe to re-run: the underlying primitives are idempotent.
/// </para>
/// </summary>
public interface IManagedWorktreeInitializer
{
    /// <summary>Run local layout + system registration for the current
    /// checkout. <paramref name="organization"/> and
    /// <paramref name="project"/> are the connection binding this run
    /// registers; <paramref name="team"/> is the optional team column on
    /// the connections row.</summary>
    Task<Result> InitializeAsync(string organization, string project, string? team, CancellationToken ct = default);
}
