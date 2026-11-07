using System.Collections.Immutable;
using Twig.Core;

namespace Twig.Infrastructure.Services;

public sealed record NativePlanSource(Guid Id, Guid StoreInstance, string OriginRoot, NativeSourceStamp Source, string State);
public sealed record NativePlanStatus(Guid UserInstance, Guid BenchId, long BenchRevision,
    Guid PlanId, long Revision, bool Enabled, ImmutableArray<NativePlanSource> Sources)
{
    public string? LastReviewDigest { get; init; }
}

public sealed partial class UserBenchStore
{
    public async Task<Result<NativePlanStatus>> ReadPlanStatusAsync(Guid benchId, CancellationToken ct = default)
    {
        var library = await ReadAsync(ct).ConfigureAwait(false);
        if (!library.IsSuccess) return Result<NativePlanStatus>.Fail(library.Error);
        var bench = library.Value.Benches.SingleOrDefault(value => value.Id == benchId);
        if (bench is null) return Result<NativePlanStatus>.Fail(ErrorCode.NotFound, "The named Bench no longer exists.");
        var plan = await ReadPlanAsync(benchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<NativePlanStatus>.Fail(plan.Error);
        var review = await LatestPlanReviewDigestAsync(plan.Value.Id, ct).ConfigureAwait(false);
        return review.IsSuccess ? Result<NativePlanStatus>.Ok(Status(library.Value.InstanceId, bench.Revision, plan.Value)
            with { LastReviewDigest = review.Value.Digest }) : Result<NativePlanStatus>.Fail(review.Error);
    }

    public async Task<Result<NativePlanStatus>> ChangePlanModeAsync(Guid expectedInstance, Guid benchId,
        long expectedBenchRevision, long expectedPlanRevision, bool enabled, CancellationToken ct = default)
    {
        var plan = await SetPlanModeAsync(expectedInstance, benchId, expectedBenchRevision,
            expectedPlanRevision, enabled, ct).ConfigureAwait(false);
        return plan.IsSuccess ? Result<NativePlanStatus>.Ok(Status(expectedInstance, expectedBenchRevision, plan.Value))
            : Result<NativePlanStatus>.Fail(plan.Error);
    }

    public async Task<Result<ImmutableArray<Draft>>> ReadPlanDraftsAsync(Guid expectedInstance, Guid benchId,
        long expectedPlanRevision, Guid scopeId, string originRoot, NativeSourceStamp source, CancellationToken ct = default)
    {
        var plan = await ReadPlanAsync(benchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<ImmutableArray<Draft>>.Fail(plan.Error);
        if (!plan.Value.PlanMode)
            return Result<ImmutableArray<Draft>>.Fail(ErrorCode.Conflict, "Reopen the exact Plan mode before displaying its staged intent.");
        var opened = await OpenPlanScopeAsync(expectedInstance, benchId, expectedPlanRevision, scopeId, originRoot, source, ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<ImmutableArray<Draft>>.Fail(opened.Error);
        var snapshot = await opened.Value.ReadAsync(source.Connection, ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<ImmutableArray<Draft>>.Fail(snapshot.Error);
        foreach (var seed in snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal))
        {
            var owner = await ReadPlanSeedOwnerAsync(seed.Key.LocalId, ct).ConfigureAwait(false);
            if (!owner.IsSuccess) return Result<ImmutableArray<Draft>>.Fail(owner.Error);
            if (owner.Value.BenchId != benchId || owner.Value.PlanId != plan.Value.Id || owner.Value.ScopeId != scopeId)
                return Result<ImmutableArray<Draft>>.Fail(ErrorCode.Conflict, "A local seed belongs to another exact Plan scope.");
        }
        return Result<ImmutableArray<Draft>>.Ok(snapshot.Value.Drafts);
    }

    private static NativePlanStatus Status(Guid instance, long benchRevision, NativeBenchPlan plan)
        => new(instance, plan.BenchId, benchRevision, plan.Id, plan.Revision, plan.PlanMode,
            plan.Scopes.Where(scope => scope.State != NativePlanScopeState.Superseded).Select(scope =>
                new NativePlanSource(scope.Id, scope.StoreInstance, scope.OriginRoot, scope.Source, scope.State.ToString())).ToImmutableArray());
}
