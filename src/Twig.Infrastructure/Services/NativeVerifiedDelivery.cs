using System.Collections.Immutable;
using Twig.Core;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal static class NativeVerifiedDelivery
{
    internal static bool Verified(StoreSnapshot snapshot, Proposal proposal)
        => snapshot.Proposals.Any(current => current.Digest == proposal.Digest)
            && proposal.Operations.Select((_, ordinal) => snapshot.Journal.Any(row =>
                row.Digest == proposal.Digest && row.Operation == ordinal && row.Status == OpStatus.Verified)).All(value => value);

    internal static async Task<Result<ImmutableArray<Selector>>> ReadbackAsync(StoreSnapshot snapshot,
        Proposal proposal, ImmutableArray<Op> verified, CoreWorkspace origin, CancellationToken ct)
    {
        var pins = ImmutableArray.CreateBuilder<Selector>();
        foreach (var op in verified)
        {
            var ordinal = proposal.Operations.IndexOf(op);
            if (ordinal < 0 || !snapshot.Proposals.Any(value => value.Digest == proposal.Digest)
                || !snapshot.Journal.Any(row => row.Digest == proposal.Digest && row.Operation == ordinal
                    && row.Status == OpStatus.Verified))
                return Result<ImmutableArray<Selector>>.Fail(ErrorCode.Indeterminate, "Only original Verified journal operations can be delivered.");
            var key = op.Item;
            if (key.IsLocal)
            {
                var mapping = snapshot.Mappings.SingleOrDefault(value => value.Local == key && value.ProposalDigest == proposal.Digest);
                if (mapping is null)
                    return Result<ImmutableArray<Selector>>.Fail(ErrorCode.Indeterminate, "The attributable Verified seed mapping is unavailable.");
                key = mapping.Remote;
                pins.Add(new(SelectorKind.Pin, key, connection: proposal.Connection));
            }
            var read = await origin.ReadAsync(key, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<ImmutableArray<Selector>>.Fail(read.Error);
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
            : await user.SaveAsync(new(bench.Id, bench.Name, bench.Selectors.Concat(additions)), bench.Revision, ct).ConfigureAwait(false);
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
