using Twig.Core;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

internal sealed partial class CoreWorkspace
{
    // Called only with the original routine quiescent and the execution owner lease held.
    // This path cannot import, call Apply, or open a dispatch barrier.
    internal async Task<Result<string>> InspectUpdateAsync(OriginalUpdate original, CancellationToken ct)
    {
        if (original.Plan.Connection != connection || executionRegistryPath is null || provider is not IOriginalUpdateProvider qualified)
            return Result<string>.Fail(ErrorCode.Conflict, "The original source is not available.");
        var read = await SnapshotAsync(ct);
        if (!read.IsSuccess) return Result<string>.Fail(read.Error);
        using var registry = new SqliteSystemWorktreeRegistry(executionRegistryPath, TimeProvider.System);
        var gate = await registry.ReadCoreGateAsync(ct);
        if (!gate.IsSuccess || !gate.Value.Active || !gate.Value.Sources.Any(s => s.Instance == storeInstance && s.Connection == connection))
            return Result<string>.Fail(ErrorCode.Indeterminate, "Original registry authority is unavailable.");
        var refs = await registry.CoreAttemptReferencesAsync(storeInstance, ct);
        if (!refs.IsSuccess || !NativeCoreExecution.ReferencesMatch(read.Value, refs.Value))
            return Result<string>.Fail(ErrorCode.Indeterminate, "Original attempt inventory is inconsistent.");
        var plan = original.Plan;
        var references = refs.Value.Where(r => r.Digest == plan.Digest || r.Attempt == original.Attempt).ToArray();
        if (references.Any(r => r.Attempt != original.Attempt || r.Digest != plan.Digest || r.Operation != 0))
            return Result<string>.Fail(ErrorCode.Indeterminate, "Contradictory original attempt references.");
        var row = read.Value.Journal.SingleOrDefault(r => r.Digest == plan.Digest && r.Operation == 0);
        if (original.Identity.State == "Claimed")
        {
            var absent = await qualified.CheckClosedAsync(plan, original.Attempt, ct);
            if (!absent.IsSuccess || !absent.Value)
                return Result<string>.Fail(ErrorCode.Indeterminate, "Closed barrier lacks consistent original authority evidence.");
            if (row is null || row.Status == OpStatus.Planned) return Result<string>.Ok("NotStarted");
            if (references.Length == 0 || row.Attempt != original.Attempt || row.Status is not (OpStatus.Applying or OpStatus.Failed))
                return Result<string>.Fail(ErrorCode.Indeterminate, "Closed barrier conflicts with original journal.");
            if (row.Status == OpStatus.Applying)
            {
                var result = await qualified.InspectOriginalAsync(plan, original.Attempt, null, ct);
                if (!result.IsSuccess || result.Value.Status != OpStatus.Failed)
                    return Result<string>.Fail(ErrorCode.Indeterminate, "Original closed-barrier proof could not be recorded.");
            }
            else if (!row.Receipts[^1].Evidence.Any(s => s is "original:Closed" or "authority:RejectedNoEffect"))
                return Result<string>.Fail(ErrorCode.Indeterminate, "Closed barrier has a contradictory original receipt.");
            return Result<string>.Ok("NotStarted");
        }
        if (references.Length == 0 || row is null || row.Attempt != original.Attempt)
            return Result<string>.Fail(ErrorCode.Indeterminate, "Original operation and attempt could not be correlated.");
        var receipt = await qualified.InspectOriginalAsync(plan, original.Attempt, original.Dispatch, ct);
        if (!receipt.IsSuccess) return Result<string>.Fail(receipt.Error);
        if (receipt.Value.Status == OpStatus.Applied)
        {
            if (original.Identity.State != "Settling") return Result<string>.Ok("Settling");
            receipt = await qualified.VerifyOriginalAsync(plan, receipt.Value, ct);
            if (!receipt.IsSuccess) return Result<string>.Fail(receipt.Error);
        }
        return Result<string>.Ok(receipt.Value.Status switch {
            OpStatus.Verified => original.Identity.State == "Settling" ? "Complete" : "Settling",
            OpStatus.Partial => original.Identity.State == "Settling" ? "Partial" : "Settling",
            OpStatus.Failed => "RejectedNoEffect",
            _ => original.Identity.State == "Settling" ? "Settling" : "ObservationNeeded"
        });
    }
}
