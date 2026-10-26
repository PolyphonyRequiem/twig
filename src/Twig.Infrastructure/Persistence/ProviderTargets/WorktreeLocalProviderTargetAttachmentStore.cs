using System.Globalization;
using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence.ProviderTargets;

internal sealed class WorktreeLocalProviderTargetAttachmentStore(
    TwigPaths paths,
    IProviderTargetBindingStore bindings,
    TimeProvider clock) : IProviderTargetAttachmentStore, IDisposable
{
    private const string TmpDirName = "tmp";
    private const int LockAcquireMaxAttempts = 400;
    private const int LockAcquireDelayMs = 25;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<Result<VersionedProviderTargetAttachment>> ReadWithRevisionAsync(
        CancellationToken ct = default) =>
        await ReadInternalAsync(ct).ConfigureAwait(false);

    public Task<Result<ProviderTargetAttachmentWriteOutcome>> WriteAsync(
        ProviderTargetBindingId bindingId,
        long expectedRevision,
        CancellationToken ct = default)
    {
        if (bindingId.Value == Guid.Empty)
            return Task.FromResult(Result.Fail<ProviderTargetAttachmentWriteOutcome>(
                ProviderTargetBindingFailures.InvalidBindingId));
        return MutateAsync(bindingId, expectedBindingId: null, expectedRevision, ct);
    }

    public Task<Result<ProviderTargetAttachmentWriteOutcome>> ClearAsync(
        ProviderTargetBindingId expectedBindingId,
        long expectedRevision,
        CancellationToken ct = default)
    {
        if (expectedBindingId.Value == Guid.Empty)
            return Task.FromResult(Result.Fail<ProviderTargetAttachmentWriteOutcome>(
                ProviderTargetBindingFailures.InvalidBindingId));
        return MutateAsync(bindingId: null, expectedBindingId, expectedRevision, ct);
    }

    private async Task<Result<VersionedProviderTargetAttachment>> ReadInternalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(paths.TwigDir))
            return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.WorktreeUnavailable);

        var path = Path.Combine(paths.TwigDir, ProviderTargetAttachmentDocument.FileName);
        ProviderTargetAttachmentDocument? document;
        try
        {
            await using var stream = File.OpenRead(path);
            document = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.ProviderTargetAttachmentDocument, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Result.Ok(new VersionedProviderTargetAttachment(null, 0));
        }
        catch (DirectoryNotFoundException)
        {
            return Result.Ok(new VersionedProviderTargetAttachment(null, 0));
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.InvalidDocument); }
        catch (IOException) { return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.ReadFailed); }
        catch (UnauthorizedAccessException) { return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.ReadFailed); }

        if (!TryReadEnvelope(document, out var envelope))
            return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.InvalidDocument);

        var liveFingerprint = GetLiveFingerprint();
        if (!liveFingerprint.IsSuccess)
            return Result.Fail<VersionedProviderTargetAttachment>(liveFingerprint.Error);
        if (!string.Equals(envelope!.WorktreeFingerprint, liveFingerprint.Value, StringComparison.Ordinal))
            return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.WorktreeFingerprintMismatch);

        if (envelope.BindingId is { } bindingId)
        {
            var binding = await bindings.FindByIdAsync(bindingId, ct).ConfigureAwait(false);
            if (!binding.IsSuccess)
                return Result.Fail<VersionedProviderTargetAttachment>(binding.Error);
            if (binding.Value is null)
                return Result.Fail<VersionedProviderTargetAttachment>(ProviderTargetAttachmentFailures.BindingMissing);
        }

        return Result.Ok(new VersionedProviderTargetAttachment(envelope, envelope.Revision));
    }

    private async Task<Result<ProviderTargetAttachmentWriteOutcome>> MutateAsync(
        ProviderTargetBindingId? bindingId,
        ProviderTargetBindingId? expectedBindingId,
        long expectedRevision,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(paths.TwigDir))
            return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.WorktreeUnavailable);
        if (expectedRevision < 0)
            return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.RevisionMismatch);

        if (bindingId is { } targetId)
        {
            var target = await bindings.FindByIdAsync(targetId, ct).ConfigureAwait(false);
            if (!target.IsSuccess)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(target.Error);
            if (target.Value is null)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.BindingMissing);
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        FileStream? lockHandle = null;
        try
        {
            lockHandle = await AcquireCrossProcessLockAsync(ct).ConfigureAwait(false);
            if (lockHandle is null)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.LockUnavailable);

            var current = await ReadInternalAsync(ct).ConfigureAwait(false);
            if (!current.IsSuccess)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(current.Error);

            var currentEnvelope = current.Value.Envelope;
            if (currentEnvelope is null)
            {
                if (expectedRevision != 0 || bindingId is null)
                    return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.RevisionMismatch);
            }
            else
            {
                if (currentEnvelope.Revision != expectedRevision)
                    return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.RevisionMismatch);
                if (expectedBindingId is { } expectedId
                    && (currentEnvelope.State != ProviderTargetAttachmentState.Attached
                        || currentEnvelope.BindingId != expectedId))
                    return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.BindingMismatch);
            }

            var liveFingerprint = GetLiveFingerprint();
            if (!liveFingerprint.IsSuccess)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(liveFingerprint.Error);

            if (currentEnvelope is null && bindingId is null)
                return Result.Ok(new ProviderTargetAttachmentWriteOutcome(0));

            var nextRevision = currentEnvelope is null ? 1 : currentEnvelope.Revision + 1;
            var nextEnvelope = new ProviderTargetAttachmentEnvelope(
                nextRevision,
                liveFingerprint.Value,
                clock.GetUtcNow(),
                bindingId is null ? ProviderTargetAttachmentState.Detached : ProviderTargetAttachmentState.Attached,
                bindingId);
            var write = await WriteEnvelopeAtomicAsync(nextEnvelope, ct).ConfigureAwait(false);
            if (!write.IsSuccess)
                return Result.Fail<ProviderTargetAttachmentWriteOutcome>(write.Error);
            return Result.Ok(new ProviderTargetAttachmentWriteOutcome(nextRevision));
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.WriteFailed); }
        catch (UnauthorizedAccessException) { return Result.Fail<ProviderTargetAttachmentWriteOutcome>(ProviderTargetAttachmentFailures.WriteFailed); }
        finally
        {
            lockHandle?.Dispose();
            _writeGate.Release();
        }
    }

    private async Task<FileStream?> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.TwigDir);
        var lockPath = Path.Combine(paths.TwigDir, ProviderTargetAttachmentDocument.LockFileName);
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

    private async Task<Result> WriteEnvelopeAtomicAsync(
        ProviderTargetAttachmentEnvelope envelope,
        CancellationToken ct)
    {
        var document = new ProviderTargetAttachmentDocument(
            ProviderTargetAttachmentDocument.CurrentSchema,
            ProviderTargetAttachmentDocument.CurrentVersion,
            envelope.Revision,
            envelope.WorktreeFingerprint,
            envelope.UpdatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            envelope.State == ProviderTargetAttachmentState.Attached
                ? ProviderTargetAttachmentDocument.StateAttached
                : ProviderTargetAttachmentDocument.StateDetached,
            envelope.BindingId?.ToString());
        var targetPath = Path.Combine(paths.TwigDir, ProviderTargetAttachmentDocument.FileName);
        Directory.CreateDirectory(paths.TwigDir);
        var tmpDir = Path.Combine(paths.TwigDir, TmpDirName);
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, $"{ProviderTargetAttachmentDocument.FileName}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(
                    stream, document, TwigJsonContext.Default.ProviderTargetAttachmentDocument, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, targetPath, overwrite: true);
            return Result.Ok();
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private Result<string> GetLiveFingerprint()
    {
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out _))
            return Result.Fail<string>(ProviderTargetAttachmentFailures.WorktreeUnavailable);
        return Result.Ok(WorktreeFingerprintProvider.CanonicalJson(anchor));
    }

    private static bool TryReadEnvelope(
        ProviderTargetAttachmentDocument? document,
        out ProviderTargetAttachmentEnvelope? envelope)
    {
        envelope = null;
        if (document is null
            || !string.Equals(document.Schema, ProviderTargetAttachmentDocument.CurrentSchema, StringComparison.Ordinal)
            || document.Version != ProviderTargetAttachmentDocument.CurrentVersion
            || document.Revision < 1
            || string.IsNullOrWhiteSpace(document.WorktreeFingerprint)
            || !DateTimeOffset.TryParse(document.UpdatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updatedAt))
            return false;

        ProviderTargetAttachmentState state;
        ProviderTargetBindingId? bindingId;
        switch (document.State)
        {
            case ProviderTargetAttachmentDocument.StateAttached:
                if (!ProviderTargetBindingId.TryParse(document.BindingId, out var parsedId))
                    return false;
                state = ProviderTargetAttachmentState.Attached;
                bindingId = parsedId;
                break;
            case ProviderTargetAttachmentDocument.StateDetached:
                if (document.BindingId is not null)
                    return false;
                state = ProviderTargetAttachmentState.Detached;
                bindingId = null;
                break;
            default:
                return false;
        }

        envelope = new ProviderTargetAttachmentEnvelope(
            document.Revision,
            document.WorktreeFingerprint,
            updatedAt,
            state,
            bindingId);
        return true;
    }

    public void Dispose() => _writeGate.Dispose();
}
