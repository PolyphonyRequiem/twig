using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// Concrete <see cref="IAdoClaimProjection"/> that reaches ADO through the
/// existing mutation infrastructure (<see cref="IAdoWorkItemService.PatchAsync"/>
/// + <see cref="ConflictRetryHelper.PatchWithRetryAsync"/>). AB#737 §Mint
/// ordering — step 2 — requires the projection to write the resolved holder
/// onto <c>System.AssignedTo</c> using the exact plan/publish contract every
/// other Twig mutation uses, then <b>verify by readback</b> that ADO now
/// resolves the field to the intended holder before returning success.
/// AB#737 §Named failure vocabulary makes a failed verification an
/// <c>AdoProjectionFailed</c>, not a hidden success: identity normalization,
/// server-side rules, or a workflow refusal that leaves
/// <c>System.AssignedTo</c> pointing at a different identity would otherwise
/// let mint/reclaim promote a local row to <c>active</c> while ADO shows a
/// different holder.
/// <para>
/// The seam is process-agnostic: it treats the primary-scope id as an opaque
/// string that parses into the numeric work-item id, and forwards
/// <c>System.AssignedTo</c> as the sole ADO field written. It never inspects
/// work-item type, state, or process template. AB#737 §Cross-cutting rules —
/// "no ambient identity" — is preserved: the holder is supplied by the
/// caller, never re-resolved here.
/// </para>
/// </summary>
internal sealed class AdoClaimProjection : IAdoClaimProjection
{
    // The single ADO field this seam projects. Fixed because #728 fixes it,
    // not by a process-config lookup: the design contract has no other
    // hard-coded ADO field.
    internal const string AssignedToField = "System.AssignedTo";

    // Named opaque underlying strings the claim service maps to
    // AdoProjectionFailed. Kept as constants so the tests can assert
    // exact-string outcomes without duplicating the copy.
    internal const string InvalidScopeId = "invalid-primary-scope-id";
    internal const string EmptyHolder = "holder-identity-required";
    internal const string ConflictAfterRetry = "ado-conflict-after-retry";
    internal const string ReadbackMissing = "ado-readback-missing";
    internal const string ReadbackMismatch = "ado-readback-mismatch";
    internal const string ClearReadbackNotEmpty = "ado-clear-readback-not-empty";

    private readonly IAdoWorkItemService _ado;

    public AdoClaimProjection(IAdoWorkItemService ado)
    {
        _ado = ado;
    }

    public async Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default)
    {
        if (!TryParseWorkItemId(primaryScopeId, out var workItemId))
            return Result.Fail(InvalidScopeId);
        if (string.IsNullOrWhiteSpace(holder.Identity))
            return Result.Fail(EmptyHolder);

        var intended = string.IsNullOrWhiteSpace(holder.DisplayName) ? holder.Identity : holder.DisplayName!;
        try
        {
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);

            // Idempotent projection: if ADO already resolves AssignedTo to
            // the intended holder, treat it as verified success without a
            // second write. AB#737 §Reclaim over active names the "already
            // matches" branch a no-op-but-verified read.
            if (IdentityMatches(remote.AssignedTo, intended))
                return Result.Ok();

            var change = new FieldChange(AssignedToField, remote.AssignedTo, intended);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);

            // Verify by readback. A successful patch does not guarantee
            // ADO normalized to the intended identity; workflow rules,
            // identity providers, or server-side coercion may resolve to
            // a different value. Refuse silent divergence.
            var verified = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(verified.AssignedTo))
                return Result.Fail(ReadbackMissing);
            if (!IdentityMatches(verified.AssignedTo, intended))
                return Result.Fail($"{ReadbackMismatch}: expected='{intended}' observed='{verified.AssignedTo}'");
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdoConflictException)
        {
            return Result.Fail(ConflictAfterRetry);
        }
        catch (Exception ex)
        {
            return Result.Fail(FormatUnderlying(ex));
        }
    }

    public async Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default)
    {
        if (!TryParseWorkItemId(primaryScopeId, out var workItemId))
            return Result.Fail(InvalidScopeId);
        try
        {
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(remote.AssignedTo))
                return Result.Ok(); // Already cleared; the design permits no-op-verified reads.

            var change = new FieldChange(AssignedToField, remote.AssignedTo, null);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);

            // Verify by readback that ADO is genuinely cleared. AB#737
            // §Release requires the release to observe an empty
            // AssignedTo before local terminalization runs; a
            // rule-refused clear that leaves a value in place MUST
            // surface here so the release path leaves the row active.
            var verified = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(verified.AssignedTo))
                return Result.Fail($"{ClearReadbackNotEmpty}: observed='{verified.AssignedTo}'");
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdoConflictException)
        {
            return Result.Fail(ConflictAfterRetry);
        }
        catch (Exception ex)
        {
            return Result.Fail(FormatUnderlying(ex));
        }
    }

    /// <summary>
    /// Compare two ADO identity renderings for equivalence. ADO returns
    /// <c>System.AssignedTo</c> as either a display name or a
    /// <c>display &lt;upn&gt;</c>-shaped composite depending on server rules
    /// and identity provider. The claim service supplies a single opaque
    /// string that is either shape; the comparison is:
    /// <list type="bullet">
    ///   <item>Ordinal case-insensitive on the whole string.</item>
    ///   <item>OR ordinal case-insensitive on either side's UPN token
    ///     (the text inside angle brackets or after a space) — so
    ///     <c>Jane &lt;jane@example.com&gt;</c> matches <c>jane@example.com</c>.</item>
    ///   <item>OR ordinal case-insensitive on either side's leading
    ///     display token — so <c>Jane &lt;jane@example.com&gt;</c> matches
    ///     <c>Jane Doe</c> only when <c>Jane</c> matches whole-token, not
    ///     substring. The claim path always supplies the exact holder
    ///     ADO returned on read-before-write, so equivalence is usually
    ///     byte-exact on the whole string.</item>
    /// </list>
    /// </summary>
    internal static bool IdentityMatches(string? observed, string intended)
    {
        if (string.IsNullOrEmpty(observed) || string.IsNullOrEmpty(intended))
            return false;
        var cmp = StringComparer.OrdinalIgnoreCase;
        if (cmp.Equals(observed, intended)) return true;

        // Fall back to UPN-token comparison so a normalized composite still
        // round-trips against a bare upn or a bare display name.
        var obsUpn = ExtractUpn(observed);
        var intUpn = ExtractUpn(intended);
        if (obsUpn is not null && intUpn is not null && cmp.Equals(obsUpn, intUpn))
            return true;
        if (obsUpn is not null && cmp.Equals(obsUpn, intended)) return true;
        if (intUpn is not null && cmp.Equals(intUpn, observed)) return true;

        var obsDisplay = ExtractDisplay(observed);
        var intDisplay = ExtractDisplay(intended);
        if (obsDisplay is not null && intDisplay is not null && cmp.Equals(obsDisplay, intDisplay))
            return true;
        return false;
    }

    private static string? ExtractUpn(string value)
    {
        var lt = value.IndexOf('<');
        var gt = value.IndexOf('>');
        if (lt >= 0 && gt > lt) return value.Substring(lt + 1, gt - lt - 1).Trim();
        if (value.Contains('@')) return value.Trim();
        return null;
    }

    private static string? ExtractDisplay(string value)
    {
        var lt = value.IndexOf('<');
        if (lt <= 0) return null;
        return value[..lt].Trim();
    }

    private static bool TryParseWorkItemId(string primaryScopeId, out int workItemId)
        => int.TryParse(primaryScopeId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0;

    private static string FormatUnderlying(Exception ex)
    {
        var msg = ex.Message?.Replace('\r', ' ').Replace('\n', ' ') ?? "unknown";
        return $"{ex.GetType().Name}: {msg}";
    }
}
