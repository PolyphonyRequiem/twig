using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// The §8.4 storage seam AB#745 owns. Worktree-local
/// <c>.twig/transport.json</c> read/write plus the two host-mutation
/// tombstone paths.
/// <para>
/// The surface follows contract §8.4 verbatim:
/// <list type="bullet">
///   <item><see cref="ReadWithRevisionAsync"/> returns the envelope and
///     the current CAS revision (0 when the file does not exist).</item>
///   <item><see cref="WriteAsync"/> mutates from
///     <c>state = "detached"</c> or from a never-existent file into
///     <c>state = "attached"</c>, or replaces one attached record with
///     another; either way <c>revision</c> increments by 1.</item>
///   <item><see cref="DetachAsync"/> and <see cref="CloseAsync"/> both
///     write a <c>state = "detached"</c> tombstone with
///     <c>revision + 1</c>; on a never-existent file both are no-ops
///     that still assert <c>expectedRevision = 0</c> and return
///     <c>writtenRevision = 0</c>.</item>
/// </list>
/// Every returned <c>writtenRevision</c> is the new CAS token for the
/// next call. Every failure carries a §11 identifier string, never an
/// exception (§8.4 Result convention).
/// </para>
/// </summary>
internal interface ITransportAttachmentStore
{
    /// <summary>Read the envelope and the current CAS revision. When
    /// <c>transport.json</c> does not exist, returns
    /// <c>envelope = null, revision = 0</c> per §8.2 "never-attached".
    /// </summary>
    Task<Result<VersionedTransportEnvelope>> ReadWithRevisionAsync(CancellationToken ct = default);

    /// <summary>CAS write of a new attached record. Increments the
    /// envelope revision by 1. <paramref name="expectedRevision"/> = 0
    /// asserts the file is absent (§8.4).</summary>
    Task<Result<TransportWriteOutcome>> WriteAsync(
        TransportAttachmentRecord newRecord,
        long expectedRevision,
        CancellationToken ct = default);

    /// <summary>CAS-write a §8.2 detach tombstone. Detach is idempotent
    /// (§6.1): the envelope's <c>revision</c> advances even under
    /// adapter failure so a subsequent reattach cannot ABA-collide.
    /// </summary>
    Task<Result<TransportWriteOutcome>> DetachAsync(
        long expectedRevision,
        CancellationToken ct = default);

    /// <summary>CAS-write a §8.2 detach tombstone as the write-side
    /// half of a §6.2 close. Callers explicitly invoke this after the
    /// adapter's close verb succeeded — §1.1(c) forbids any implicit
    /// reach from a probe/read/detach/validator/render path.</summary>
    Task<Result<TransportWriteOutcome>> CloseAsync(
        long expectedRevision,
        CancellationToken ct = default);
}
