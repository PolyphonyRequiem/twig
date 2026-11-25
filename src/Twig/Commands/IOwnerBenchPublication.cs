using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Commands;

internal interface IOwnerBenchPublication
{
    IDisposable TrackWorkspace(string root, NativeSourceStamp source, UserBenchStore definitions);
    Task<Result<NativePlanOutcome>> ContinuePlanAsync(string root, NativeSourceStamp origin, UserBenchStore definitions,
        NativePlanReview review, CancellationToken ct);
    Task<Result<QueryPage>> Observe(string root, NativeSourceStamp source, UserBenchStore definitions, ItemKey key,
        Func<Task<Result<QueryPage>>> read, bool publish, CancellationToken ct);
    void InvalidateItem(Connection connection, ItemKey key);
}
