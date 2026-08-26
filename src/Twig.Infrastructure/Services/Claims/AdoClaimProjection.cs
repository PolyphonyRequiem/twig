using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// AB#739 claim projection onto <c>System.AssignedTo</c>, with strict
/// stable-identity readback verification. Every write is followed by a
/// re-fetch, and the resulting <c>uniqueName</c> is byte-compared against
/// <see cref="ClaimHolderDescriptor.Identity"/> — the stable ADO identity
/// (UPN / uniqueName / descriptor) captured by the resolver. A missing
/// uniqueName on read, or one that differs from the intended identity,
/// surfaces as a named projection failure; the projection NEVER accepts
/// "same display / different UPN" as a match.
/// </summary>
internal sealed class AdoClaimProjection : IAdoClaimProjection
{
    internal const string AssignedToField = "System.AssignedTo";

    internal const string InvalidScopeId = "invalid-primary-scope-id";
    internal const string EmptyHolder = "holder-identity-required";
    internal const string ConflictAfterRetry = "ado-conflict-after-retry";
    internal const string ReadbackMissing = "ado-readback-missing";
    internal const string ReadbackMissingUniqueName = "ado-readback-missing-unique-name";
    internal const string ReadbackMismatch = "ado-readback-unique-name-mismatch";
    internal const string ClearReadbackNotEmpty = "ado-clear-readback-not-empty";

    private readonly IAdoWorkItemService _ado;
    private readonly IAdoAssignedIdentityReader _identityReader;

    public AdoClaimProjection(IAdoWorkItemService ado, IAdoAssignedIdentityReader identityReader)
    {
        _ado = ado;
        _identityReader = identityReader;
    }

    public async Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default)
    {
        if (!TryParseWorkItemId(primaryScopeId, out var workItemId))
            return Result.Fail(InvalidScopeId);
        if (string.IsNullOrWhiteSpace(holder.Identity))
            return Result.Fail(EmptyHolder);

        var stableUpn = holder.Identity.Trim();

        try
        {
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            var observedUpn = await _identityReader.ReadAssignedUniqueNameAsync(workItemId, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(observedUpn)
                && string.Equals(observedUpn, stableUpn, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Ok();
            }

            var change = new FieldChange(AssignedToField, remote.AssignedTo, stableUpn);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);

            var verified = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(verified.AssignedTo))
                return Result.Fail(ReadbackMissing);
            var verifiedUpn = await _identityReader.ReadAssignedUniqueNameAsync(workItemId, ct).ConfigureAwait(false);
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
