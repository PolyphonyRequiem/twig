using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
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
/// </summary>
internal sealed class WorktreeLocalAttachmentStore : IPrimaryScopeAttachmentStore
{
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly TimeProvider _clock;

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
        ct.ThrowIfCancellationRequested();

        // §6.4 step 1 — detect roots. Failure here is fatal for every managed
        // read; the store never returns an empty attachment as a substitute
        // for a valid anchor.
        if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out var anchorFailure))
            return Result.Fail<PrimaryScopeAttachment>(anchorFailure);

        // Legacy layout check: an ancestor <c>.twig/&lt;org&gt;/&lt;project&gt;/twig.db</c>
        // tree is exactly the "silently adopt an old checkout" scenario §7
        // forbids. Refuse fail-closed with the §8 identifier.
        if (LegacyLayoutDetector.IsLegacyLayoutPresent(_paths.TwigDir))
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.LegacyLayoutPresent);

        // §6.4 step 3 — layout marker is the observable "new layout" flag.
        var layoutPath = Path.Combine(_paths.TwigDir, LayoutFileName);
        if (!File.Exists(layoutPath))
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.LayoutMarkerMissing);

        // §6.4 step 4 — worktree fingerprint MUST match the live rev-parse
        // tuple byte-equal. A missing or unparseable file, or a fabricated
        // empty tuple, is drift.
        var driftError = await ValidateFingerprintAsync(anchor, ct).ConfigureAwait(false);
        if (driftError is not null)
            return Result.Fail<PrimaryScopeAttachment>(driftError);

        var connectionRef = ConnectionRefResolver.Compute(_config);
        var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);
        if (!File.Exists(attachmentPath))
        {
            // Marker present, attachment.json missing → partial init. Managed
            // init writes an empty attachment at step 7; its absence here
            // matches the layout-marker-missing symptom §8 covers.
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.LayoutMarkerMissing);
        }

        AttachmentDocument? doc;
        try
        {
            await using var stream = File.OpenRead(attachmentPath);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AttachmentDocument, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.CheckedInConfigInvalid);
        }
        catch (IOException ex)
        {
            return Result.Fail<PrimaryScopeAttachment>($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }

        if (doc is null)
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.CheckedInConfigInvalid);

        if (!string.Equals(doc.ConnectionRef, connectionRef, StringComparison.Ordinal))
            return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.AttachmentConnectionMismatch);

        PrimaryScope? scope = null;
        if (doc.PrimaryScope is { } ps
            && DateTimeOffset.TryParse(ps.AttachedAt, out var attachedAt))
        {
            // §4.2.2: the workItemUrl origin MUST match the checked-in
            // connection. A copied .twig/ carrying a forged connectionRef
            // still trips this because the URL origin cannot be forged
            // without also editing every downstream visible link.
            if (!AdoWorkItemUrlValidator.OriginMatches(ps.WorkItemUrl, _config.Organization, _config.Project))
                return Result.Fail<PrimaryScopeAttachment>(AttachmentStorageFailure.AttachmentConnectionMismatch);

            scope = new PrimaryScope(ps.WorkItemId, ps.WorkItemUrl, attachedAt);
        }

        ActiveClaimReference? claim = null;
        if (doc.ActiveClaim is { } ac && DateTimeOffset.TryParse(ac.MintedAt, out var mintedAt))
            claim = new ActiveClaimReference(ac.ClaimId, mintedAt);

        return Result.Ok(new PrimaryScopeAttachment(
            ConnectionRef: doc.ConnectionRef,
            PrimaryScope: scope,
            ActiveClaim: claim));
    }

    public async Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir ?? _paths.TwigDir, out var anchor, out var anchorFailure))
            return Result.Fail(anchorFailure);

        if (LegacyLayoutDetector.IsLegacyLayoutPresent(_paths.TwigDir))
            return Result.Fail(AttachmentStorageFailure.LegacyLayoutPresent);

        // Writes NEVER bootstrap markers. If the layout marker is absent the
        // caller must run managed init first; adopting an existing .twig/
        // silently was the exact defect §7 fixes.
        var layoutPath = Path.Combine(_paths.TwigDir, LayoutFileName);
        if (!File.Exists(layoutPath))
            return Result.Fail(AttachmentStorageFailure.LayoutMarkerMissing);

        var driftError = await ValidateFingerprintAsync(anchor, ct).ConfigureAwait(false);
        if (driftError is not null)
            return Result.Fail(driftError);

        var expectedRef = ConnectionRefResolver.Compute(_config);
        if (!string.Equals(attachment.ConnectionRef, expectedRef, StringComparison.Ordinal))
            return Result.Fail(AttachmentStorageFailure.AttachmentConnectionMismatch);

        try
        {
            var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);
            var doc = new AttachmentDocument(
                Schema: AttachmentDocument.CurrentSchema,
                Version: AttachmentDocument.CurrentVersion,
                ConnectionRef: attachment.ConnectionRef,
                PrimaryScope: attachment.PrimaryScope is { } scope
                    ? new AttachmentPrimaryScope(
                        scope.WorkItemId,
                        scope.WorkItemUrl,
                        scope.AttachedAt.ToUniversalTime().ToString("o"))
                    : null,
                // AB#736 §9.3: consumers set one field without disturbing the
                // other. AB#738 writes primary scope only; the ActiveClaim
                // block is carried through byte-identical (opaque id +
                // original mint timestamp) so an AB#739-minted record
                // survives an AB#738 switch or detach.
                ActiveClaim: attachment.ActiveClaim is { } claim
                    ? new AttachmentActiveClaim(claim.ClaimId, claim.MintedAt.ToUniversalTime().ToString("o"))
                    : null);

            await WriteJsonAtomicAsync(attachmentPath, doc, TwigJsonContext.Default.AttachmentDocument, ct)
                .ConfigureAwait(false);
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
    }

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
    }

    /// <summary>
    /// Bind the given active-claim reference onto the current attachment
    /// (AB#737 §Interface consumed by #739, step 4 of mint/reclaim). Reads
    /// the current record, replaces the <c>ActiveClaim</c> block with the
    /// new (id, mint-timestamp) pair, and re-runs
    /// <see cref="WriteAsync"/> — the whole read/validate/write sequence
    /// so the connectionRef, layout-marker, and fingerprint checks apply.
    /// The primary-scope block is preserved byte-for-byte.
    /// </summary>
    public async Task<Result> LinkClaimAsync(string claimId, DateTimeOffset mintedAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(claimId))
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: claimId is required.");
        var read = await ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail(read.Error);
        var next = read.Value with { ActiveClaim = new ActiveClaimReference(claimId, mintedAt) };
        return await WriteAsync(next, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drop the active-claim reference when it points at
    /// <paramref name="expectedClaimId"/>. If the record already carries no
    /// claim, or references a different id, the call is a success — release
    /// is idempotent from the attachment's perspective (AB#737 §Named
    /// release outcomes preserves the "unlink after terminalize" ordering
    /// even when the attachment has already been cleared by another writer).
    /// </summary>
    public async Task<Result> UnlinkClaimAsync(string expectedClaimId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expectedClaimId))
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: expectedClaimId is required.");
        var read = await ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail(read.Error);
        var current = read.Value;
        if (current.ActiveClaim is null
            || !string.Equals(current.ActiveClaim.Value.ClaimId, expectedClaimId, StringComparison.Ordinal))
        {
            return Result.Ok();
        }
        var next = current with { ActiveClaim = null };
        return await WriteAsync(next, ct).ConfigureAwait(false);
    }

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return AttachmentStorageFailure.WorktreeFingerprintDrift;
        }

        if (doc is null)
            return AttachmentStorageFailure.WorktreeFingerprintDrift;

        var stored = doc.WorktreeFingerprint;
        // An empty stored tuple is not "unverifiable" — §3.1 forbids managed
        // init from ever producing one. Treat it as drift so a hand-crafted
        // or half-init fingerprint fails closed rather than silently passing.
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

    /// <summary>
    /// Atomic write with a durable fsync boundary. Opens the temp file with
    /// <see cref="FileOptions.WriteThrough"/> so writes hit the storage
    /// stack directly and calls <see cref="FileStream.Flush(bool)"/>(<c>true</c>)
    /// before <see cref="File.Move(string, string, bool)"/>, matching §6.1's
    /// "write temp, fsync, rename" success boundary. Rename is atomic on
    /// POSIX and via <c>MoveFileExW</c>+<c>MOVEFILE_REPLACE_EXISTING</c> on
    /// Windows (the runtime default).
    /// </summary>
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
                // Managed flush + WriteThrough is not the same as fsync on
                // every platform; force the durable path explicitly per §6.1.
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
}

/// <summary>
/// Detects the legacy pre-AB#736 layout — <c>.twig/&lt;org&gt;/&lt;project&gt;/twig.db</c> —
/// so runtime storage reads/writes refuse fail-closed rather than adopt the
/// old tree silently. Only the shape §7 fixes is probed; the specific org and
/// project names are opaque.
/// </summary>
internal static class LegacyLayoutDetector
{
    public static bool IsLegacyLayoutPresent(string twigDir)
    {
        if (string.IsNullOrEmpty(twigDir) || !Directory.Exists(twigDir))
            return false;
        // Managed layout is authoritative: once §4.2.1 layout.json is present
        // the checkout is on the new layout, and disposable cache directories
        // under the old `.twig/<org>/<project>/` shape are just interim state
        // rather than "legacy layout". Refusing here would make managed init
        // reject its own run once the SqliteCacheStore fills the cache path.
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
                // Any nested <org>/<project>/twig.db is the legacy shape §7 forbids.
                foreach (var projectDir in Directory.EnumerateDirectories(orgDir))
                {
                    if (File.Exists(Path.Combine(projectDir, "twig.db")))
                        return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }
}

/// <summary>
/// Validates the origin (organization + project) of an ADO work-item URL against
/// the current connection binding. AB#736 §4.2.2 requires this check ahead of
/// the system-store answer so a stolen or copied <c>.twig/</c> whose
/// <c>connectionRef</c> forgery matches still trips on the visible URL
/// origin. The URL shape Twig writes is
/// <c>https://dev.azure.com/{org}/{project}/_workitems/edit/{id}</c>; the
/// validator accepts equivalent legacy shapes (<c>{org}.visualstudio.com</c>)
/// so a repo migrated from the old hostname continues to round-trip.
/// </summary>
internal static class AdoWorkItemUrlValidator
{
    public static bool OriginMatches(string? url, string organization, string project)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(organization) || string.IsNullOrEmpty(project))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("https" or "http"))
            return false;

        // The configured organization may arrive as either a slug ("contoso")
        // or a full URI ("https://dev.azure.com/contoso" or
        // "https://contoso.visualstudio.com"). Normalize once so a mismatched
        // storage-versus-config shape does not surface as a false
        // attachment-connection-mismatch.
        var orgSlug = OrganizationNormalizer.ToSlug(organization);
        var host = uri.Host;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (string.Equals(host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            // {org}/{project}/_workitems/edit/{id}
            return segments.Length >= 2
                && string.Equals(segments[0], orgSlug, StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], project, StringComparison.OrdinalIgnoreCase);
        }

        var legacySuffix = ".visualstudio.com";
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
