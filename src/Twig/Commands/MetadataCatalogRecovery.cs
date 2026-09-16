using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Process;

namespace Twig.Commands;

/// <summary>
/// AB#879. Command-layer readiness helper: when a read touches a local metadata catalog
/// (field definitions or process types) that is empty, or when the specific name it
/// asked for is absent, run one targeted metadata-only sync via the existing
/// <see cref="FieldDefinitionSyncService"/> / <see cref="ProcessTypeSyncService"/> and
/// re-check. Never enumerates work items, never flushes pending writes, and never
/// hints at <c>twig refresh</c> (which is a work-item pull). When the caller wants a
/// forcing manual refresh, <c>twig process --refresh</c> is the metadata-only entry
/// point.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the CLI project on purpose: this is a command-layer readiness rule, not
/// a Domain concept. Keeping it out of the shipped Domain API avoids an RS0016 surface
/// and pins the boundary to where the decision is actually made (in the command that
/// is about to error or succeed).
/// </para>
/// <para>
/// <b>The predicate is "already have what I need", not "is the store nonempty".</b>
/// A nonempty catalog cannot prove completeness — a valid field a customer added
/// yesterday will look identical to a typo until a fresh sync happens. So the helper
/// takes a caller-supplied <c>hasWhatINeed</c> probe: empty is always miss; nonempty
/// with the specific name absent is also a miss. That way the "Unknown" error path is
/// only reached after an authoritative catalog says so.
/// </para>
/// </remarks>
internal static class MetadataCatalogRecovery
{
    internal enum State { Ready, NotReady, RefreshFailed }

    internal readonly record struct Result(State Outcome, string? RefreshError);

    /// <summary>
    /// Ensures the field-definition catalog can authoritatively answer the caller's
    /// question. Contract:
    /// <list type="bullet">
    ///   <item>Local catalog already satisfies <paramref name="hasWhatINeed"/> → <see cref="State.Ready"/>.</item>
    ///   <item>Local misses AND no recovery source wired → <see cref="State.NotReady"/> (unconditional; callers do NOT gate on catalog count).</item>
    ///   <item>Recovery threw → <see cref="State.RefreshFailed"/> with the propagated exception text.</item>
    ///   <item>Recovery succeeded → <see cref="State.Ready"/> iff the refreshed catalog has any rows. The source spoke; the caller now decides whether the specific name it wanted is present (a missing name after a Ready outcome is a genuine "Unknown", not a not-ready).</item>
    ///   <item>Recovery succeeded but the refreshed catalog is still empty → <see cref="State.NotReady"/>. The source did not actually populate anything, so we cannot answer authoritatively.</item>
    /// </list>
    /// Cancellation propagates unchanged.
    /// </summary>
    internal static async Task<Result> EnsureFieldsAsync(
        IFieldDefinitionStore fieldDefinitionStore,
        IIterationService? iterationService,
        Func<IReadOnlyList<Domain.ValueObjects.FieldDefinition>, bool> hasWhatINeed,
        CancellationToken ct)
    {
        var current = await fieldDefinitionStore.GetAllAsync(ct);
        if (current is not null && hasWhatINeed(current))
            return new Result(State.Ready, null);

        if (iterationService is null)
            return new Result(State.NotReady, null);

        int synced;
        try
        {
            synced = await FieldDefinitionSyncService.SyncAsync(iterationService, fieldDefinitionStore, ct, strict: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(State.RefreshFailed, ex.Message);
        }

        // AB#879 authoritative signal: use the SYNC RETURN COUNT, not the post-sync
        // cached count. FieldDefinitionSyncService returns 0 without touching the store
        // when the remote list is empty, so a stale populated cache would falsely read
        // as Ready. If the source spoke and returned zero rows, the catalog is not
        // ready — regardless of whatever old rows the cache still carries.
        return synced > 0
            ? new Result(State.Ready, null)
            : new Result(State.NotReady, null);
    }

    /// <summary>
    /// Ensures the process-type catalog contains everything the caller needs.
    /// Same shape as <see cref="EnsureFieldsAsync"/>.
    /// </summary>
    internal static async Task<Result> EnsureProcessTypesAsync(
        IProcessTypeStore processTypeStore,
        IIterationService? iterationService,
        Func<IReadOnlyList<Domain.Aggregates.ProcessTypeRecord>, bool> hasWhatINeed,
        CancellationToken ct)
    {
        var current = await processTypeStore.GetAllAsync(ct);
        if (current is not null && hasWhatINeed(current))
            return new Result(State.Ready, null);

        if (iterationService is null)
            return new Result(State.NotReady, null);

        int synced;
        try
        {
            synced = await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore, ct, strict: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(State.RefreshFailed, ex.Message);
        }

        // AB#879 authoritative signal: sync return count > 0 means the source actually
        // populated the store this call. A stale populated cache with a zero-count
        // remote read would otherwise be indistinguishable from success.
        return synced > 0
            ? new Result(State.Ready, null)
            : new Result(State.NotReady, null);
    }

    /// <summary>
    /// Common stderr phrasing. Shared so the two commands (and future ones) can't drift
    /// on the substrings BaselineNative's read-forwarding regression pins to.
    /// </summary>
    internal static class Messages
    {
        internal const string NotReadyRerunHint = "Run 'twig process --refresh' to force a metadata-only sync.";

        internal static string FieldsNotReady(string? because = null)
            => (because is null
                ? "Metadata not ready: field-definition catalog is empty or incomplete. "
                : $"Metadata not ready: field-definition catalog is empty or incomplete ({because}). ")
                + NotReadyRerunHint;

        internal static string FieldsRefreshFailed(string? reason)
            => $"Metadata refresh failed while validating --field: {reason}.";

        internal static string ProcessTypesNotReady(string? because = null)
            => (because is null
                ? "Metadata not ready: process-type catalog is empty or incomplete. "
                : $"Metadata not ready: {because}. ")
                + NotReadyRerunHint;

        internal static string ProcessTypesRefreshFailed(string? reason)
            => $"Metadata refresh failed while resolving process type: {reason}.";
    }
}
