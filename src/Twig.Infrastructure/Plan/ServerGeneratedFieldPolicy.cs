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
    /// True when every entry in <paramref name="normalizations"/> is a difference this policy
    /// can actually account for on <paramref name="batch"/> — the field was requested by the
    /// batch, and a <see cref="NormalizationKind.ServerGenerated"/> classification also holds
    /// up against the justified set.
    /// <para>
    /// This is the "the intended mutation landed" half of the gate. The caller reaches it only
    /// after every unexplained field already compared equal, so it is a defensive restatement
    /// rather than the sole guard — but it is the restatement that would catch a future caller
    /// recording a normalization the policy never sanctioned.
    /// </para>
    /// </summary>
    internal static bool OnlyExplainedDifferencesRemain(
        BatchOperation batch,
        IReadOnlyCollection<PlanReadbackNormalization> normalizations)
    {
        foreach (var normalization in normalizations)
        {
            // Every recorded normalization must name a field the plan actually asked for —
            // a difference on a field the batch never mentioned is not this batch's business
            // and must never be used to justify verifying it.
            if (!batch.Fields.ContainsKey(normalization.ReferenceName))
                return false;

            // A ServerGenerated classification must still satisfy the justified-set test.
            // CanonicalizedHtml and CanonicalizedIdentity carry their own evidence — the
            // field's declared html data type or isIdentity flag, plus a comparison that
            // already succeeded against that specific metadata — so they do not.
            if (normalization.Kind == NormalizationKind.ServerGenerated
                && !IsServerGenerated(normalization.ReferenceName))
            {
                return false;
            }
        }
        return normalizations.Count > 0;
    }

    /// <summary>
    /// Renders the human-readable warning detail carried alongside the Verified outcome.
    /// Deliberately names each field and both values so the ledger records WHAT ADO rewrote,
    /// not merely that something was rewritten.
    /// </summary>
    internal static string FormatWarning(IReadOnlyList<PlanReadbackNormalization> normalizations)
    {
        var serverGenerated = normalizations
            .Where(n => n.Kind == NormalizationKind.ServerGenerated)
            .ToList();
        var html = normalizations
            .Where(n => n.Kind == NormalizationKind.CanonicalizedHtml)
            .ToList();
        var identities = normalizations
            .Where(n => n.Kind == NormalizationKind.CanonicalizedIdentity)
            .ToList();

        var segments = new List<string>(3);
        if (serverGenerated.Count > 0)
        {
            var parts = serverGenerated
                .Select(n => $"{n.ReferenceName} (requested '{n.Expected}', server '{n.Actual ?? "(absent)"}')");
            segments.Add(
                "ADO normalized server-generated field(s) after apply: " + string.Join("; ", parts)
                + ". These fields are owned by ADO's revision machinery and cannot be authored "
                + "by a plan.");
        }
        if (html.Count > 0)
        {
            // Deliberately NOT echoing both markup blobs: a description field is routinely
            // kilobytes, and a warning that dumps two copies of it into the journal is
            // unreadable in CLI output and useless in a log. The field name plus the
            // equivalence claim is the actionable content; the values are one refreshed read
            // away for anyone who wants them.
            segments.Add(
                "ADO canonicalized HTML field(s) after apply: "
                + string.Join(", ", html.Select(n => n.ReferenceName))
                + ". The markup is structurally equivalent to what the plan authored; only "
                + "ADO's serialization differs.");
        }
        if (identities.Count > 0)
        {
            // Both values ARE echoed here, unlike the html case: an identity is one short
            // line, and which account ADO resolved the write to is exactly the fact a
            // reader of this warning needs. Suppressing it would hide the only detail that
            // distinguishes a correct resolution from a surprising one.
            var parts = identities
                .Select(n => $"{n.ReferenceName} (requested '{n.Expected}', server '{n.Actual ?? "(absent)"}')");
            segments.Add(
                "ADO re-rendered identity field(s) after apply: " + string.Join("; ", parts)
                + ". Each names the same account the plan authored; only ADO's rendering "
                + "of it differs.");
        }

        return string.Join(" ", segments)
            + " The requested mutation is proven landed by the refreshed read.";
    }

    /// <summary>
    /// The lifecycle/terminal-contract fields whose equality this policy must NEVER be able
    /// to excuse. Spec #753 user story 7: "System.State=Done and Custom.TerminalOutcome=
    /// completed remain an atomic, strict terminal contract."
    /// <para>
    /// Keeping the coupling honest is a STATIC property, not a runtime branch: because none
    /// of these is server-generated, a batch whose transition did not land fails strict
    /// comparison in the executor's field loop long before any normalization is considered.
    /// The danger is not a missing runtime check — it is a future edit quietly adding a
    /// lifecycle-adjacent field to <see cref="ServerGeneratedFields"/>. This set exists so
    /// that edit breaks a test instead.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlySet<string> TerminalContractFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.State",
            "Custom.TerminalOutcome",
        };
}

/// <summary>
/// Why a readback difference was classified as normalization rather than contradiction.
/// <para>
/// Both kinds are the SAME fact — the write landed and something outside Twig's control
/// rewrote the stored form — so both take the same warning-verified path. The distinction
/// exists only so the recorded warning names WHICH kind of rewrite happened; a reader of the
/// journal should not have to infer that from the field name.
/// </para>
/// </summary>
internal enum NormalizationKind
{
    /// <summary>ADO's revision machinery owns the value (close/change stamps). AB#754.</summary>
    ServerGenerated,

    /// <summary>
    /// ADO re-serialized HTML the plan authored. The CONTENT compared equal under
    /// <see cref="HtmlStructuralComparer"/>; only the markup's serialization differs. AB#755.
    /// </summary>
    CanonicalizedHtml,

    /// <summary>
    /// ADO re-rendered an identity the plan authored. The stable identity key compared
    /// equal under <see cref="IdentityValueComparer"/>; only ADO's rendering of the same
    /// account differs. AB#802.
    /// </summary>
    CanonicalizedIdentity,
}

/// <summary>
/// One observed readback normalization: what the plan asked for, what the refreshed ADO read
/// reported instead, and which class of rewrite explains the difference.
/// </summary>
internal readonly record struct PlanReadbackNormalization(
    string ReferenceName,
    string Expected,
    string? Actual,
    NormalizationKind Kind);
