using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services.Claims;
using Xunit;

namespace Twig.Infrastructure.Tests;

/// <summary>
/// AB#728 final-review defense-in-depth tests. Each case pins one of the
/// review findings so a regression fails a specific-named assertion rather
/// than a vague behavioral drift.
/// </summary>
public sealed class Ab728FinalReviewTests : IDisposable
{
    private readonly string _tempRoot;

    public Ab728FinalReviewTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "twig-728-final-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── Fix #5: cross-connection claim insert is refused ───────────────

    [Fact]
    public async Task InsertClaim_refuses_when_fingerprint_belongs_to_a_different_connection()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        (await registry.UpsertConnectionAsync("conn-A", "orgA", "projA", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertConnectionAsync("conn-B", "orgB", "projB", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync("fp-A", "conn-A", "/tmp/a")).IsSuccess.ShouldBeTrue();

        var insert = await registry.InsertClaimAsync(
            claimId: "CLM-cross",
            connectionRef: "conn-B",
            worktreeFingerprint: "fp-A",
            primaryScopeKind: PrimaryScopeKinds.AdoWorkItem,
            workItemId: 1,
            state: ClaimStates.Pending,
            casToken: "cas-1",
            recordJson: "{}");
        insert.IsSuccess.ShouldBeFalse();
        insert.Error.ShouldBe(AttachmentStorageFailure.AttachmentConnectionMismatch);
    }

    [Fact]
    public async Task InsertClaim_refuses_when_worktree_row_is_retired()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        (await registry.UpsertConnectionAsync("conn-A", "orgA", "projA", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync("fp-A", "conn-A", "/tmp/a")).IsSuccess.ShouldBeTrue();

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE worktrees SET retired_at = $ts WHERE worktree_fingerprint = 'fp-A';";
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var insert = await registry.InsertClaimAsync(
            claimId: "CLM-retired",
            connectionRef: "conn-A",
            worktreeFingerprint: "fp-A",
            primaryScopeKind: PrimaryScopeKinds.AdoWorkItem,
            workItemId: 1,
            state: ClaimStates.Pending,
            casToken: "cas-1",
            recordJson: "{}");
        insert.IsSuccess.ShouldBeFalse();
        insert.Error.ShouldBe(AttachmentStorageFailure.WorktreeRetired);
    }

    // ── Fix #7: versioned local files fail closed on unknown schema/version ─

    [Fact]
    public async Task Attachment_read_rejects_newer_document_version()
    {
        var setup = await InitializeManagedStoreAsync();
        if (setup is null) return; // git unavailable
        var (paths, config) = setup.Value;
        var attachmentPath = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        var futureDoc = new AttachmentDocument(
            Schema: "twig-attachment/v99",
            Version: 99,
            Revision: 0,
            ConnectionRef: ConnectionRefResolver.Compute(config),
            PrimaryScope: null,
            ActiveClaim: null);
        await File.WriteAllTextAsync(attachmentPath, JsonSerializer.Serialize(futureDoc, TwigJsonContext.Default.AttachmentDocument));

        var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System);
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.CheckedInConfigInvalid);
    }

    [Fact]
    public async Task Layout_read_rejects_newer_marker_version()
    {
        var setup = await InitializeManagedStoreAsync();
        if (setup is null) return;
        var (paths, config) = setup.Value;
        var layoutPath = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName);
        var futureMarker = new LayoutMarkerDocument(
            Schema: "twig-layout/v99",
            Version: 99,
            InitializedAt: DateTimeOffset.UtcNow.ToString("o"),
            CreatedBy: "future-tool");
        await File.WriteAllTextAsync(layoutPath, JsonSerializer.Serialize(futureMarker, TwigJsonContext.Default.LayoutMarkerDocument));

        var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System);
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.LayoutMarkerMissing);
    }

    [Fact]
    public async Task Fingerprint_read_rejects_newer_marker_version()
    {
        var setup = await InitializeManagedStoreAsync();
        if (setup is null) return;
        var (paths, config) = setup.Value;
        var wtPath = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName);
        var existing = await File.ReadAllTextAsync(wtPath);
        var live = JsonSerializer.Deserialize(existing, TwigJsonContext.Default.WorktreeFingerprintDocument)!;
        var futureDoc = new WorktreeFingerprintDocument(
            Schema: "twig-worktree/v99",
            Version: 99,
            WorktreeFingerprint: live.WorktreeFingerprint);
        await File.WriteAllTextAsync(wtPath, JsonSerializer.Serialize(futureDoc, TwigJsonContext.Default.WorktreeFingerprintDocument));

        var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System);
        var read = await store.ReadAsync();
        read.IsSuccess.ShouldBeFalse();
        read.Error.ShouldBe(AttachmentStorageFailure.WorktreeFingerprintDrift);
    }

    // ── Fix #3: legacy predicates run independently even when a marker exists ─

    [Fact]
    public async Task Legacy_layout_is_detected_even_when_current_marker_exists()
    {
        var setup = await InitializeManagedStoreAsync();
        if (setup is null) return;
        var (paths, _) = setup.Value;
        var legacyNested = Path.Combine(paths.TwigDir, "legacy-org", "legacy-project");
        Directory.CreateDirectory(legacyNested);
        await File.WriteAllBytesAsync(Path.Combine(legacyNested, "twig.db"), new byte[] { 0 });

        LegacyLayoutDetector.IsLegacyLayoutPresent(paths.TwigDir).ShouldBeTrue();
        LegacyLayoutDetector.HasNestedLegacyDb(paths.TwigDir).ShouldBeTrue();
        LegacyLayoutDetector.HasFlatLegacyDb(paths.TwigDir).ShouldBeFalse();
    }

    [Fact]
    public async Task Legacy_flat_db_is_detected_even_when_current_marker_exists()
    {
        var setup = await InitializeManagedStoreAsync();
        if (setup is null) return;
        var (paths, _) = setup.Value;
        await File.WriteAllBytesAsync(Path.Combine(paths.TwigDir, "twig.db"), new byte[] { 0 });

        LegacyLayoutDetector.HasFlatLegacyDb(paths.TwigDir).ShouldBeTrue();
        LegacyLayoutDetector.IsLegacyLayoutPresent(paths.TwigDir).ShouldBeTrue();
    }

    [Fact]
    public void ArchiveLegacyLayout_atomically_renames_and_disambiguates_on_repeat()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "residue.txt"), "old");

        var clock = new FrozenTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var first = LegacyLayoutDetector.ArchiveLegacyLayout(twigDir, clock);
        first.IsSuccess.ShouldBeTrue(first.Error);
        Directory.Exists(twigDir).ShouldBeFalse();
        Directory.Exists(first.Value).ShouldBeTrue();
        File.Exists(Path.Combine(first.Value, "residue.txt")).ShouldBeTrue();

        Directory.CreateDirectory(twigDir);
        File.WriteAllText(Path.Combine(twigDir, "residue.txt"), "newer");
        var second = LegacyLayoutDetector.ArchiveLegacyLayout(twigDir, clock);
        second.IsSuccess.ShouldBeTrue(second.Error);
        second.Value.ShouldNotBe(first.Value);
        Directory.Exists(first.Value).ShouldBeTrue();
        Directory.Exists(second.Value).ShouldBeTrue();
    }

    [Fact]
    public void ArchiveLegacyLayout_missing_source_is_success_with_empty_target()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist", ".twig");
        var res = LegacyLayoutDetector.ArchiveLegacyLayout(missing, TimeProvider.System);
        res.IsSuccess.ShouldBeTrue();
        res.Value.ShouldBe(string.Empty);
    }

    // ── Fix #10: claim label validation ─────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("normal label")]
    [InlineData("mixed 日本語 emoji 🚀")]
    public void ValidateClaimLabel_accepts_normal_labels(string? label)
    {
        LocalClaimService.ValidateClaimLabel(label, out _).ShouldBeTrue();
    }

    [Fact]
    public void ValidateClaimLabel_rejects_control_and_newline_characters()
    {
        LocalClaimService.ValidateClaimLabel("line1\nline2", out var reason).ShouldBeFalse();
        reason.ShouldContain("control");

        LocalClaimService.ValidateClaimLabel("tab\there", out reason).ShouldBeFalse();
        reason.ShouldContain("control");
    }

    [Fact]
    public void ValidateClaimLabel_boundary_is_200_code_points()
    {
        LocalClaimService.ValidateClaimLabel(new string('a', 200), out _).ShouldBeTrue();
        LocalClaimService.ValidateClaimLabel(new string('a', 201), out var reason).ShouldBeFalse();
        reason.ShouldContain("200");
    }

    [Fact]
    public void ValidateClaimLabel_counts_grapheme_clusters_not_utf16_units()
    {
        var pair = "\uD83D\uDE80"; // rocket emoji
        LocalClaimService.ValidateClaimLabel(string.Concat(Enumerable.Repeat(pair, 200)), out _).ShouldBeTrue();
        LocalClaimService.ValidateClaimLabel(string.Concat(Enumerable.Repeat(pair, 201)), out _).ShouldBeFalse();
    }

    // ── Fix #4: XDG_STATE_HOME support and system tier layout ─────────

    [Fact]
    public void SystemStoreLayout_creates_marker_layout_and_tmp_directory()
    {
        var systemRoot = Path.Combine(_tempRoot, "system-root");
        SystemStoreLayout.EnsureRoot(systemRoot, TimeProvider.System);

        Directory.Exists(systemRoot).ShouldBeTrue();
        Directory.Exists(Path.Combine(systemRoot, SystemStoreLayout.TmpDirName)).ShouldBeTrue();
        var markerPath = Path.Combine(systemRoot, SystemStoreLayout.LayoutFileName);
        File.Exists(markerPath).ShouldBeTrue();
        var marker = JsonSerializer.Deserialize(
            File.ReadAllText(markerPath), TwigJsonContext.Default.LayoutMarkerDocument)!;
        marker.Schema.ShouldBe(LayoutMarkerDocument.CurrentSchema);
        marker.Version.ShouldBe(LayoutMarkerDocument.CurrentVersion);
    }

    [Fact]
    public void SystemStoreLayout_preserves_existing_marker_bytes_on_repeat()
    {
        var systemRoot = Path.Combine(_tempRoot, "system-root");
        SystemStoreLayout.EnsureRoot(systemRoot, TimeProvider.System);
        var markerPath = Path.Combine(systemRoot, SystemStoreLayout.LayoutFileName);
        var initialBytes = File.ReadAllBytes(markerPath);

        SystemStoreLayout.EnsureRoot(systemRoot, TimeProvider.System);
        File.ReadAllBytes(markerPath).ShouldBe(initialBytes);
    }

    // ── Fix #6: mint LocalCommitted phase after atomic activation ──────

    [Fact]
    public async Task Mint_persists_active_row_after_atomic_activation_and_commit_phase()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        (await registry.UpsertConnectionAsync("conn", "org", "proj", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync("fp", "conn", "/tmp/wt")).IsSuccess.ShouldBeTrue();

        var svc = new LocalClaimService(registry, new TrivialAttachmentStore(42),
            new StaticIdGenerator("CLM-1"), new SequenceCasGenerator(),
            new StaticHolderResolver(new ClaimHolderDescriptor("holder", "Holder Display")),
            TimeProvider.System);

        var input = new MintClaimInput(
            "conn", PrimaryScopeKinds.AdoWorkItem, "42", "fp", "holder", "Holder Display",
            Label: "ok", Notes: null, AdoProjection: new PassThroughAdoProjection());

        var outcome = await svc.MintAsync(input);
        outcome.ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var row = (await registry.FindClaimAsync("CLM-1")).Value!;
        row.State.ShouldBe(ClaimStates.Active);
    }

    // ── Fix #10: mint rejects an invalid label before any write ─────

    [Fact]
    public async Task Mint_returns_invalid_request_for_control_character_label_and_writes_nothing()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        (await registry.UpsertConnectionAsync("conn", "org", "proj", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync("fp", "conn", "/tmp/wt")).IsSuccess.ShouldBeTrue();

        var svc = new LocalClaimService(registry, new TrivialAttachmentStore(42),
            new StaticIdGenerator("CLM-x"), new SequenceCasGenerator(),
            new StaticHolderResolver(new ClaimHolderDescriptor("holder", "H")),
            TimeProvider.System);

        var input = new MintClaimInput(
            "conn", PrimaryScopeKinds.AdoWorkItem, "42", "fp", "holder", "H",
            Label: "bad\nlabel", Notes: null, AdoProjection: new PassThroughAdoProjection());

        var outcome = await svc.MintAsync(input);
        outcome.ShouldBeOfType<ClaimMintOutcome.InvalidRequest>();
        (await registry.FindClaimAsync("CLM-x")).Value.ShouldBeNull();
    }

    // ── Fix #10: persisted invalid label surfaces as SchemaDrift ────

    [Fact]
    public async Task Validate_rejects_persisted_row_with_control_character_label_as_schema_drift()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        (await registry.UpsertConnectionAsync("conn", "org", "proj", null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync("fp", "conn", "/tmp/wt")).IsSuccess.ShouldBeTrue();

        var doc = new ClaimRecordDocument(
            SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
            ClaimId: "CLM-forged",
            Label: "line1\nline2",
            ConnectionRef: "conn",
            PrimaryScopeId: "42",
            PrimaryScopeKind: PrimaryScopeKinds.AdoWorkItem,
            HolderIdentity: "holder",
            HolderDisplay: null,
            WorktreeFingerprint: "fp",
            State: ClaimStates.Active,
            Origin: ClaimOrigins.Local,
            LeaseGeneration: 0,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("o"),
            ActivatedAt: DateTimeOffset.UtcNow.ToString("o"),
            ReleasedAt: null,
            SupersededByClaimId: null,
            ReleaseReason: null,
            Notes: null,
            CasToken: "cas-1");
        var json = JsonSerializer.Serialize(doc, TwigJsonContext.Default.ClaimRecordDocument);
        (await registry.InsertClaimAsync(
            claimId: "CLM-forged",
            connectionRef: "conn",
            worktreeFingerprint: "fp",
            primaryScopeKind: PrimaryScopeKinds.AdoWorkItem,
            workItemId: 42,
            state: ClaimStates.Active,
            casToken: "cas-1",
            recordJson: json)).IsSuccess.ShouldBeTrue();

        var svc = new LocalClaimService(registry, new TrivialAttachmentStore(42),
            new StaticIdGenerator("unused"), new SequenceCasGenerator(),
            new StaticHolderResolver(new ClaimHolderDescriptor("holder", "H")),
            TimeProvider.System);

        var validate = await svc.ValidateAsync(new ClaimValidationInput(
            "CLM-forged", "conn", PrimaryScopeKinds.AdoWorkItem, "42"));
        validate.ShouldBeOfType<ClaimValidationOutcome.SchemaDrift>();
    }

    // ── Fix #10: label update rejects invalid labels ───────────────────

    [Fact]
    public async Task UpdateLabel_returns_invalid_request_for_too_long_label()
    {
        var dbPath = Path.Combine(_tempRoot, "system.db");
        using var registry = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);

        var svc = new LocalClaimService(registry, new TrivialAttachmentStore(42),
            new StaticIdGenerator("CLM-1"), new SequenceCasGenerator(),
            new StaticHolderResolver(new ClaimHolderDescriptor("h", "H")),
            TimeProvider.System);

        var outcome = await svc.UpdateLabelAsync(new UpdateClaimLabelInput(
            "CLM-1", NewLabel: new string('a', 201), ExpectedCasToken: "cas"));
        outcome.ShouldBeOfType<ClaimLabelUpdateOutcome.InvalidRequest>();
    }

    // ── Test helpers ───────────────────────────────────────────────────

    private async Task<(TwigPaths Paths, TwigConfiguration Config)?> InitializeManagedStoreAsync()
    {
        var workDir = Path.Combine(_tempRoot, "workdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var twigDir = Path.Combine(workDir, ".twig");
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "cache", "twig.db"), workDir);
        var config = new TwigConfiguration { Organization = "org", Project = "proj" };
        File.WriteAllText(Path.Combine(workDir, "twig.json"), "{\n}\n");
        var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        });
        git?.WaitForExit(5_000);
        if (git is null || git.ExitCode != 0) return null;

        var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System);
        (await store.InitializeAsync()).IsSuccess.ShouldBeTrue();
        return (paths, config);
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FrozenTimeProvider(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TrivialAttachmentStore : Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore
    {
        private PrimaryScopeAttachment _current;
        private long _revision;

        public TrivialAttachmentStore(int defaultWorkItemId)
        {
            var scope = new PrimaryScope(defaultWorkItemId, $"https://dev.azure.com/org/proj/_workitems/edit/{defaultWorkItemId}", DateTimeOffset.UtcNow);
            _current = new PrimaryScopeAttachment("conn", scope, ActiveClaim: null);
        }

        public bool IsManagedWorktree() => true;
        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok(_current));
        public Task<Result<VersionedPrimaryScopeAttachment>> ReadWithRevisionAsync(CancellationToken ct = default)
            => Task.FromResult(Result.Ok(new VersionedPrimaryScopeAttachment(_current, _revision)));
        public Task<Result> WriteAsync(PrimaryScopeAttachment attachment, long expectedRevision = -1, CancellationToken ct = default)
        {
            _current = attachment; _revision++;
            return Task.FromResult(Result.Ok());
        }
        public Task<Result> LinkClaimAsync(string claimId, DateTimeOffset mintedAt, string expectedPrimaryScopeKind, int expectedWorkItemId, long expectedRevision, CancellationToken ct = default)
        {
            _current = _current with { ActiveClaim = new ActiveClaimReference(claimId, mintedAt) };
            _revision++;
            return Task.FromResult(Result.Ok());
        }
        public Task<Result> UnlinkClaimAsync(string expectedClaimId, long expectedRevision, CancellationToken ct = default)
        {
            _current = _current with { ActiveClaim = null };
            _revision++;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class StaticIdGenerator : IClaimIdGenerator
    {
        private readonly string _id;
        public StaticIdGenerator(string id) { _id = id; }
        public string NewClaimId() => _id;
    }

    private sealed class SequenceCasGenerator : IClaimCasTokenGenerator
    {
        private int _n;
        public string NewCasToken() => $"cas-{++_n}";
    }

    private sealed class StaticHolderResolver : IClaimHolderResolver
    {
        private readonly ClaimHolderDescriptor _holder;
        public StaticHolderResolver(ClaimHolderDescriptor holder) { _holder = holder; }
        public Task<Result<ClaimHolderDescriptor>> ResolveAsync(CancellationToken ct = default) => Task.FromResult(Result.Ok(_holder));
    }

    private sealed class PassThroughAdoProjection : IAdoClaimProjection
    {
        public Task<Result> ProjectHolderAsync(string primaryScopeId, ClaimHolderDescriptor holder, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> ClearHolderAsync(string primaryScopeId, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<ClaimHolderDescriptor?>> ReadHolderAsync(string primaryScopeId, CancellationToken ct = default) => Task.FromResult(Result.Ok<ClaimHolderDescriptor?>(null));
    }
}
