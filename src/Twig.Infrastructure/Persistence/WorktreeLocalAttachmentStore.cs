using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Worktree-local implementation of <see cref="IPrimaryScopeAttachmentStore"/> —
/// the §9.3 seam AB#738 consumes. Writes atomically via temp file + rename;
/// validates the layout marker, the worktree fingerprint, and the connection ref
/// on every read (§6.4). AB#736's full managed-init and legacy-layout migration
/// are not run here; instead the marker + fingerprint files are populated the
/// first time an attach write lands, so an existing <c>.twig/</c> is never
/// silently repurposed and a checkout that carries neither today reads as
/// "unattached" rather than "legacy-layout-present".
/// <para>
/// The store never writes when a validation refusal fires: refusals surface
/// before the temp file is created, so an ineligible-type refusal at the service
/// layer leaves <c>attachment.json</c> byte-identical.
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

    public bool IsManagedWorktree()
    {
        // A managed worktree, for AB#738's purposes, is one where the CLI has
        // discovered a workspace anchor (twig.json + optional .twig/). This
        // matches WorkspaceDiscovery.IsWorkspaceDirectory; the stricter
        // AB#736 §3.1 anchor rules apply on read/write, not on the human
        // status projection's presence check.
        var twigDir = _paths.TwigDir;
        if (string.IsNullOrEmpty(twigDir))
            return false;

        var hasTwigDir = Directory.Exists(twigDir);
        var hasManifest = !string.IsNullOrEmpty(_paths.RepoConfigPath) && File.Exists(_paths.RepoConfigPath);
        return hasTwigDir || hasManifest;
    }

    public async Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default)
    {
        if (!IsManagedWorktree())
            return Result.Fail<PrimaryScopeAttachment>("not-a-git-worktree");

        var connectionRef = ConnectionRefResolver.Compute(_config);
        var attachmentPath = Path.Combine(_paths.TwigDir, AttachmentFileName);

        // Absent attachment.json is treated as "managed but unattached". This
        // deliberately does NOT surface layout-marker-missing so an existing
        // checkout that predates AB#736's init still reads clean. Once
        // WriteAsync lands even once, all three files exist and every future
        // read runs the full drift check below.
        if (!File.Exists(attachmentPath))
            return Result.Ok(PrimaryScopeAttachment.Empty(connectionRef));

        var driftError = await ValidateFingerprintAsync(ct).ConfigureAwait(false);
        if (driftError is not null)
            return Result.Fail<PrimaryScopeAttachment>(driftError);

        AttachmentDocument? doc;
        try
        {
            await using var stream = File.OpenRead(attachmentPath);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.AttachmentDocument, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Result.Fail<PrimaryScopeAttachment>("attachment-connection-mismatch: unparseable attachment.json");
        }
        catch (IOException ex)
        {
            return Result.Fail<PrimaryScopeAttachment>($"atomic-write-failed: {ex.Message}");
        }

        if (doc is null)
            return Result.Ok(PrimaryScopeAttachment.Empty(connectionRef));

        if (!string.Equals(doc.ConnectionRef, connectionRef, StringComparison.Ordinal))
            return Result.Fail<PrimaryScopeAttachment>("attachment-connection-mismatch");

        PrimaryScope? scope = null;
        if (doc.PrimaryScope is { } ps
            && DateTimeOffset.TryParse(ps.AttachedAt, out var attachedAt))
        {
            scope = new PrimaryScope(ps.WorkItemId, ps.WorkItemUrl, attachedAt);
        }

        return Result.Ok(new PrimaryScopeAttachment(
            ConnectionRef: doc.ConnectionRef,
            PrimaryScope: scope,
            ActiveClaimId: doc.ActiveClaim?.ClaimId));
    }

    public async Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default)
    {
        if (!IsManagedWorktree())
            return Result.Fail("not-a-git-worktree");

        var expectedRef = ConnectionRefResolver.Compute(_config);
        if (!string.Equals(attachment.ConnectionRef, expectedRef, StringComparison.Ordinal))
            return Result.Fail("attachment-connection-mismatch");

        try
        {
            Directory.CreateDirectory(_paths.TwigDir);
            await EnsureLayoutMarkerAsync(ct).ConfigureAwait(false);
            await EnsureWorktreeFingerprintAsync(ct).ConfigureAwait(false);

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
                ActiveClaim: attachment.ActiveClaimId is { Length: > 0 } claimId
                    ? new AttachmentActiveClaim(claimId, _clock.GetUtcNow().ToString("o"))
                    : null);

            await WriteJsonAtomicAsync(attachmentPath, doc, TwigJsonContext.Default.AttachmentDocument, ct)
                .ConfigureAwait(false);
            return Result.Ok();
        }
        catch (IOException ex)
        {
            return Result.Fail($"atomic-write-failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Fail($"atomic-write-failed: {ex.Message}");
        }
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

    private async Task EnsureWorktreeFingerprintAsync(CancellationToken ct)
    {
        var path = Path.Combine(_paths.TwigDir, WorktreeFileName);
        if (File.Exists(path))
            return;

        var startDir = _paths.StartDir ?? Path.GetDirectoryName(_paths.TwigDir) ?? string.Empty;
        var detected = WorktreeAnchorDetector.Detect(startDir);
        var tuple = detected is { } anchor
            ? new WorktreeFingerprintTuple(anchor.GitCommonDir, anchor.WorktreeGitDir, anchor.WorktreeRoot)
            : new WorktreeFingerprintTuple(string.Empty, string.Empty, Path.GetDirectoryName(_paths.TwigDir) ?? string.Empty);

        var doc = new WorktreeFingerprintDocument(
            Schema: WorktreeFingerprintDocument.CurrentSchema,
            Version: WorktreeFingerprintDocument.CurrentVersion,
            WorktreeFingerprint: tuple);
        await WriteJsonAtomicAsync(path, doc, TwigJsonContext.Default.WorktreeFingerprintDocument, ct)
            .ConfigureAwait(false);
    }

    private async Task<string?> ValidateFingerprintAsync(CancellationToken ct)
    {
        var path = Path.Combine(_paths.TwigDir, WorktreeFileName);
        if (!File.Exists(path))
        {
            return "worktree-fingerprint-drift";
        }

        WorktreeFingerprintDocument? doc;
        try
        {
            await using var stream = File.OpenRead(path);
            doc = await JsonSerializer.DeserializeAsync(
                stream, TwigJsonContext.Default.WorktreeFingerprintDocument, ct).ConfigureAwait(false);
        }
        catch
        {
            return "worktree-fingerprint-drift";
        }

        if (doc is null)
            return "worktree-fingerprint-drift";

        // Missing anchor tuple in the stored doc is treated as "unverifiable" —
        // silence the drift check rather than fabricate a mismatch. The zeroed
        // doc is the "no git available" bootstrap path.
        if (string.IsNullOrEmpty(doc.WorktreeFingerprint.WorktreeRoot))
            return null;

        var startDir = _paths.StartDir ?? Path.GetDirectoryName(_paths.TwigDir) ?? string.Empty;
        var live = WorktreeAnchorDetector.Detect(startDir);
        if (live is null)
            return null; // git unavailable — do not falsely accuse drift.

        var stored = doc.WorktreeFingerprint;
        if (!PathsEqual(stored.WorktreeRoot, live.Value.WorktreeRoot)
            || !PathsEqual(stored.GitCommonDir, live.Value.GitCommonDir)
            || !PathsEqual(stored.WorktreeGitDir, live.Value.WorktreeGitDir))
        {
            return "worktree-fingerprint-drift";
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
            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
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
