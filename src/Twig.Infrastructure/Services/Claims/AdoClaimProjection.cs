using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// AB#739 claim projection onto <c>System.AssignedTo</c>, with strict
/// stable-identity readback verification per AB#737 §Mint step 2 and the
/// reviewer's identity-readback blocker: every write is followed by a
/// re-fetch, and the resulting <c>uniqueName</c> is byte-compared against
/// the intended holder's <see cref="ClaimHolderDescriptor.UniqueName"/>.
/// A missing uniqueName on read — or one that differs from the intended
/// — surfaces as a named projection failure; the projection NEVER
/// accepts "same display / different UPN" as a match, and NEVER falls
/// back to display comparison.
/// <para>
/// The seam remains process-agnostic: it treats the primary-scope id as
/// an opaque string that parses into the numeric work-item id, and
/// forwards <c>System.AssignedTo</c> as the sole ADO field written.
/// </para>
/// </summary>
internal sealed class AdoClaimProjection : IAdoClaimProjection
{
    internal const string AssignedToField = "System.AssignedTo";

    internal const string InvalidScopeId = "invalid-primary-scope-id";
    internal const string EmptyHolder = "holder-identity-required";
    internal const string EmptyUniqueName = "holder-unique-name-required";
    internal const string ConflictAfterRetry = "ado-conflict-after-retry";
    internal const string ReadbackMissing = "ado-readback-missing";
    internal const string ReadbackMissingUniqueName = "ado-readback-missing-unique-name";
    internal const string ReadbackMismatch = "ado-readback-unique-name-mismatch";
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
        // AB#739 §Identity readback: refuse to project without a stable
        // uniqueName the readback can byte-compare on.
        if (string.IsNullOrWhiteSpace(holder.UniqueName))
            return Result.Fail(EmptyUniqueName);

        // Write the stable UPN as the ADO field value. ADO's
        // System.AssignedTo accepts a UPN verbatim and reflects it back as
        // the identity object's uniqueName property, which the response
        // mapper preserves in the composite <c>display &lt;upn&gt;</c>
        // rendering used here for extraction.
        var stableUpn = holder.UniqueName!.Trim();

        try
        {
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            var observedUpn = AdoResponseMapper.ExtractAssigneeUniqueName(remote.AssignedTo);

            // Already-matching stable identity — no-op verified read
            // (AB#737 §Reclaim over active).
            if (observedUpn is not null
                && string.Equals(observedUpn, stableUpn, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Ok();
            }

            var change = new FieldChange(AssignedToField, remote.AssignedTo, stableUpn);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);

            // Verify by readback of the stable identity.
            var verified = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(verified.AssignedTo))
                return Result.Fail(ReadbackMissing);
            var verifiedUpn = AdoResponseMapper.ExtractAssigneeUniqueName(verified.AssignedTo);
            if (string.IsNullOrEmpty(verifiedUpn))
                return Result.Fail($"{ReadbackMissingUniqueName}: observed='{verified.AssignedTo}'");
            if (!string.Equals(verifiedUpn, stableUpn, StringComparison.OrdinalIgnoreCase))
                return Result.Fail($"{ReadbackMismatch}: expected='{stableUpn}' observed='{verifiedUpn}'");
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
                return Result.Ok();

            var change = new FieldChange(AssignedToField, remote.AssignedTo, null);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);

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

    private static bool TryParseWorkItemId(string primaryScopeId, out int workItemId)
        => int.TryParse(primaryScopeId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0;

    private static string FormatUnderlying(Exception ex)
    {
        var msg = ex.Message?.Replace('\r', ' ').Replace('\n', ' ') ?? "unknown";
        return $"{ex.GetType().Name}: {msg}";
    }
}
