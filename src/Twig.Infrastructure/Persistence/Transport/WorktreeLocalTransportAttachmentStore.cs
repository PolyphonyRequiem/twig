using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Worktree-local implementation of <see cref="ITransportAttachmentStore"/>
/// per contract §8.1–§8.4. The <c>.twig/transport.json</c> envelope
/// lives beside the AB#736 §4.2 attachment tier; layout, atomic-write,
/// and cross-process locking discipline mirror
/// <see cref="WorktreeLocalAttachmentStore"/> so an operator cannot see
/// two "attachment stores" behave differently on the same host.
/// <para>
/// Every mutation runs the sequence: acquire the exclusive
/// <c>transport.json.lock</c> file via <see cref="FileStream"/> with
/// <see cref="FileShare.None"/>, re-read the envelope under the lock,
/// compare the caller's <c>expectedRevision</c> against the on-disk
/// value, run the mutation, bump revision + write via temp-file
/// rename, then release the lock (§8.4).
/// </para>
/// </summary>
internal sealed class WorktreeLocalTransportAttachmentStore : ITransportAttachmentStore, System.IDisposable
{
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    internal const string TransportFileName = "transport.json";
    internal const string TransportLockFileName = "transport.json.lock";
    internal const string TmpDirName = "tmp";

    private const int LockAcquireMaxAttempts = 400;
    private const int LockAcquireDelayMs = 25;

    public WorktreeLocalTransportAttachmentStore(TwigPaths paths, TwigConfiguration config, TimeProvider clock)
    {
        _paths = paths;
        _config = config;
        _clock = clock;
    }

    public async Task<Result<VersionedTransportEnvelope>> ReadWithRevisionAsync(CancellationToken ct = default)
    {
        var res = await ReadInternalAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccess)
            return Result.Fail<VersionedTransportEnvelope>(res.Error);
        return Result.Ok(res.Value);
    }

    public Task<Result<TransportWriteOutcome>> WriteAsync(
        TransportAttachmentRecord newRecord,
        long expectedRevision,
        CancellationToken ct = default)
    {
        // §2.2 shape validator on the write boundary. A malformed
        // record never reaches disk — the caller receives the same
        // named identifier a downstream read would surface.
        var shapeResult = TransportShapeValidator.ValidateRecord(newRecord);
        if (!shapeResult.IsSuccess)
            return Task.FromResult(Result.Fail<TransportWriteOutcome>(shapeResult.Error));

        // §2.1 / §8.4 — the incoming record's worktreeFingerprint MUST
        // byte-equal the live tuple. Otherwise a first attach persists
        // a wrong fingerprint that only surfaces on the NEXT read.
        // The read-boundary check catches replaced-record drift; this
        // write-boundary check catches the first-attach case the read
        // never sees.
        if (newRecord.Worktree is { } incoming)
        {
            if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out _))
                return Task.FromResult(Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.WorktreeFingerprintMismatch));
            var liveFingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
            if (!string.Equals(incoming.WorktreeFingerprint, liveFingerprint, System.StringComparison.Ordinal))
                return Task.FromResult(Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.WorktreeFingerprintMismatch));
        }

        return MutateAsync(
            expectedRevision: expectedRevision,
            targetState: TransportAttachmentEnvelopeState.Attached,
            newRecord: newRecord,
            ct: ct);
    }

    public Task<Result<TransportWriteOutcome>> DetachAsync(
        long expectedRevision,
        CancellationToken ct = default)
        => MutateAsync(
            expectedRevision: expectedRevision,
            targetState: TransportAttachmentEnvelopeState.Detached,
            newRecord: null,
            ct: ct);

    public Task<Result<TransportWriteOutcome>> CloseAsync(
        long expectedRevision,
        CancellationToken ct = default)
        => MutateAsync(
            expectedRevision: expectedRevision,
            targetState: TransportAttachmentEnvelopeState.Detached,
            newRecord: null,
            ct: ct);

    // ── Read path ─────────────────────────────────────────────────────

    private async Task<Result<VersionedTransportEnvelope>> ReadInternalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(_paths.TwigDir))
            return Result.Ok(new VersionedTransportEnvelope(null, 0));

        var transportPath = Path.Combine(_paths.TwigDir, TransportFileName);
        if (!File.Exists(transportPath))
        {
            // §8.2 — never-attached; caller MUST NOT synthesize.
            return Result.Ok(new VersionedTransportEnvelope(null, 0));
        }

        TransportAttachmentDocument? doc;
        try
        {
            await using var stream = File.OpenRead(transportPath);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.TransportAttachmentDocument, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) { throw; }
        catch (JsonException) { return Result.Fail<VersionedTransportEnvelope>(TransportAttachmentFailure.RecordInvalid); }
        catch (IOException ex) { return Result.Fail<VersionedTransportEnvelope>($"{TransportAttachmentFailure.AtomicWriteFailed}: {ex.Message}"); }

        if (doc is null)
            return Result.Fail<VersionedTransportEnvelope>(TransportAttachmentFailure.RecordInvalid);

        var envelopeResult = TransportEnvelopeMapper.FromDocument(doc);
        if (!envelopeResult.IsSuccess)
            return Result.Fail<VersionedTransportEnvelope>(envelopeResult.Error);
        var envelope = envelopeResult.Value;

        // §8.4 — connectionRef equality against the live twig.json ref.
        var expectedConnectionRef = ConnectionRefResolver.Compute(_config);
        if (!string.Equals(envelope.ConnectionRef, expectedConnectionRef, System.StringComparison.Ordinal))
            return Result.Fail<VersionedTransportEnvelope>(TransportAttachmentFailure.ConnectionMismatch);

        // §2.2 shape validator on the read boundary.
        var shapeResult = TransportShapeValidator.Validate(envelope);
        if (!shapeResult.IsSuccess)
            return Result.Fail<VersionedTransportEnvelope>(shapeResult.Error);

        // §2.1 worktree fingerprint byte-equality against the live tuple.
        if (envelope.Record?.Worktree is { } worktree)
        {
            if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out _))
                return Result.Fail<VersionedTransportEnvelope>(TransportAttachmentFailure.WorktreeFingerprintMismatch);
            var liveFingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
            if (!string.Equals(worktree.WorktreeFingerprint, liveFingerprint, System.StringComparison.Ordinal))
                return Result.Fail<VersionedTransportEnvelope>(TransportAttachmentFailure.WorktreeFingerprintMismatch);
        }

        return Result.Ok(new VersionedTransportEnvelope(envelope, envelope.Revision));
    }

    // ── Mutate path ───────────────────────────────────────────────────

    private async Task<Result<TransportWriteOutcome>> MutateAsync(
        long expectedRevision,
        TransportAttachmentEnvelopeState targetState,
        TransportAttachmentRecord? newRecord,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(_paths.TwigDir))
            return Result.Fail<TransportWriteOutcome>($"{TransportAttachmentFailure.AtomicWriteFailed}: TwigDir not configured.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        FileStream? lockHandle = null;
        try
        {
            lockHandle = await AcquireCrossProcessLockAsync(ct).ConfigureAwait(false);
            if (lockHandle is null)
                return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.VersionMismatch);

            var readRes = await ReadInternalAsync(ct).ConfigureAwait(false);
            if (!readRes.IsSuccess)
            {
                // §8.2 — a never-attached file with a matching
                // expectedRevision = 0 detach/close is a documented
                // no-op. Every other read failure surfaces verbatim.
                return Result.Fail<TransportWriteOutcome>(readRes.Error);
            }

            var current = readRes.Value;
            if (current.Envelope is null)
            {
                // Never-existent file path.
                if (expectedRevision != 0)
                    return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.VersionMismatch);
                if (targetState == TransportAttachmentEnvelopeState.Detached)
                {
                    // §8.4 — detach/close on a never-existent file is a
                    // no-op that still asserts expectedRevision = 0 and
                    // returns writtenRevision = 0.
                    return Result.Ok(new TransportWriteOutcome(0));
                }
                // First attach: bump revision from 0 to 1.
                var firstEnvelope = BuildEnvelope(
                    revision: 1,
                    state: TransportAttachmentEnvelopeState.Attached,
                    record: newRecord);
                var firstWrite = await WriteEnvelopeAtomicAsync(firstEnvelope, ct).ConfigureAwait(false);
                if (!firstWrite.IsSuccess)
                    return Result.Fail<TransportWriteOutcome>(firstWrite.Error);
                return Result.Ok(new TransportWriteOutcome(1));
            }

            if (current.Envelope.Revision != expectedRevision)
                return Result.Fail<TransportWriteOutcome>(TransportAttachmentFailure.VersionMismatch);

            var nextRevision = current.Envelope.Revision + 1;
            var nextEnvelope = BuildEnvelope(
                revision: nextRevision,
                state: targetState,
                record: newRecord);
            var write = await WriteEnvelopeAtomicAsync(nextEnvelope, ct).ConfigureAwait(false);
            if (!write.IsSuccess)
                return Result.Fail<TransportWriteOutcome>(write.Error);
            return Result.Ok(new TransportWriteOutcome(nextRevision));
        }
        finally
        {
            lockHandle?.Dispose();
            _writeGate.Release();
        }
    }

    private TransportAttachmentEnvelope BuildEnvelope(
        long revision,
        TransportAttachmentEnvelopeState state,
        TransportAttachmentRecord? record)
    {
        return new TransportAttachmentEnvelope(
            Revision: revision,
            ConnectionRef: ConnectionRefResolver.Compute(_config),
            RecordedAt: _clock.GetUtcNow(),
            State: state,
            Record: state == TransportAttachmentEnvelopeState.Attached ? record : null);
    }

    /// <summary>Acquires an exclusive OS-visible lock via
    /// <c>transport.json.lock</c>. Same discipline as
    /// <see cref="WorktreeLocalAttachmentStore"/>: a peer's open attempt
    /// raises <see cref="IOException"/>, which is the cross-process
    /// serialization signal. A long-outstanding lock returns null →
    /// caller surfaces as
    /// <see cref="TransportAttachmentFailure.VersionMismatch"/>.</summary>
    private async Task<FileStream?> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.TwigDir);
        var lockPath = Path.Combine(_paths.TwigDir, TransportLockFileName);
        for (var attempt = 0; attempt < LockAcquireMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(LockAcquireDelayMs, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<Result> WriteEnvelopeAtomicAsync(TransportAttachmentEnvelope envelope, CancellationToken ct)
    {
        var doc = TransportEnvelopeMapper.ToDocument(envelope);
        var transportPath = Path.Combine(_paths.TwigDir, TransportFileName);
        try
        {
            await WriteJsonAtomicAsync(transportPath, doc, TwigJsonContext.Default.TransportAttachmentDocument, ct)
                .ConfigureAwait(false);
            return Result.Ok();
        }
        catch (IOException ex) { return Result.Fail($"{TransportAttachmentFailure.AtomicWriteFailed}: {ex.Message}"); }
        catch (System.UnauthorizedAccessException ex) { return Result.Fail($"{TransportAttachmentFailure.AtomicWriteFailed}: {ex.Message}"); }
    }

    private async Task WriteJsonAtomicAsync<T>(
        string targetPath,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.TwigDir);
        var tmpDir = Path.Combine(_paths.TwigDir, TmpDirName);
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, $"{Path.GetFileName(targetPath)}.{System.Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough | FileOptions.Asynchronous,
                BufferSize = 4096,
            };
            await using (var stream = new FileStream(tmpPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort */ }
            throw;
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
