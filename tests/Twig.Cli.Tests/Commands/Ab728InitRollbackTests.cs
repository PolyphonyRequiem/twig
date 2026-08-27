using System.Diagnostics;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#728 §6.3 root-scoping + transaction-and-rollback behavior tests.
/// These fixtures exercise the InitCommand end-to-end against real
/// storage seams so a rollback observation is a real observation on the
/// filesystem, not a mock's out-parameter.
/// </summary>
public sealed class Ab728InitRollbackTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly IIterationService _iterationService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public Ab728InitRollbackTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"twig-728-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _iterationService = Substitute.For<IIterationService>();
        _iterationService.DetectTemplateNameAsync(Arg.Any<CancellationToken>()).Returns("Agile");
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>()).Returns(new ProcessConfigurationData());
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItemTypeAppearance>());

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── Fix (5): no-write invalid root ─────────────────────────────────

    [Fact]
    public async Task Init_refuses_and_writes_no_state_when_invocation_directory_is_not_a_git_worktree()
    {
        var nonGitDir = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(nonGitDir);
        // Deliberately DO NOT `git init` — the anchor detector must refuse.
        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(nonGitDir, ".twig"),
            Path.Combine(nonGitDir, ".twig", "config"),
            Path.Combine(nonGitDir, ".twig", "twig.db"),
            startDir: nonGitDir);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(1);
        Directory.Exists(Path.Combine(nonGitDir, ".twig")).ShouldBeFalse();
        File.Exists(Path.Combine(nonGitDir, "twig.json")).ShouldBeFalse();
        File.Exists(Path.Combine(nonGitDir, ".gitignore")).ShouldBeFalse();
    }

    // ── Fix (5): no-write nested-repo invocation ───────────────────────

    [Fact]
    public async Task Init_refuses_and_writes_no_state_when_invocation_directory_is_below_the_git_worktree_root()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return; // git unavailable
        var nested = Path.Combine(_tempRoot, "src", "nested");
        Directory.CreateDirectory(nested);

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(nested, ".twig"),
            Path.Combine(nested, ".twig", "config"),
            Path.Combine(nested, ".twig", "twig.db"),
            startDir: nested);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        // AB#728 §3.1 acceptance: init from a nested directory refuses
        // outright rather than initializing the ancestor's .twig/.
        result.ShouldBe(1);
        Directory.Exists(Path.Combine(nested, ".twig")).ShouldBeFalse();
        // The ancestor's .twig MUST NOT have been created either.
        Directory.Exists(Path.Combine(_tempRoot, ".twig")).ShouldBeFalse();
    }

    // ── Fix (1): selected-profile-unavailable is fatal ─────────────────

    [Fact]
    public async Task Init_returns_fatal_when_profile_registry_reports_selected_profile_unavailable_and_writes_no_managed_state()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        var (registry, _) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(_tempRoot, ".twig"),
            Path.Combine(_tempRoot, ".twig", "config"),
            Path.Combine(_tempRoot, ".twig", "cache", "twig.db"),
            startDir: _tempRoot);

        // Deliberately inject the UNAVAILABLE profile registry: the
        // fixture default supplies an explicit profile, but the review
        // acceptance requires that when the profile registry declines,
        // init aborts fatally without leaving managed state on disk.
        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, new UnavailableProfileRegistrySource(),
            _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(1);
        // The managed-init failure path rolls back everything the init run
        // created. layout.json / attachment.json MUST be absent.
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeFalse();
    }

    // ── Fix (4): rollback on system-registry failure ───────────────────

    [Fact]
    public async Task Init_rolls_back_created_local_files_when_system_registry_upsert_fails()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        var (_, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var failingRegistry = new FailingRegistry();
        var paths = new TwigPaths(
            Path.Combine(_tempRoot, ".twig"),
            Path.Combine(_tempRoot, ".twig", "config"),
            Path.Combine(_tempRoot, ".twig", "cache", "twig.db"),
            startDir: _tempRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            failingRegistry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(1);
        failingRegistry.UpsertConnectionCalled.ShouldBeTrue();
        // Rollback: files this run created MUST be gone; twig.json must
        // not remain either (there was nothing to preserve).
        File.Exists(Path.Combine(_tempRoot, "twig.json")).ShouldBeFalse();
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_restores_overwritten_twig_json_bytes_when_registry_upsert_fails_after_config_write()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        // Simulate an existing checked-in manifest whose bytes MUST be
        // preserved byte-for-byte if init aborts.
        var manifestPath = Path.Combine(_tempRoot, "twig.json");
        var originalManifestBytes = System.Text.Encoding.UTF8.GetBytes(
            "{\n  \"organization\": \"ExistingOrg\",\n  \"project\": \"ExistingProject\"\n}\n");
        await File.WriteAllBytesAsync(manifestPath, originalManifestBytes);
        // Track the manifest so the InitCommand's LoadSplitAsync recognises it.
        await RunGit("add", "--", "twig.json");
        await RunGit("-c", "user.email=t@example", "-c", "user.name=T", "commit", "-q", "-m", "seed");

        var (_, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var failingRegistry = new FailingRegistry();
        var paths = new TwigPaths(
            Path.Combine(_tempRoot, ".twig"),
            Path.Combine(_tempRoot, ".twig", "config"),
            Path.Combine(_tempRoot, ".twig", "cache", "twig.db"),
            startDir: _tempRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            failingRegistry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        // NOTE: coordinates match the tracked manifest so LoadSplit doesn't
        // reject before managed-init runs.
        var result = await cmd.ExecuteAsync("ExistingOrg", "ExistingProject");

        result.ShouldBe(1);
        failingRegistry.UpsertConnectionCalled.ShouldBeTrue();
        // Byte-for-byte restoration — the review's Fix #2 acceptance.
        (await File.ReadAllBytesAsync(manifestPath)).ShouldBe(originalManifestBytes);
    }

    // ── §6.3: pure input validation runs before the first write ────────

    [Fact]
    public async Task Init_refuses_invalid_sprint_expression_before_creating_any_local_state()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(_tempRoot, ".twig"),
            Path.Combine(_tempRoot, ".twig", "config"),
            Path.Combine(_tempRoot, ".twig", "cache", "twig.db"),
            startDir: _tempRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj", sprint: "@@not-an-expression");

        result.ShouldBe(1);
        // A rejected flag must not leave a half-built workspace behind:
        // no local layout, no manifest, no registry row to orphan.
        Directory.Exists(paths.TwigDir).ShouldBeFalse();
        File.Exists(Path.Combine(_tempRoot, "twig.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_refuses_invalid_area_before_reinitialize_archives_the_workspace()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        var markerPath = Path.Combine(twigDir, "marker.txt");
        await File.WriteAllTextAsync(markerPath, "pre-existing workspace\n");

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            twigDir,
            Path.Combine(twigDir, "config"),
            Path.Combine(twigDir, "cache", "twig.db"),
            startDir: _tempRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        // Empty segment — rejected by AreaPath.Parse.
        var result = await cmd.ExecuteAsync("org", "proj", area: "Project\\\\Team", reinitialize: true);

        result.ShouldBe(1);
        // --reinitialize renames .twig/ to .twig-legacy-<timestamp>/. Input
        // validation runs first, so a rejected flag leaves the workspace
        // exactly where it was — nothing archived, nothing to restore.
        File.Exists(markerPath).ShouldBeTrue();
        Directory.EnumerateDirectories(_tempRoot, ".twig-legacy-*").ShouldBeEmpty();
    }

    // ── §6.3 step 3: legacy refusal precedes step 4 ────────────────────

    [Fact]
    public async Task Init_refuses_legacy_layout_without_creating_current_shape_state()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);
        // Design §7 predicate #2: a flat pre-T1 twig.db at the workspace root.
        var legacyDbPath = Path.Combine(twigDir, "twig.db");
        var legacyBytes = System.Text.Encoding.UTF8.GetBytes("legacy-pre-t1-cache");
        await File.WriteAllBytesAsync(legacyDbPath, legacyBytes);

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            twigDir,
            Path.Combine(twigDir, "config"),
            Path.Combine(twigDir, "cache", "twig.db"),
            startDir: _tempRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        _iterationService.ClearReceivedCalls();
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(1);
        // Ordering, not just outcome: the refusal must land before step 4
        // starts any initialization work. Template detection is the first
        // such step, so a late check (one that relies on rollback to undo
        // its writes) fails here even though the filesystem ends up clean.
        await _iterationService.DidNotReceive().DetectTemplateNameAsync(Arg.Any<CancellationToken>());
        (await File.ReadAllBytesAsync(legacyDbPath)).ShouldBe(legacyBytes);
        // Nothing of the current shape may appear alongside the legacy tree.
        File.Exists(Path.Combine(twigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
        File.Exists(Path.Combine(twigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeFalse();
        File.Exists(paths.ConfigPath).ShouldBeFalse();
        File.Exists(Path.Combine(_tempRoot, "twig.json")).ShouldBeFalse();
    }

    // ── §3.1: a linked worktree root is a valid init target ────────────

    [Fact]
    public async Task Init_succeeds_from_a_linked_worktree_root_where_dot_git_is_a_file()
    {
        if (!InitCommandTestFixture.InitTempWorktree(_tempRoot)) return;
        // A linked worktree needs at least one commit in the parent repo.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "seed.txt"), "seed\n");
        await RunGit("add", "--", "seed.txt");
        await RunGit("-c", "user.email=t@example", "-c", "user.name=T", "commit", "-q", "-m", "seed");
        var linkedRoot = Path.Combine(_tempRoot, "linked");
        await RunGit("worktree", "add", "-q", "-b", "linked-branch", linkedRoot);
        // The defining property of a linked worktree: .git is a FILE.
        File.Exists(Path.Combine(linkedRoot, ".git")).ShouldBeTrue();

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(linkedRoot, ".twig"),
            Path.Combine(linkedRoot, ".twig", "config"),
            Path.Combine(linkedRoot, ".twig", "cache", "twig.db"),
            startDir: linkedRoot);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(0);
        File.Exists(Path.Combine(linkedRoot, ".twig", WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
        File.Exists(paths.DbPath).ShouldBeTrue();
        // State belongs to the linked worktree, never the parent checkout.
        Directory.Exists(Path.Combine(_tempRoot, ".twig")).ShouldBeFalse();
    }

    // ── §3.1: a root reached through a symlinked ancestor is still the root

    [Fact]
    public async Task Init_succeeds_when_the_worktree_root_is_reached_through_a_symlinked_ancestor()
    {
        var realParent = Path.Combine(_tempRoot, "real");
        var repoRoot = Path.Combine(realParent, "repo");
        Directory.CreateDirectory(repoRoot);
        if (!InitCommandTestFixture.InitTempWorktree(repoRoot)) return;

        // macOS reaches every temp path this way: /var is a symlink to
        // /private/var, so the ANCESTOR is the link, not the leaf. git
        // reports the resolved root while the runtime keeps the link path.
        var linkParent = Path.Combine(_tempRoot, "link");
        Directory.CreateSymbolicLink(linkParent, realParent);
        var rootViaLink = Path.Combine(linkParent, "repo");

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(rootViaLink, ".twig"),
            Path.Combine(rootViaLink, ".twig", "config"),
            Path.Combine(rootViaLink, ".twig", "cache", "twig.db"),
            startDir: rootViaLink);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        result.ShouldBe(0);
        File.Exists(Path.Combine(repoRoot, ".twig", WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task Init_still_refuses_a_subdirectory_reached_through_a_symlinked_ancestor()
    {
        var realParent = Path.Combine(_tempRoot, "real");
        var repoRoot = Path.Combine(realParent, "repo");
        Directory.CreateDirectory(repoRoot);
        if (!InitCommandTestFixture.InitTempWorktree(repoRoot)) return;
        Directory.CreateDirectory(Path.Combine(repoRoot, "src"));

        var linkParent = Path.Combine(_tempRoot, "link");
        Directory.CreateSymbolicLink(linkParent, realParent);
        var nestedViaLink = Path.Combine(linkParent, "repo", "src");

        var (registry, profile) = InitCommandTestFixture.CreateSeams(_tempRoot);
        var paths = new TwigPaths(
            Path.Combine(nestedViaLink, ".twig"),
            Path.Combine(nestedViaLink, ".twig", "config"),
            Path.Combine(nestedViaLink, ".twig", "cache", "twig.db"),
            startDir: nestedViaLink);

        var cmd = InitCommandTestFixture.CreateInitCommand(
            registry, profile, _iterationService, paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("org", "proj");

        // Resolving symlinks must not weaken the refusal it enables.
        result.ShouldBe(1);
        Directory.Exists(Path.Combine(nestedViaLink, ".twig")).ShouldBeFalse();
        Directory.Exists(Path.Combine(repoRoot, ".twig")).ShouldBeFalse();
    }

    private async Task RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _tempRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) return;
        await proc.WaitForExitAsync();
    }

    /// <summary>
    /// Injection stand-in for <see cref="ISystemWorktreeRegistry"/> that
    /// always fails the first mutating call. AB#728 §6.3 acceptance: a
    /// registry failure at step 10 rolls back every artifact created in
    /// steps 4–9 and restores overwritten files byte-for-byte.
    /// </summary>
    private sealed class FailingRegistry : ISystemWorktreeRegistry
    {
        public bool UpsertConnectionCalled { get; private set; }

        public Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default)
        {
            UpsertConnectionCalled = true;
            return Task.FromResult(Result.Fail("simulated-registry-failure"));
        }

        public Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default)
            => Task.FromResult(Result.Ok<SystemWorktreeRow?>(null));
        public Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
            => Task.FromResult(Result.Fail("simulated-registry-failure"));
        public Task<Result> InsertClaimAsync(string claimId, string connectionRef, string worktreeFingerprint, string primaryScopeKind, int workItemId, string state, string casToken, string recordJson, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> UpdateClaimStateAsync(string claimId, string expectedCasToken, string newCasToken, string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, string primaryScopeKind, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemClaimRow?>(null));
        public Task<Result<IReadOnlyList<SystemClaimRow>>> FindClaimsForTupleAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default) => Task.FromResult(Result.Ok<IReadOnlyList<SystemClaimRow>>(Array.Empty<SystemClaimRow>()));
        public Task<Result> SupersedeAndActivateClaimAsync(string newClaimId, string newCasToken, string connectionRef, string worktreeFingerprint, string primaryScopeKind, int workItemId, string newRecordJson, string predecessorClaimId, string predecessorExpectedCasToken, string predecessorNewCasToken, string predecessorRecordJson, DateTimeOffset transitionAt, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default) => Task.FromResult(Result.Ok<SystemProfileCacheRow?>(null));
        public Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<long>> ReserveTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default) => Task.FromResult(Result.Ok(0L));
        public Task<Result> CommitTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, long expectedEpoch, string winningClaimId, string winningCasToken, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result<TupleEpochRow>> GetTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default) => Task.FromResult(Result.Ok(new TupleEpochRow(0, null, null)));
        public Task<Result> ActivateClaimAndCommitEpochAsync(string claimId, string expectedCasToken, string newCasToken, DateTimeOffset activatedAt, string recordJson, string connectionRef, string primaryScopeKind, int workItemId, long expectedEpoch, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> SupersedeAndActivateClaimAndCommitEpochAsync(string newClaimId, string newCasToken, string connectionRef, string worktreeFingerprint, string primaryScopeKind, int workItemId, string newRecordJson, string predecessorClaimId, string predecessorExpectedCasToken, string predecessorNewCasToken, string predecessorRecordJson, DateTimeOffset transitionAt, long expectedEpoch, CancellationToken ct = default) => Task.FromResult(Result.Ok());
        public Task<Result> TerminalizeClaimAndCommitEpochAsync(string claimId, string expectedCasToken, string newCasToken, DateTimeOffset endedAt, string recordJson, string connectionRef, string primaryScopeKind, int workItemId, long expectedEpoch, CancellationToken ct = default) => Task.FromResult(Result.Ok());
    }
}
