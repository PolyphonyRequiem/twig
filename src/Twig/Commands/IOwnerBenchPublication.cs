using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Commands;

internal interface IOwnerBenchPublication
{
    IDisposable TrackWorkspace(string root, NativeSourceStamp source, UserBenchStore definitions);
    Task<Result<QueryPage>> Observe(string root, NativeSourceStamp source, UserBenchStore definitions, ItemKey key,
        Func<Task<Result<QueryPage>>> read, bool publish, CancellationToken ct);
    void InvalidateItem(Connection connection, ItemKey key);
}
