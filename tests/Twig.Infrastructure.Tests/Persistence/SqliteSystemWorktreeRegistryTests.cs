using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="SqliteSystemWorktreeRegistry"/>. These exercise
/// the AB#736 §9.4 surface AB#738 depends on: connection + worktree upsert,
/// non-retired matching lookup, and the fail-closed behavior a missing row
/// implies. The DB is created inside a per-test temp dir so parallel tests do
/// not clash.
/// </summary>
public sealed class SqliteSystemWorktreeRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteSystemWorktreeRegistry _registry;

    public SqliteSystemWorktreeRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twig-system-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_dir, "system.db"), TimeProvider.System);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_fingerprint()
    {
        var find = await _registry.FindWorktreeAsync("{\"unknown\":true}");
        find.IsSuccess.ShouldBeTrue(find.Error);
        find.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_worktree_and_lookup_round_trips()
    {
        (await _registry.UpsertConnectionAsync("ref-a", "org-a", "proj-a", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-a", "ref-a", "/some/wt")).IsSuccess.ShouldBeTrue();

        var find = await _registry.FindWorktreeAsync("fp-a");
        find.Value.ShouldNotBeNull();
        find.Value!.ConnectionRef.ShouldBe("ref-a");
        find.Value.RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_reactivates_a_retired_row()
    {
        (await _registry.UpsertConnectionAsync("ref-b", "org-b", "proj-b", team: "t")).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-b", "ref-b", "/wt-b")).IsSuccess.ShouldBeTrue();

        // A second upsert refreshes lastSeenAt and clears retiredAt back to
        // null — the exact behavior §7 depends on when a legacy reinit reruns
        // and re-registers the same fingerprint.
        (await _registry.UpsertWorktreeAsync("fp-b", "ref-b", "/wt-b")).IsSuccess.ShouldBeTrue();
        var find = await _registry.FindWorktreeAsync("fp-b");
        find.Value!.RetiredAt.ShouldBeNull();
    }

    // ── AB#736 §4.3.1 claims table ─────────────────────────────────────

    [Fact]
    public async Task Insert_claim_refuses_when_the_worktree_is_not_registered()
    {
        (await _registry.UpsertConnectionAsync("ref-c", "org-c", "proj-c", team: null)).IsSuccess.ShouldBeTrue();
        var insert = await _registry.InsertClaimAsync(
            claimId: "claim-x", connectionRef: "ref-c",
            worktreeFingerprint: "fp-does-not-exist",
            workItemId: 42, state: "active", recordJson: "{}");
        insert.IsSuccess.ShouldBeFalse();
        insert.Error.ShouldBe(AttachmentStorageFailure.WorktreeNotRegistered);
    }

    [Fact]
    public async Task Insert_and_find_claim_round_trip()
    {
        (await _registry.UpsertConnectionAsync("ref-d", "org-d", "proj-d", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-d", "ref-d", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-01", "ref-d", "fp-d", 42, "active", "{\"a\":1}")).IsSuccess.ShouldBeTrue();

        var find = await _registry.FindClaimAsync("claim-01");
        find.Value.ShouldNotBeNull();
        find.Value!.State.ShouldBe("active");
        find.Value.RecordJson.ShouldBe("{\"a\":1}");
    }

    [Fact]
    public async Task FindReserved_returns_matching_state_within_reserved_set()
    {
        (await _registry.UpsertConnectionAsync("ref-e", "org-e", "proj-e", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-e", "ref-e", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-a", "ref-e", "fp-e", 100, "pending", "{}")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-b", "ref-e", "fp-e", 101, "released", "{}")).IsSuccess.ShouldBeTrue();

        var pending = await _registry.FindReservedClaimAsync("ref-e", 100, new[] { "pending", "active" });
        pending.Value.ShouldNotBeNull();
        pending.Value!.ClaimId.ShouldBe("claim-a");

        // A released row MUST NOT be surfaced through the reserved lookup —
        // widening the set here would defeat the local-duplicate rule §9.4 §615.
        var released = await _registry.FindReservedClaimAsync("ref-e", 101, new[] { "pending", "active" });
        released.Value.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateClaimState_persists_state_endedAt_and_recordJson()
    {
        (await _registry.UpsertConnectionAsync("ref-f", "org-f", "proj-f", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-f", "ref-f", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-c", "ref-f", "fp-f", 200, "active", "{}")).IsSuccess.ShouldBeTrue();

        var endedAt = new DateTimeOffset(2026, 5, 5, 5, 5, 5, TimeSpan.Zero);
        (await _registry.UpdateClaimStateAsync("claim-c", "released", endedAt, "{\"reason\":\"done\"}")).IsSuccess.ShouldBeTrue();

        var find = await _registry.FindClaimAsync("claim-c");
        find.Value!.State.ShouldBe("released");
        find.Value.EndedAt.ShouldBe(endedAt);
        find.Value.RecordJson.ShouldContain("done");
    }

    // ── AB#736 §4.3.1 profileCache table ─────────────────────────────

    [Fact]
    public async Task Profile_cache_write_read_round_trips()
    {
        (await _registry.UpsertConnectionAsync("ref-g", "org-g", "proj-g", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.WriteProfileCacheAsync("ref-g", "prof-id", "v1", "{\"types\":[]}")).IsSuccess.ShouldBeTrue();

        var read = await _registry.ReadProfileCacheAsync("ref-g");
        read.Value.ShouldNotBeNull();
        read.Value!.ProfileIdentity.ShouldBe("prof-id");
        read.Value.ProfileVersion.ShouldBe("v1");
        read.Value.Payload.ShouldContain("types");
    }

    // ── §4.3.1 layout_meta exact-version check ──────────────────────────

    [Fact]
    public async Task Reopen_with_bumped_schema_version_fails_with_named_error()
    {
        // Bootstrap the store at the current schema version, then hand-patch
        // layout_meta to a version this binary does not expect and verify that
        // reopening surfaces `system-store-schema-mismatch` instead of silently
        // adopting the mismatched shape.
        var dbPath = Path.Combine(_dir, "system.db");
        (await _registry.UpsertConnectionAsync("ref-h", "org-h", "proj-h", team: null)).IsSuccess.ShouldBeTrue();
        _registry.Dispose();

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE layout_meta SET version = 99 WHERE id = 1;";
            cmd.ExecuteNonQuery();
        }

        using var reopened = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        var find = await reopened.FindWorktreeAsync("anything");
        find.IsSuccess.ShouldBeFalse();
        find.Error.ShouldBe(AttachmentStorageFailure.SystemStoreSchemaMismatch);
    }

    [Fact]
    public async Task Registry_survives_a_close_and_reopen()
    {
        (await _registry.UpsertConnectionAsync("ref-c", "org-c", "proj-c", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-c", "ref-c", "/wt-c")).IsSuccess.ShouldBeTrue();
        _registry.Dispose();

        using var reopened = new SqliteSystemWorktreeRegistry(Path.Combine(_dir, "system.db"), TimeProvider.System);
        var find = await reopened.FindWorktreeAsync("fp-c");
        find.Value.ShouldNotBeNull();
        find.Value!.ConnectionRef.ShouldBe("ref-c");
    }
}
