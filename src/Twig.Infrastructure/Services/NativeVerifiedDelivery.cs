using System.Collections.Immutable;
using Twig.Core;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal static class NativeVerifiedDelivery
{
    internal static bool Verified(StoreSnapshot snapshot, Plan plan)
        => CoreWorkspace.VerifiedPlanOperations(snapshot, plan).Length == plan.Operations.Length;

    internal static async Task<Result<ImmutableArray<Selector>>> ReadbackAsync(
        ImmutableArray<CoreVerifiedDeliveryTarget> targets, CoreWorkspace origin, CancellationToken ct)
    {
        var pins = ImmutableArray.CreateBuilder<Selector>();
        foreach (var target in targets)
        {
            var read = await origin.ReadAsync(target.Key, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<ImmutableArray<Selector>>.Fail(read.Error);
            if (target.PublishedSeed)
                pins.Add(new(SelectorKind.Pin, target.Key, connection: origin.Connection));
        }
        return Result<ImmutableArray<Selector>>.Ok(pins.ToImmutable());
    }

    internal static async Task<Result<Bench>> AddPinsAsync(UserBenchStore user, Guid userInstance, Guid benchId,
        IEnumerable<Selector> pins, CancellationToken ct)
    {
        var library = await user.ReadAsync(ct).ConfigureAwait(false);
        if (!library.IsSuccess) return Result<Bench>.Fail(library.Error);
        var bench = library.Value.Benches.SingleOrDefault(value => value.Id == benchId);
        if (library.Value.InstanceId != userInstance || bench is null)
            return Result<Bench>.Fail(ErrorCode.Conflict, "The original Bench was replaced or deleted; verified delivery is pending.");
        var known = bench.Selectors.Select(selector => BenchCodec.SerializeSelectors([selector])).ToHashSet(StringComparer.Ordinal);
        var additions = pins.Where(pin => known.Add(BenchCodec.SerializeSelectors([pin]))).ToArray();
        return additions.Length == 0 ? Result<Bench>.Ok(bench)
            : await user.SaveAsync(new(bench.Id, bench.Name, bench.Selectors.Concat(additions), connections:
                bench.Connections.Union(additions.Select(selector => selector.Connection!))), bench.Revision, ct).ConfigureAwait(false);
    }

    internal static async Task<Result<bool>> SettleCapturesAsync(UserBenchStore user, Guid benchId,
        long benchRevision, ImmutableArray<NativeEditorCapture> delivered, CancellationToken ct)
    {
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<bool>.Fail(captures.Error);
        var plan = await user.ReadPlanAsync(benchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<bool>.Fail(plan.Error);
        foreach (var capture in delivered)
        {
            var current = captures.Value.SingleOrDefault(value => value.Id == capture.Id);
            if (current is null || !current.Settled && !current.SameAs(capture))
                return Result<bool>.Fail(ErrorCode.Conflict, "Original verified effects cannot consume changed captured intent.");
            if (current.Settled) continue;
            var settled = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
                BenchRevision = benchRevision, PlanRevision = plan.Value.Revision, Settled = true }, capture.Revision, ct).ConfigureAwait(false);
            if (!settled.IsSuccess) return Result<bool>.Fail(settled.Error);
        }
        return Result<bool>.Ok(true);
    }
}
