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
/// other Twig mutation uses. Failures translate to short opaque strings so
/// the claim service maps them to the named lifecycle outcomes without leaking
/// stack traces.
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

    private readonly IAdoWorkItemService _ado;

    public AdoClaimProjection(IAdoWorkItemService ado)
    {
        _ado = ado;
    }

    public async Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default)
    {
        if (!TryParseWorkItemId(primaryScopeId, out var workItemId))
            return Result.Fail("primaryScopeId is not a valid ADO work-item id.");
        if (string.IsNullOrWhiteSpace(holder.Identity))
            return Result.Fail("holder identity is required.");
        try
        {
            // Read → patch under optimistic concurrency. ADO's AssignedTo
            // accepts either a display name or a UPN-style identity; the
            // resolver supplies whichever the connection uses.
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            var change = new FieldChange(AssignedToField, remote.AssignedTo, holder.DisplayName ?? holder.Identity);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdoConflictException)
        {
            return Result.Fail("ado-conflict-after-retry");
        }
        catch (Exception ex)
        {
            return Result.Fail(FormatUnderlying(ex));
        }
    }

    public async Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default)
    {
        if (!TryParseWorkItemId(primaryScopeId, out var workItemId))
            return Result.Fail("primaryScopeId is not a valid ADO work-item id.");
        try
        {
            var remote = await _ado.FetchAsync(workItemId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(remote.AssignedTo))
                return Result.Ok(); // Already cleared; the design permits no-op-verified reads.
            // Clearing System.AssignedTo passes a null NewValue; the plan
            // canonicalizer converts null to a remove-operation.
            var change = new FieldChange(AssignedToField, remote.AssignedTo, null);
            await ConflictRetryHelper.PatchWithRetryAsync(_ado, workItemId, [change], remote.Revision, ct).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdoConflictException)
        {
            return Result.Fail("ado-conflict-after-retry");
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
        // Keep the type name + message so the caller sees enough to diagnose;
        // strip the newline/stack trace so the message flows into a single
        // human-readable status line.
        var msg = ex.Message?.Replace('\r', ' ').Replace('\n', ' ') ?? "unknown";
        return $"{ex.GetType().Name}: {msg}";
    }
}
