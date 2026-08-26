using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Worktree-local implementation of <see cref="IPrimaryScopeAttachmentStore"/>.
/// AB#736 §9.3 seam that AB#738 owns and AB#739 extends with a
/// cross-process CAS handshake through an OS-visible file lock plus a
/// monotonic revision counter.
/// <para>
/// Every mutating operation runs the sequence: acquire the exclusive
/// <c>attachment.json.lock</c> file via <see cref="FileStream"/> with
/// <see cref="FileShare.None"/>, read the current document (validating
/// the layout / worktree / connectionRef ordering §6.4), compare the
/// caller's expected revision against the on-disk revision, run the
/// mutation, bump revision + write via temp-file rename, then release
/// the lock. Because <see cref="FileShare.None"/> yields an
/// <see cref="IOException"/> on any peer's open attempt, this
/// serializes writers across processes on the same worktree checkout —
/// the in-process semaphore alone was not sufficient.
/// </para>
/// </summary>
internal sealed class WorktreeLocalAttachmentStore : IPrimaryScopeAttachmentStore, IDisposable
{
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    internal const string LayoutFileName = "layout.json";
    internal const string WorktreeFileName = "worktree.json";
    internal const string AttachmentFileName = "attachment.json";
    internal const string AttachmentLockFileName = "attachment.json.lock";
    internal const string TmpDirName = "tmp";

    private const int LockAcquireMaxAttempts = 400;
    private const int LockAcquireDelayMs = 25;

    public WorktreeLocalAttachmentStore(TwigPaths paths, TwigConfiguration config, TimeProvider clock)
    {
        _paths = paths;
        _config = config;
        _clock = clock;
    }

    public bool IsManagedWorktree()
    {
        if (string.IsNullOrEmpty(_paths.TwigDir))
            return false;
        if (Directory.Exists(_paths.TwigDir))
            return true;
        return !string.IsNullOrEmpty(_paths.RepoConfigPath) && File.Exists(_paths.RepoConfigPath);
    }

    public async Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default)
    {
        var res = await ReadInternalAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccess)
            return Result.Fail<PrimaryScopeAttachment>(res.Error);
        return Result.Ok(res.Value.Projection);
    }

    public async Task<Result<VersionedPrimaryScopeAttachment>> ReadWithRevisionAsync(CancellationToken ct = default)
    {
        var res = await ReadInternalAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccess)
            return Result.Fail<VersionedPrimaryScopeAttachment>(res.Error);
        return Result.Ok(new VersionedPrimaryScopeAttachment(res.Value.Projection, res.Value.Document.Revision));
    }

    public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, long expectedRevision = -1, CancellationToken ct = default)
        => MutateAsync(
            expectedRevision: expectedRevision,
            build: _ => attachment,
            scopeCheck: null,
            requireScopeKind: null,
            ct: ct);

    public async Task<Result> InitializeAsync(CancellationToken ct = default)
    {
        if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out var anchorFailure))
            return Result.Fail(anchorFailure);
        if (LegacyLayoutDetector.IsLegacyLayoutPresent(_paths.TwigDir))
            return Result.Fail(AttachmentStorageFailure.LegacyLayoutPresent);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.TwigDir);
            await EnsureLayoutMarkerAsync(ct).ConfigureAwait(false);
            await WriteFingerprintAsync(anchor, ct).ConfigureAwait(false);

            var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);
            if (!File.Exists(attachmentPath))
            {
                var empty = AttachmentDocument.Empty(ConnectionRefResolver.Compute(_config));
                await WriteJsonAtomicAsync(attachmentPath, empty, TwigJsonContext.Default.AttachmentDocument, ct)
                    .ConfigureAwait(false);
            }
            return Result.Ok();
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex) { return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}"); }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<Result> LinkClaimAsync(
        string claimId,
        DateTimeOffset mintedAt,
        string expectedPrimaryScopeKind,
        int expectedWorkItemId,
        long expectedRevision,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(claimId))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: claimId is required."));
        if (string.IsNullOrEmpty(expectedPrimaryScopeKind))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedPrimaryScopeKind is required."));
        if (expectedWorkItemId <= 0)
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedWorkItemId must be positive."));

        return MutateAsync(
            expectedRevision: expectedRevision,
            build: current => current with { ActiveClaim = new ActiveClaimReference(claimId, mintedAt) },
            scopeCheck: current =>
            {
                if (current.PrimaryScope is not { } scope) return AttachmentStorageFailure.AttachmentScopeMismatch;
                if (scope.WorkItemId != expectedWorkItemId) return AttachmentStorageFailure.AttachmentScopeMismatch;
                return null;
            },
            requireScopeKind: expectedPrimaryScopeKind,
            ct: ct);
    }

    public Task<Result> UnlinkClaimAsync(string expectedClaimId, long expectedRevision, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedClaimId))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedClaimId is required."));

        return MutateAsync(
            expectedRevision: expectedRevision,
            build: current =>
            {
                if (current.ActiveClaim is null
                    || !string.Equals(current.ActiveClaim.Value.ClaimId, expectedClaimId, StringComparison.Ordinal))
                {
                    return current; // idempotent short-circuit
                }
                return current with { ActiveClaim = null };
            },
            scopeCheck: null,
            requireScopeKind: null,
            ct: ct);
    }

    // ── Internals ────────────────────────────────────────────────────

    private readonly record struct ReadOutcome(PrimaryScopeAttachment Projection, AttachmentDocument Document);

    private async Task<Result<ReadOutcome>> ReadInternalAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out var anchorFailure))
            return Result.Fail<ReadOutcome>(anchorFailure);
        if (LegacyLayoutDetector.IsLegacyLayoutPresent(_paths.TwigDir))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.LegacyLayoutPresent);

        var layoutPath = Path.Combine(_paths.TwigDir, LayoutFileName);
        if (!File.Exists(layoutPath))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.LayoutMarkerMissing);

        var driftError = await ValidateFingerprintAsync(anchor, ct).ConfigureAwait(false);
        if (driftError is not null)
            return Result.Fail<ReadOutcome>(driftError);

        var connectionRef = ConnectionRefResolver.Compute(_config);
        var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);
        if (!File.Exists(attachmentPath))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.LayoutMarkerMissing);

        AttachmentDocument? doc;
        try
        {
            await using var stream = File.OpenRead(attachmentPath);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AttachmentDocument, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException) { return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid); }
        catch (IOException ex) { return Result.Fail<ReadOutcome>($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}"); }

        if (doc is null) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        if (doc.Revision < 0) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        if (string.IsNullOrEmpty(doc.ConnectionRef)) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        if (!string.Equals(doc.ConnectionRef, connectionRef, StringComparison.Ordinal))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.AttachmentConnectionMismatch);

        PrimaryScope? scope = null;
        if (doc.PrimaryScope is { } ps)
        {
            if (ps.WorkItemId <= 0) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (string.IsNullOrEmpty(ps.Kind)) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (!DateTimeOffset.TryParse(ps.AttachedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var attachedAt))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (string.IsNullOrEmpty(ps.WorkItemUrl))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (!AdoWorkItemUrlValidator.OriginMatches(ps.WorkItemUrl, _config.Organization, _config.Project))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.AttachmentConnectionMismatch);
            scope = new PrimaryScope(ps.WorkItemId, ps.WorkItemUrl, attachedAt);
        }

        ActiveClaimReference? claim = null;
        if (doc.ActiveClaim is { } ac)
        {
            if (string.IsNullOrEmpty(ac.ClaimId)) return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (!DateTimeOffset.TryParse(ac.MintedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var mintedAt))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            claim = new ActiveClaimReference(ac.ClaimId, mintedAt);
        }

        return Result.Ok(new ReadOutcome(new PrimaryScopeAttachment(doc.ConnectionRef, scope, claim), doc));
    }

    private async Task<Result> MutateAsync(
        long expectedRevision,
        Func<PrimaryScopeAttachment, PrimaryScopeAttachment> build,
        Func<PrimaryScopeAttachment, string?>? scopeCheck,
        string? requireScopeKind,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        FileStream? lockHandle = null;
        try
        {
            lockHandle = await AcquireCrossProcessLockAsync(ct).ConfigureAwait(false);
            if (lockHandle is null)
                return Result.Fail(AttachmentStorageFailure.AttachmentVersionMismatch);

            var readRes = await ReadInternalAsync(ct).ConfigureAwait(false);
            if (!readRes.IsSuccess)
                return Result.Fail(readRes.Error);

            var currentProjection = readRes.Value.Projection;
            var currentDoc = readRes.Value.Document;

            if (expectedRevision >= 0 && currentDoc.Revision != expectedRevision)
                return Result.Fail(AttachmentStorageFailure.AttachmentVersionMismatch);

            if (scopeCheck is not null)
            {
                var scopeErr = scopeCheck(currentProjection);
                if (scopeErr is not null)
                    return Result.Fail(scopeErr);
            }
            if (requireScopeKind is not null)
            {
                var storedKind = currentDoc.PrimaryScope?.Kind;
                if (!string.Equals(storedKind, requireScopeKind, StringComparison.Ordinal))
                    return Result.Fail(AttachmentStorageFailure.AttachmentScopeMismatch);
            }

            var next = build(currentProjection);
            if (ReferenceEquals(next, currentProjection) || next.Equals(currentProjection))
                return Result.Ok(); // no-op

            var expectedRef = ConnectionRefResolver.Compute(_config);
            if (!string.Equals(next.ConnectionRef, expectedRef, StringComparison.Ordinal))
                return Result.Fail(AttachmentStorageFailure.AttachmentConnectionMismatch);

            var nextDoc = BuildDocument(next, currentDoc.Revision + 1);
            var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);
            try
            {
                await WriteJsonAtomicAsync(attachmentPath, nextDoc, TwigJsonContext.Default.AttachmentDocument, ct)
                    .ConfigureAwait(false);
                return Result.Ok();
            }
            catch (IOException ex) { return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}"); }
        }
        finally
        {
            lockHandle?.Dispose();
            _writeGate.Release();
        }
    }

    /// <summary>Acquires an exclusive OS-visible lock via
    /// <c>attachment.json.lock</c>. <see cref="FileShare.None"/> gives any
    /// peer process an <see cref="IOException"/> when it attempts to
    /// open, which is the cross-process serialization signal. Bounded
    /// retry so a stale lock does not permanently block us; a
    /// long-outstanding lock returns null → the caller surfaces as
    /// <c>attachment-version-mismatch</c>.</summary>
    private async Task<FileStream?> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.TwigDir);
        var lockPath = Path.Combine(_paths.TwigDir, AttachmentLockFileName);
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

    private static AttachmentDocument BuildDocument(PrimaryScopeAttachment attachment, long nextRevision) =>
        new(
            Schema: AttachmentDocument.CurrentSchema,
            Version: AttachmentDocument.CurrentVersion,
            Revision: nextRevision,
            ConnectionRef: attachment.ConnectionRef,
            PrimaryScope: attachment.PrimaryScope is { } scope
                ? new AttachmentPrimaryScope(
                    Kind: PrimaryScopeKinds.AdoWorkItem,
                    WorkItemId: scope.WorkItemId,
                    WorkItemUrl: scope.WorkItemUrl,
                    AttachedAt: scope.AttachedAt.ToUniversalTime().ToString("o"))
                : null,
            ActiveClaim: attachment.ActiveClaim is { } claim
                ? new AttachmentActiveClaim(claim.ClaimId, claim.MintedAt.ToUniversalTime().ToString("o"))
                : null);

    private async Task EnsureLayoutMarkerAsync(CancellationToken ct)
    {
        var path = Path.Combine(_paths.TwigDir, LayoutFileName);
        if (File.Exists(path)) return;

        var doc = new LayoutMarkerDocument(
            Schema: LayoutMarkerDocument.CurrentSchema,
            Version: LayoutMarkerDocument.CurrentVersion,
            InitializedAt: _clock.GetUtcNow().ToString("o"),
            CreatedBy: "twig-cli/attachment");
        await WriteJsonAtomicAsync(path, doc, TwigJsonContext.Default.LayoutMarkerDocument, ct).ConfigureAwait(false);
    }

    private async Task WriteFingerprintAsync(WorktreeAnchor anchor, CancellationToken ct)
    {
        var path = Path.Combine(_paths.TwigDir, WorktreeFileName);
        var doc = new WorktreeFingerprintDocument(
            Schema: WorktreeFingerprintDocument.CurrentSchema,
            Version: WorktreeFingerprintDocument.CurrentVersion,
            WorktreeFingerprint: new WorktreeFingerprintTuple(anchor.GitCommonDir, anchor.WorktreeGitDir, anchor.WorktreeRoot));
        await WriteJsonAtomicAsync(path, doc, TwigJsonContext.Default.WorktreeFingerprintDocument, ct)
            .ConfigureAwait(false);
    }

    private async Task<string?> ValidateFingerprintAsync(WorktreeAnchor live, CancellationToken ct)
    {
        var path = Path.Combine(_paths.TwigDir, WorktreeFileName);
        if (!File.Exists(path)) return AttachmentStorageFailure.WorktreeFingerprintDrift;

        WorktreeFingerprintDocument? doc;
        try
        {
            await using var stream = File.OpenRead(path);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.WorktreeFingerprintDocument, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return AttachmentStorageFailure.WorktreeFingerprintDrift; }

        if (doc is null) return AttachmentStorageFailure.WorktreeFingerprintDrift;

        var stored = doc.WorktreeFingerprint;
        if (string.IsNullOrEmpty(stored.WorktreeRoot)
            || string.IsNullOrEmpty(stored.GitCommonDir)
            || string.IsNullOrEmpty(stored.WorktreeGitDir))
            return AttachmentStorageFailure.WorktreeFingerprintDrift;

        if (!PathsEqual(stored.WorktreeRoot, live.WorktreeRoot)
            || !PathsEqual(stored.GitCommonDir, live.GitCommonDir)
            || !PathsEqual(stored.WorktreeGitDir, live.WorktreeGitDir))
        {
            return AttachmentStorageFailure.WorktreeFingerprintDrift;
        }
        return null;
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return comparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));
    }

    private async Task WriteJsonAtomicAsync<T>(
        string targetPath,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        var tmpDir = Path.Combine(_paths.TwigDir, TmpDirName);
        Directory.CreateDirectory(tmpDir);
        var tmpPath = Path.Combine(tmpDir, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
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

/// <summary>
/// Detects the legacy pre-AB#736 layout so runtime storage reads/writes
/// refuse fail-closed rather than adopt the old tree silently.
/// </summary>
internal static class LegacyLayoutDetector
{
    public static bool IsLegacyLayoutPresent(string? twigDir)
    {
        if (string.IsNullOrEmpty(twigDir) || !Directory.Exists(twigDir))
            return false;
        if (File.Exists(Path.Combine(twigDir, WorktreeLocalAttachmentStore.LayoutFileName)))
            return false;
        try
        {
            foreach (var orgDir in Directory.EnumerateDirectories(twigDir))
            {
                var name = Path.GetFileName(orgDir);
                if (string.Equals(name, WorktreeLocalAttachmentStore.TmpDirName, StringComparison.Ordinal)
                    || name.StartsWith('.'))
                    continue;
                foreach (var projectDir in Directory.EnumerateDirectories(orgDir))
                {
                    if (File.Exists(Path.Combine(projectDir, "twig.db")))
                        return true;
                }
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        return false;
    }
}

internal static class AdoWorkItemUrlValidator
{
    public static bool OriginMatches(string? url, string? organization, string? project)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(organization) || string.IsNullOrEmpty(project))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("https" or "http")) return false;

        var orgSlug = OrganizationNormalizer.ToSlug(organization);
        var host = uri.Host;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length >= 2
                && string.Equals(segments[0], orgSlug, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], project, StringComparison.OrdinalIgnoreCase);
        }

        const string legacySuffix = ".visualstudio.com";
        if (host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase))
        {
            var orgFromHost = host[..^legacySuffix.Length];
            return string.Equals(orgFromHost, orgSlug, StringComparison.OrdinalIgnoreCase)
                && segments.Length >= 1
                && string.Equals(segments[0], project, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static string BuildWorkItemUrl(string organization, string project, int workItemId)
    {
        var slug = OrganizationNormalizer.ToSlug(organization);
        return $"https://dev.azure.com/{Uri.EscapeDataString(slug)}/{Uri.EscapeDataString(project)}/_workitems/edit/{workItemId}";
    }
}
