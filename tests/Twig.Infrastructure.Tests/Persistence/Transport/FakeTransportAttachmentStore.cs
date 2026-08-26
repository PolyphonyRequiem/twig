using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;
using Twig.Infrastructure.Persistence.Transport;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// In-memory <see cref="ITransportAttachmentStore"/> for dispatcher /
/// renderer tests. Records every Detach/Close call so tests can assert
/// the tombstone rules per §6.1/§6.2 without touching real disk.
/// </summary>
internal sealed class FakeTransportAttachmentStore : ITransportAttachmentStore
{
    public sealed record DetachCall(long ExpectedRevision);
    public sealed record CloseCall(long ExpectedRevision);

    public List<DetachCall> DetachCalls { get; } = new();
    public List<CloseCall> CloseCalls { get; } = new();
    public Result<TransportWriteOutcome>? DetachNextResult { get; set; }
    public Result<TransportWriteOutcome>? CloseNextResult { get; set; }
    public long NextRevision { get; set; } = 1;

    public Task<Result<VersionedTransportEnvelope>> ReadWithRevisionAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Ok(new VersionedTransportEnvelope(null, 0)));

    public Task<Result<TransportWriteOutcome>> WriteAsync(
        TransportAttachmentRecord newRecord,
        long expectedRevision,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Ok(new TransportWriteOutcome(NextRevision++)));

    public Task<Result<TransportWriteOutcome>> DetachAsync(long expectedRevision, CancellationToken ct = default)
    {
        DetachCalls.Add(new DetachCall(expectedRevision));
        return Task.FromResult(DetachNextResult ?? Result.Ok(new TransportWriteOutcome(NextRevision++)));
    }

    public Task<Result<TransportWriteOutcome>> CloseAsync(long expectedRevision, CancellationToken ct = default)
    {
        CloseCalls.Add(new CloseCall(expectedRevision));
        return Task.FromResult(CloseNextResult ?? Result.Ok(new TransportWriteOutcome(NextRevision++)));
    }
}
