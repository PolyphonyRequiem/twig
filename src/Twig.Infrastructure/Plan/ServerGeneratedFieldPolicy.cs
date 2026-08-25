using Twig.Domain.Aggregates;
using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// The ONE policy that decides whether a post-PATCH readback difference on a given field is
/// ADO's own <b>server-generated</b> normalization rather than a real contradiction of the
/// plan's intent (AB#754, spec #753).
/// <para>
/// This is deliberately NOT an ignore list. Three things must ALL hold before a difference is
/// downgraded from "the write did not land" to "the write landed and ADO rewrote its own
/// bookkeeping":
/// </para>
/// <list type="number">
///   <item><b>The field must be server-generated</b> — a member of
///     <see cref="ServerGeneratedFields"/>. Every entry is a field ADO computes itself on
///     every revision (close stamps and change stamps); a client value for one of them is
///     advisory at best, and ADO overwrites it as a matter of documented behaviour.</item>
///   <item><b>The refreshed item must prove the intended mutation landed.</b> Every
///     user-authored field in the same batch — anything NOT in the generated set — must match
///     exactly under the ordinary comparator, INCLUDING the requested lifecycle state and any
///     required terminal values. A batch whose <c>System.State</c> did not land is never
///     warning-verified; the generated stamps only ever ride along with a proven mutation.</item>
///   <item><b>The readback itself must be complete and fresh</b> — the caller only consults
///     this policy after the refreshed revision advanced past the expected revision. A
///     missing, stale, or unavailable readback never reaches here at all.</item>
/// </list>
/// <para>
/// Why an explicit set rather than <c>FieldDefinition.IsReadOnly</c> alone: the field metadata
/// cache does mark these read-only, but it also marks fields read-only that a plan has no
/// business writing at all, and a plan that DID name one must still fail loudly. Ownership by
/// ADO's revision machinery is a narrower property than read-only, so it is named here and
/// justified per field. The field-definition store is still consulted (see
/// <see cref="PlanOperationExecutor"/>) so a field the process does not even declare cannot be
/// warning-verified.
/// </para>
/// </summary>
internal static class ServerGeneratedFieldPolicy
{
    /// <summary>
    /// Fields ADO's own revision machinery writes on every save. Each is here because the
    /// server derives it from the transition it just performed, so an authored value can
    /// never survive the PATCH:
    /// <list type="bullet">
    ///   <item><c>System.ChangedDate</c> / <c>System.ChangedBy</c> — stamped by the server on
    ///     every revision from the request clock and the calling identity.</item>
    ///   <item><c>Microsoft.VSTS.Common.ClosedDate</c> / <c>ClosedBy</c> — stamped by the
    ///     server when a state transition enters a Completed/Removed category.</item>
    ///   <item><c>Microsoft.VSTS.Common.StateChangeDate</c> — stamped by the server on any
    ///     <c>System.State</c> change.</item>
    /// </list>
    /// </summary>
    internal static readonly IReadOnlySet<string> ServerGeneratedFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.ChangedDate",
            "System.ChangedBy",
            "Microsoft.VSTS.Common.ClosedDate",
            "Microsoft.VSTS.Common.ClosedBy",
            "Microsoft.VSTS.Common.StateChangeDate",
        };

    internal static bool IsServerGenerated(string referenceName)
        => ServerGeneratedFields.Contains(referenceName);

    /// <summary>
    /// True when every field of <paramref name="batch"/> that is NOT server-generated is
    /// absent from <paramref name="normalizedFields"/> — i.e. the ONLY differences observed
    /// were server-generated ones. This is the "the intended mutation landed" half of the
    /// gate: it is evaluated by the caller only after every non-generated field compared
    /// equal, so it is a defensive restatement rather than the sole guard.
    /// </summary>
    internal static bool OnlyServerGeneratedDiffered(
        BatchOperation batch,
        IReadOnlyCollection<string> normalizedFields)
    {
        foreach (var field in normalizedFields)
        {
            if (!IsServerGenerated(field))
                return false;
            if (!batch.Fields.ContainsKey(field))
                return false;
        }
        return normalizedFields.Count > 0;
    }

    /// <summary>
    /// Renders the human-readable warning detail carried alongside the Verified outcome.
    /// Deliberately names each field and both values so the ledger records WHAT ADO rewrote,
    /// not merely that something was rewritten.
    /// </summary>
    internal static string FormatWarning(IReadOnlyList<ServerGeneratedNormalization> normalizations)
    {
        var parts = normalizations
            .Select(n => $"{n.ReferenceName} (requested '{n.Expected}', server '{n.Actual ?? "(absent)"}')");
        return "ADO normalized server-generated field(s) after apply: " + string.Join("; ", parts)
            + ". The requested mutation is proven landed by the refreshed read; these fields are "
            + "owned by ADO's revision machinery and cannot be authored by a plan.";
    }

    /// <summary>
    /// True when <paramref name="item"/> proves the batch's requested lifecycle state landed.
    /// A batch that did not name <c>System.State</c> trivially satisfies this — there was no
    /// lifecycle intent to prove. A batch that DID name it must observe exactly that state on
    /// the refreshed item.
    /// </summary>
    internal static bool RequestedLifecycleStateLanded(BatchOperation batch, WorkItem item)
    {
        if (!TryGetRequestedState(batch, out var requested))
            return true;
        if (requested is null)
            return string.IsNullOrEmpty(item.State);

        if (string.Equals(item.State, requested, StringComparison.Ordinal))
            return true;
        return item.Fields.TryGetValue("System.State", out var mirrored)
            && string.Equals(mirrored, requested, StringComparison.Ordinal);
    }

    private static bool TryGetRequestedState(BatchOperation batch, out string? requested)
    {
        foreach (var kv in batch.Fields)
        {
            if (string.Equals(kv.Key, "System.State", StringComparison.OrdinalIgnoreCase))
            {
                requested = kv.Value;
                return true;
            }
        }
        requested = null;
        return false;
    }
}

/// <summary>
/// One observed server-generated normalization: what the plan asked for and what the refreshed
/// ADO read reported instead.
/// </summary>
internal readonly record struct ServerGeneratedNormalization(
    string ReferenceName,
    string Expected,
    string? Actual);
