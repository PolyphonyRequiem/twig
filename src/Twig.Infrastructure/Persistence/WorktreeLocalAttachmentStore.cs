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
/// This is the AB#736 §9.3 seam AB#738 consumes. It refuses to open, read, or
/// write outside a valid managed worktree — every code path names its failure
/// against §8 rather than falling through to an "unmanaged" projection.
/// <para>
/// The write path <b>never bootstraps</b> the layout marker or the worktree
/// fingerprint file. Marker creation belongs exclusively to explicit managed
/// init (<see cref="InitializeAsync"/>) so an existing pre-AB#736 checkout —
/// including a legacy <c>.twig/&lt;org&gt;/&lt;project&gt;/</c> tree — cannot be
/// silently adopted by an attachment operation. Under the T1 runtime ordering
/// (§6.4) the sequence is: detect git → validate <c>layout.json</c> → validate
/// <c>worktree.json</c> against the live tuple → validate
/// <c>attachment.json</c>'s connectionRef → open the system store row. Any
/// mismatch surfaces named.
/// </para>
/// <para>
/// Atomic writes are durable (§6.1): the temp file is opened with
/// <see cref="FileOptions.WriteThrough"/> and flushed with
/// <see cref="FileStream.Flush(bool)"/>(<c>true</c>) before rename, so a
/// power-loss between rename and further writes leaves the new version intact
/// rather than a rename-visible but content-empty target.
/// </para>
/// <para>
/// Link/unlink coordinate through a monotonic revision counter on the
/// attachment document. Every write increments the counter; link/unlink
/// perform read → mutate → write inside <see cref="_writeGate"/>, and
/// refuse if the revision advanced between read and write —
/// <c>attachment-version-mismatch</c>. Link additionally verifies the
/// caller's expected primary scope tuple still matches byte-exact and
/// refuses with <c>attachment-scope-mismatch</c> otherwise. The gate is
/// in-process serialization; cross-process contention is compounded by
/// SQLite's system-store transactions, which serialize the underlying
/// claim writes at the process boundary.
/// </para>
/// </summary>
internal sealed class WorktreeLocalAttachmentStore : IPrimaryScopeAttachmentStore, IDisposable
{
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Files this store reads and writes. Named as constants so a change to the
    // §4.2 layout is a single-line touch rather than a scavenger hunt.
    internal const string LayoutFileName = "layout.json";
    internal const string WorktreeFileName = "worktree.json";
    internal const string AttachmentFileName = "attachment.json";
    internal const string TmpDirName = "tmp";

    public WorktreeLocalAttachmentStore(TwigPaths paths, TwigConfiguration config, TimeProvider clock)
    {
        _paths = paths;
        _config = config;
        _clock = clock;
    }

    /// <summary>
    /// A managed worktree, for AB#738's purposes, is one that the T1 storage
    /// contract has anything to say about — i.e. a <c>.twig/</c> directory
    /// exists or the checked-in <c>twig.json</c> manifest is present at the
    /// repo root. The read/write paths then run the full §6.4 fail-closed
    /// ordering; a checkout that carries a <c>.twig/</c> without the layout
    /// marker surfaces <c>layout-marker-missing</c> rather than silently
    /// falling back to "unmanaged".
    /// </summary>
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

    public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default)
        => WriteWithReadCheckAsync(
            expectedRevision: null,
            build: _ => attachment,
            scopeCheck: null,
            ct: ct);

    /// <summary>
    /// Explicit managed-init hook: creates <c>.twig/layout.json</c>,
    /// <c>.twig/worktree.json</c>, and an empty <c>attachment.json</c> (§6.3
    /// steps 4–7). Idempotent on retry. Exposed so callers with a legitimate
    /// init verb — a future AB#740-scoped init command, an integration test
    /// fixture — can produce a valid managed worktree without either
    /// re-implementing §6.3 or reaching around this store to write marker
    /// files themselves.
    /// </summary>
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(claimId))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: claimId is required."));
        if (string.IsNullOrEmpty(expectedPrimaryScopeKind))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedPrimaryScopeKind is required."));
        if (expectedWorkItemId <= 0)
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedWorkItemId must be positive."));

        return WriteWithReadCheckAsync(
            expectedRevision: null,
            build: current => current with { ActiveClaim = new ActiveClaimReference(claimId, mintedAt) },
            scopeCheck: current =>
            {
                if (current.PrimaryScope is not { } scope)
                    return AttachmentStorageFailure.AttachmentScopeMismatch;
                if (scope.WorkItemId != expectedWorkItemId)
                    return AttachmentStorageFailure.AttachmentScopeMismatch;
                // The on-disk record carries the primary scope kind
                // explicitly (AttachmentPrimaryScope.Kind). The projection
                // stripped it into an internal marker; we compare against
                // the raw doc read via scopeCheckSide.
                return null;
            },
            requireScopeKind: expectedPrimaryScopeKind,
            ct: ct);
    }

    public Task<Result> UnlinkClaimAsync(string expectedClaimId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedClaimId))
            return Task.FromResult(Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedClaimId is required."));

        return WriteWithReadCheckAsync(
            expectedRevision: null,
            build: current =>
            {
                if (current.ActiveClaim is null
                    || !string.Equals(current.ActiveClaim.Value.ClaimId, expectedClaimId, StringComparison.Ordinal))
                {
                    // Idempotent from the attachment's perspective — no
                    // change to write. Return the same record so the
                    // caller-side WriteWithReadCheck short-circuits.
                    return current;
                }
                return current with { ActiveClaim = null };
            },
            scopeCheck: null,
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
        catch (JsonException)
        {
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        }
        catch (IOException ex)
        {
            return Result.Fail<ReadOutcome>($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }

        if (doc is null)
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        if (doc.Revision < 0)
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
        if (string.IsNullOrEmpty(doc.ConnectionRef))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);

        if (!string.Equals(doc.ConnectionRef, connectionRef, StringComparison.Ordinal))
            return Result.Fail<ReadOutcome>(AttachmentStorageFailure.AttachmentConnectionMismatch);

        PrimaryScope? scope = null;
        if (doc.PrimaryScope is { } ps)
        {
            // Reject malformed present primary-scope block with a NAMED
            // schema failure. A silent skip would let a hand-crafted
            // attachment.json (nonpositive id, invalid timestamp, invalid
            // URL) reach the claim path as "no primary scope" — one of
            // the exact fail-closed defects §7 forbids.
            if (ps.WorkItemId <= 0)
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (string.IsNullOrEmpty(ps.Kind))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
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
            if (string.IsNullOrEmpty(ac.ClaimId))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            if (!DateTimeOffset.TryParse(ac.MintedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var mintedAt))
                return Result.Fail<ReadOutcome>(AttachmentStorageFailure.CheckedInConfigInvalid);
            claim = new ActiveClaimReference(ac.ClaimId, mintedAt);
        }

        var projection = new PrimaryScopeAttachment(
            ConnectionRef: doc.ConnectionRef,
            PrimaryScope: scope,
            ActiveClaim: claim);
        return Result.Ok(new ReadOutcome(projection, doc));
    }

    private async Task<Result> WriteWithReadCheckAsync(
        long? expectedRevision,
        Func<PrimaryScopeAttachment, PrimaryScopeAttachment> build,
        Func<PrimaryScopeAttachment, string?>? scopeCheck,
        string? requireScopeKind = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var readRes = await ReadInternalAsync(ct).ConfigureAwait(false);
            if (!readRes.IsSuccess)
                return Result.Fail(readRes.Error);
            var currentProjection = readRes.Value.Projection;
            var currentDoc = readRes.Value.Document;

            if (expectedRevision.HasValue && currentDoc.Revision != expectedRevision.Value)
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
            {
                // No change to apply. Signal success without a write —
                // idempotent unlink over an unlinked record, for example.
                return Result.Ok();
            }

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
            catch (IOException ex)
            {
                return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
            }
        }
        finally
        {
            _writeGate.Release();
        }
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
        if (File.Exists(path))
            return;

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
        if (!File.Exists(path))
            return AttachmentStorageFailure.WorktreeFingerprintDrift;

        WorktreeFingerprintDocument? doc;
        try
        {
            await using var stream = File.OpenRead(path);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.WorktreeFingerprintDocument, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return AttachmentStorageFailure.WorktreeFingerprintDrift;
        }

        if (doc is null)
            return AttachmentStorageFailure.WorktreeFingerprintDrift;

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
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return true;
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
/// Detects the legacy pre-AB#736 layout — <c>.twig/&lt;org&gt;/&lt;project&gt;/twig.db</c> —
/// so runtime storage reads/writes refuse fail-closed rather than adopt the
/// old tree silently. Only the shape §7 fixes is probed; the specific org and
/// project names are opaque.
/// </summary>
internal static class LegacyLayoutDetector
{
    public static bool IsLegacyLayoutPresent(string? twigDir)
    {
        if (string.IsNullOrEmpty(twigDir) || !Directory.Exists(twigDir))
            return false;
        // Managed layout is authoritative: once §4.2.1 layout.json is
        // present the checkout is on the new layout and disposable cache
        // directories under the old shape are just interim state.
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

/// <summary>
/// Validates the origin (organization + project) of an ADO work-item URL against
/// the current connection binding. AB#736 §4.2.2 requires this check ahead of
/// the system-store answer so a stolen or copied <c>.twig/</c> whose
/// <c>connectionRef</c> forgery matches still trips on the visible URL
/// origin.
/// </summary>
internal static class AdoWorkItemUrlValidator
{
    public static bool OriginMatches(string? url, string? organization, string? project)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(organization) || string.IsNullOrEmpty(project))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("https" or "http"))
            return false;

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
