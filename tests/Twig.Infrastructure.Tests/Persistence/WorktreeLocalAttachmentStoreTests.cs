using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Fail-closed contract tests for <see cref="WorktreeLocalAttachmentStore"/>.
/// Each case exercises one AB#736 §8 identifier by producing exactly the
/// filesystem state that triggers it and observing the named error verbatim.
/// The tests skip themselves when git is unavailable — the store REQUIRES a
/// live rev-parse for §6.4 step 1, and the point of these cases is the
/// fail-closed identifiers, not the git shell-out.
/// </summary>
public sealed class WorktreeLocalAttachmentStoreTests : IDisposable
{
    private readonly string _workDir;
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly bool _gitAvailable;

    public WorktreeLocalAttachmentStoreTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "twig-attachment-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var twigDir = Path.Combine(_workDir, ".twig");
        _paths = new TwigPaths(
            twigDir: twigDir,
            configPath: Path.Combine(twigDir, "config"),
            dbPath: Path.Combine(twigDir, "twig.db"),
            startDir: _workDir);
        _config = new TwigConfiguration
        {
            Organization = "fixture-org",
            Project = "fixture-project",
        };
        File.WriteAllText(Path.Combine(_workDir, "twig.json"), "{\n}\n");
        _gitAvailable = TryInitGit(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
    }

    private static bool TryInitGit(string dir)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private WorktreeLocalAttachmentStore NewStore() => new(_paths, _config, TimeProvider.System);

    // ── Fail-closed: NO git anchor ─────────────────────────────────────

    [Fact]
    public async Task Read_fails_closed_when_git_is_not_a_worktree()
    {
        // A non-git temp directory: no git init.
        var outsideDir = Path.Combine(Path.GetTempPath(), "twig-no-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            var twigDir = Path.Combine(outsideDir, ".twig");
            var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"), outsideDir);
            var store = new WorktreeLocalAttachmentStore(paths, _config, TimeProvider.System);

            var read = await store.ReadAsync();
            // Either not-a-git-worktree or bare-repository-not-supported — both
            // are §8 identifiers the caller can route on. NEVER a silent
            // "unmanaged" projection.
            read.IsSuccess.ShouldBeFalse();
            read.Error.ShouldBe(AttachmentStorageFailure.NotAGitWorktree);
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    // ── Fail-closed: layout marker missing ─────────────────────────────

    [Fact]
    public async Task Read_fails_closed_with_layout_marker_missing_when_twig_dir_has_no_marker()
    {
        if (!_gitAvailable) return;
        Directory.CreateDirectory(_paths.TwigDir);
        // No layout.json, no worktree.json.

        var store = NewStore();
        var read = await store.ReadAsync();

        // The old behavior silently treated this as "managed but unattached"
        // and let a WriteAsync bootstrap a stray .twig/. The store now
        // refuses fail-closed with the §8 identifier.
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.LayoutMarkerMissing);
    }

    // ── Fail-closed: legacy layout present ─────────────────────────────

    [Fact]
    public async Task Read_fails_closed_with_legacy_layout_present_when_legacy_db_exists()
    {
        if (!_gitAvailable) return;
        // Simulate the exact pre-AB#736 shape §7 forbids.
        var legacyDir = Path.Combine(_paths.TwigDir, "some-org", "some-project");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "twig.db"), new byte[] { 0 });

        var store = NewStore();
        var read = await store.ReadAsync();

        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.LegacyLayoutPresent);
    }

    // ── Fail-closed: worktree fingerprint drift (empty stored tuple) ────

    [Fact]
    public async Task Read_fails_closed_on_empty_stored_fingerprint()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        // Hand-forge an empty fingerprint file to simulate the "fabricated
        // fingerprint" defect: the store MUST refuse rather than treat an
        // empty tuple as "unverifiable → pass".
        var wtPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName);
        var doc = new WorktreeFingerprintDocument(
            WorktreeFingerprintDocument.CurrentSchema,
            WorktreeFingerprintDocument.CurrentVersion,
            new WorktreeFingerprintTuple(string.Empty, string.Empty, string.Empty));
        await File.WriteAllTextAsync(wtPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.WorktreeFingerprintDocument));

        var store = NewStore();
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.WorktreeFingerprintDrift);
    }

    // ── Fail-closed: writes never bootstrap markers ─────────────────────

    [Fact]
    public async Task Write_fails_closed_when_layout_marker_missing()
    {
        if (!_gitAvailable) return;
        Directory.CreateDirectory(_paths.TwigDir);

        var store = NewStore();
        var write = await store.WriteAsync(PrimaryScopeAttachment.Empty(_ConnectionRef()));
        write.IsSuccess.ShouldBeFalse();
        write.Error.ShouldBe(AttachmentStorageFailure.LayoutMarkerMissing);
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeFalse();
    }

    // ── Fail-closed: attachment-connection-mismatch on read ────────────

    [Fact]
    public async Task Read_fails_closed_on_connection_ref_mismatch()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        // Rewrite attachment.json with a different connectionRef.
        var doc = new AttachmentDocument(
            AttachmentDocument.CurrentSchema, AttachmentDocument.CurrentVersion,
            Revision: 0,
            ConnectionRef: "different-ref",
            PrimaryScope: null, ActiveClaim: null);
        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.AttachmentDocument));

        var store = NewStore();
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.AttachmentConnectionMismatch);
    }

    // ── Fail-closed: forged workItemUrl origin mismatch ─────────────────

    [Fact]
    public async Task Read_fails_closed_when_work_item_url_origin_does_not_match_connection()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var doc = new AttachmentDocument(
            AttachmentDocument.CurrentSchema, AttachmentDocument.CurrentVersion,
            Revision: 0,
            ConnectionRef: _ConnectionRef(),
            PrimaryScope: new AttachmentPrimaryScope(
                Kind: PrimaryScopeKinds.AdoWorkItem,
                WorkItemId: 1234,
                // URL points at a DIFFERENT organization — the exact "stolen .twig/
                // whose connectionRef forgery matches" shape §4.2.2 catches.
                WorkItemUrl: "https://dev.azure.com/some-other-org/some-other-project/_workitems/edit/1234",
                AttachedAt: DateTimeOffset.UtcNow.ToString("o")),
            ActiveClaim: null);
        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.AttachmentDocument));

        var store = NewStore();
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.AttachmentConnectionMismatch);
    }

    // ── Round-trip: initialize → write → read preserves the record ──────

    [Fact]
    public async Task Write_then_read_round_trip_preserves_active_claim_reference()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var mintedAt = new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var attachment = new PrimaryScopeAttachment(
            _ConnectionRef(),
            PrimaryScope: new PrimaryScope(42,
                AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 42),
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ActiveClaim: new ActiveClaimReference("claim-fixture", mintedAt));

        var store = NewStore();
        var write = await store.WriteAsync(attachment);
        write.IsSuccess.ShouldBeTrue(write.Error);

        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeTrue(read.Error);
        read.Value.ActiveClaim.ShouldNotBeNull();
        read.Value.ActiveClaim!.Value.ClaimId.ShouldBe("claim-fixture");
        read.Value.ActiveClaim!.Value.MintedAt.ShouldBe(mintedAt);
    }

    // ── Durability: atomic write leaves a rename-visible target ─────────

    [Fact]
    public async Task Atomic_write_produces_a_readable_target_and_removes_the_temp()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var attachment = new PrimaryScopeAttachment(
            _ConnectionRef(),
            PrimaryScope: new PrimaryScope(1,
                AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 1),
                DateTimeOffset.UtcNow),
            ActiveClaim: null);
        var store = NewStore();
        (await store.WriteAsync(attachment)).IsSuccess.ShouldBeTrue();

        var target = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        File.Exists(target).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(target);
        content.ShouldContain("\"workItemId\":1");

        var tmpDir = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.TmpDirName);
        Directory.EnumerateFiles(tmpDir).ShouldBeEmpty();
    }

    // ── Link/unlink: scope-match precondition ─────────────────────────

    [Fact]
    public async Task LinkClaim_refuses_when_stored_scope_kind_differs_from_caller_expected_kind()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var attachment = new PrimaryScopeAttachment(
            _ConnectionRef(),
            PrimaryScope: new PrimaryScope(50,
                AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 50),
                DateTimeOffset.UtcNow),
            ActiveClaim: null);
        var store = NewStore();
        (await store.WriteAsync(attachment)).IsSuccess.ShouldBeTrue();

        var link = await store.LinkClaimAsync("CLM-a", DateTimeOffset.UtcNow, "some-other-kind", 50, expectedRevision: -1);
        link.IsSuccess.ShouldBeFalse();
        link.Error.ShouldBe(AttachmentStorageFailure.AttachmentScopeMismatch);
    }

    [Fact]
    public async Task LinkClaim_refuses_when_stored_work_item_id_differs()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var attachment = new PrimaryScopeAttachment(
            _ConnectionRef(),
            PrimaryScope: new PrimaryScope(50,
                AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 50),
                DateTimeOffset.UtcNow),
            ActiveClaim: null);
        var store = NewStore();
        (await store.WriteAsync(attachment)).IsSuccess.ShouldBeTrue();

        var link = await store.LinkClaimAsync("CLM-a", DateTimeOffset.UtcNow, PrimaryScopeKinds.AdoWorkItem, expectedWorkItemId: 51, expectedRevision: -1);
        link.IsSuccess.ShouldBeFalse();
        link.Error.ShouldBe(AttachmentStorageFailure.AttachmentScopeMismatch);
    }

    // ── Read validation: malformed present blocks are named schema failures ─

    [Fact]
    public async Task Read_reports_checked_in_config_invalid_when_primary_scope_workitem_id_is_nonpositive()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var doc = new AttachmentDocument(
            AttachmentDocument.CurrentSchema, AttachmentDocument.CurrentVersion, Revision: 1,
            ConnectionRef: _ConnectionRef(),
            PrimaryScope: new AttachmentPrimaryScope(
                Kind: PrimaryScopeKinds.AdoWorkItem, WorkItemId: 0,
                WorkItemUrl: AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 1),
                AttachedAt: DateTimeOffset.UtcNow.ToString("o")),
            ActiveClaim: null);
        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.AttachmentDocument));

        var read = await NewStore().ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.CheckedInConfigInvalid);
    }

    [Fact]
    public async Task Read_reports_checked_in_config_invalid_when_primary_scope_attached_at_is_invalid()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var doc = new AttachmentDocument(
            AttachmentDocument.CurrentSchema, AttachmentDocument.CurrentVersion, Revision: 1,
            ConnectionRef: _ConnectionRef(),
            PrimaryScope: new AttachmentPrimaryScope(
                Kind: PrimaryScopeKinds.AdoWorkItem, WorkItemId: 42,
                WorkItemUrl: AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 42),
                AttachedAt: "not-a-timestamp"),
            ActiveClaim: null);
        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.AttachmentDocument));

        var read = await NewStore().ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.CheckedInConfigInvalid);
    }

    [Fact]
    public async Task Read_reports_checked_in_config_invalid_when_active_claim_minted_at_is_invalid()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();

        var doc = new AttachmentDocument(
            AttachmentDocument.CurrentSchema, AttachmentDocument.CurrentVersion, Revision: 1,
            ConnectionRef: _ConnectionRef(),
            PrimaryScope: null,
            ActiveClaim: new AttachmentActiveClaim("CLM-x", "not-a-timestamp"));
        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(doc, TwigJsonContext.Default.AttachmentDocument));

        var read = await NewStore().ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.CheckedInConfigInvalid);
    }

    // ── Revision bumps monotonically on every write, so concurrent
    //    writes observing the same on-disk revision surface as
    //    version-mismatch on the losing writer. ──────────────────────

    [Fact]
    public async Task Write_and_link_bump_revision_monotonically()
    {
        if (!_gitAvailable) return;
        await InitializeStoreAsync();
        var store = NewStore();

        var scope = new PrimaryScope(60, AdoWorkItemUrlValidator.BuildWorkItemUrl(_config.Organization, _config.Project, 60), DateTimeOffset.UtcNow);
        (await store.WriteAsync(new PrimaryScopeAttachment(_ConnectionRef(), scope, ActiveClaim: null))).IsSuccess.ShouldBeTrue();
        (await store.LinkClaimAsync("CLM-r1", DateTimeOffset.UtcNow, PrimaryScopeKinds.AdoWorkItem, 60, expectedRevision: -1)).IsSuccess.ShouldBeTrue();

        var attachmentPath = Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        var content = await File.ReadAllTextAsync(attachmentPath);
        // Initial init writes revision=0; the two follow-up writes bump to 1
        // then 2. Absolute values aren't the point — monotonic bump is.
        var doc = JsonSerializer.Deserialize(content, TwigJsonContext.Default.AttachmentDocument)!;
        doc.Revision.ShouldBeGreaterThanOrEqualTo(2);
    }

    private string _ConnectionRef() => ConnectionRefResolver.Compute(_config);

    private async Task InitializeStoreAsync()
    {
        var store = NewStore();
        var result = await store.InitializeAsync();
        result.IsSuccess.ShouldBeTrue(result.Error);
    }
}
