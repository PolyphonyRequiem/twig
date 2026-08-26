using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteSystemWorktreeRegistryTests : IDisposable
{
    private const string Kind = PrimaryScopeKinds.AdoWorkItem;

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
    public async Task Insert_claim_refuses_when_the_worktree_is_not_registered()
    {
        (await _registry.UpsertConnectionAsync("ref-c", "org-c", "proj-c", team: null)).IsSuccess.ShouldBeTrue();
        var insert = await _registry.InsertClaimAsync(
            claimId: "claim-x", connectionRef: "ref-c",
            worktreeFingerprint: "fp-does-not-exist",
            primaryScopeKind: Kind,
            workItemId: 42, state: "active", casToken: "tok0", recordJson: "{}");
        insert.IsSuccess.ShouldBeFalse();
        insert.Error.ShouldBe(AttachmentStorageFailure.WorktreeNotRegistered);
    }

    [Fact]
    public async Task Insert_and_find_claim_round_trip()
    {
        (await _registry.UpsertConnectionAsync("ref-d", "org-d", "proj-d", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-d", "ref-d", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-01", "ref-d", "fp-d", Kind, 42, "active", "tok0", "{\"a\":1}")).IsSuccess.ShouldBeTrue();

        var find = await _registry.FindClaimAsync("claim-01");
        find.Value.ShouldNotBeNull();
        find.Value!.State.ShouldBe("active");
        find.Value.CasToken.ShouldBe("tok0");
        find.Value.PrimaryScopeKind.ShouldBe(Kind);
    }

    [Fact]
    public async Task Partial_unique_index_refuses_a_second_reserved_claim_for_the_same_work_item()
    {
        (await _registry.UpsertConnectionAsync("ref-u", "org-u", "proj-u", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-u", "ref-u", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-a", "ref-u", "fp-u", Kind, 500, "active", "tok0", "{}")).IsSuccess.ShouldBeTrue();

        var dup = await _registry.InsertClaimAsync("claim-b", "ref-u", "fp-u", Kind, 500, "pending", "tok0", "{}");
        dup.IsSuccess.ShouldBeFalse();
        dup.Error.ShouldContain(AttachmentStorageFailure.ClaimDuplicateReserved);
    }

    [Fact]
    public async Task Partial_unique_index_permits_a_new_claim_after_the_prior_one_leaves_the_reserved_set()
    {
        (await _registry.UpsertConnectionAsync("ref-v", "org-v", "proj-v", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-v", "ref-v", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-a", "ref-v", "fp-v", Kind, 600, "active", "tok0", "{}")).IsSuccess.ShouldBeTrue();
        (await _registry.UpdateClaimStateAsync("claim-a", "tok0", "tok1", "released", DateTimeOffset.UtcNow, "{}")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-b", "ref-v", "fp-v", Kind, 600, "pending", "tok2", "{}")).IsSuccess.ShouldBeTrue();
    }

    // ── Two kinds with the same work_item_id do NOT collide on the
    //    partial unique index — AB#739 §Tuple storage requires the tuple
    //    to include primaryScopeKind so a future non-ADO scope can share
    //    the numeric id space without cross-supersession. ────────────────

    [Fact]
    public async Task Partial_unique_index_scopes_uniqueness_by_kind_so_two_kinds_can_share_a_work_item_id()
    {
        (await _registry.UpsertConnectionAsync("ref-k", "org-k", "proj-k", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-k", "ref-k", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-ado", "ref-k", "fp-k", Kind, 900, "active", "t0", "{}")).IsSuccess.ShouldBeTrue();
        // Different kind, same numeric id — MUST NOT collide.
        (await _registry.InsertClaimAsync("claim-other", "ref-k", "fp-k", "other-kind", 900, "active", "t1", "{}")).IsSuccess.ShouldBeTrue();

        var adoRow = await _registry.FindReservedClaimAsync("ref-k", Kind, 900, new[] { "pending", "active" });
        adoRow.Value.ShouldNotBeNull();
        adoRow.Value!.ClaimId.ShouldBe("claim-ado");

        var otherRow = await _registry.FindReservedClaimAsync("ref-k", "other-kind", 900, new[] { "pending", "active" });
        otherRow.Value.ShouldNotBeNull();
        otherRow.Value!.ClaimId.ShouldBe("claim-other");
    }

    [Fact]
    public async Task Supersede_and_activate_scopes_by_kind_so_a_second_kind_row_is_untouched()
    {
        (await _registry.UpsertConnectionAsync("ref-s", "org-s", "proj-s", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-s", "ref-s", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-a-ado", "ref-s", "fp-s", Kind, 555, "active", "casA", "{}")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-a-other", "ref-s", "fp-s", "other-kind", 555, "active", "casX", "{}")).IsSuccess.ShouldBeTrue();

        var supersede = await _registry.SupersedeAndActivateClaimAsync(
            newClaimId: "claim-b-ado",
            newCasToken: "casB",
            connectionRef: "ref-s",
            worktreeFingerprint: "fp-s",
            primaryScopeKind: Kind,
            workItemId: 555,
            newRecordJson: "{}",
            predecessorClaimId: "claim-a-ado",
            predecessorExpectedCasToken: "casA",
            predecessorNewCasToken: "casA-sup",
            predecessorRecordJson: "{}",
            transitionAt: DateTimeOffset.UtcNow);
        supersede.IsSuccess.ShouldBeTrue(supersede.Error);

        // The ADO row moved to superseded and a new active row exists.
        (await _registry.FindClaimAsync("claim-a-ado")).Value!.State.ShouldBe("superseded");
        (await _registry.FindClaimAsync("claim-b-ado")).Value!.State.ShouldBe("active");
        // The other-kind row is untouched.
        (await _registry.FindClaimAsync("claim-a-other")).Value!.State.ShouldBe("active");
    }

    [Fact]
    public async Task FindReserved_returns_matching_state_within_reserved_set()
    {
        (await _registry.UpsertConnectionAsync("ref-e", "org-e", "proj-e", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-e", "ref-e", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-p", "ref-e", "fp-e", Kind, 100, "pending", "t0", "{}")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-r", "ref-e", "fp-e", Kind, 101, "released", "t0", "{}")).IsSuccess.ShouldBeTrue();

        var pending = await _registry.FindReservedClaimAsync("ref-e", Kind, 100, new[] { "pending", "active" });
        pending.Value.ShouldNotBeNull();
        pending.Value!.ClaimId.ShouldBe("claim-p");

        var released = await _registry.FindReservedClaimAsync("ref-e", Kind, 101, new[] { "pending", "active" });
        released.Value.ShouldBeNull();
    }

    // ── CAS-guarded UpdateClaimState ────────────────────────────────────

    [Fact]
    public async Task UpdateClaimState_succeeds_when_expected_cas_token_matches()
    {
        (await _registry.UpsertConnectionAsync("ref-w", "org-w", "proj-w", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-w", "ref-w", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-cas", "ref-w", "fp-w", Kind, 700, "active", "cas-v0", "{}")).IsSuccess.ShouldBeTrue();

        var endedAt = new DateTimeOffset(2026, 5, 5, 5, 5, 5, TimeSpan.Zero);
        var upd = await _registry.UpdateClaimStateAsync("claim-cas", "cas-v0", "cas-v1", "released", endedAt, "{\"reason\":\"done\"}");
        upd.IsSuccess.ShouldBeTrue();

        var find = await _registry.FindClaimAsync("claim-cas");
        find.Value!.State.ShouldBe("released");
        find.Value.CasToken.ShouldBe("cas-v1");
        find.Value.EndedAt.ShouldBe(endedAt);
    }

    [Fact]
    public async Task UpdateClaimState_fails_with_cas_mismatch_when_expected_token_does_not_match()
    {
        (await _registry.UpsertConnectionAsync("ref-x", "org-x", "proj-x", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-x", "ref-x", "/wt")).IsSuccess.ShouldBeTrue();
        (await _registry.InsertClaimAsync("claim-cas2", "ref-x", "fp-x", Kind, 800, "active", "cas-v0", "{}")).IsSuccess.ShouldBeTrue();

        var upd = await _registry.UpdateClaimStateAsync("claim-cas2", "wrong-token", "cas-v1", "released", null, "{}");
        upd.IsSuccess.ShouldBeFalse();
        upd.Error.ShouldBe(AttachmentStorageFailure.ClaimCasMismatch);

        var find = await _registry.FindClaimAsync("claim-cas2");
        find.Value!.State.ShouldBe("active");
        find.Value.CasToken.ShouldBe("cas-v0");
    }

    [Fact]
    public async Task UpdateClaimState_fails_with_cas_mismatch_on_a_missing_claim()
    {
        var upd = await _registry.UpdateClaimStateAsync("no-such-claim", "any", "next", "released", null, "{}");
        upd.IsSuccess.ShouldBeFalse();
        upd.Error.ShouldBe(AttachmentStorageFailure.ClaimCasMismatch);
    }

    // ── Profile cache ────────────────────────────────────────────────

    [Fact]
    public async Task Profile_cache_write_read_round_trips()
    {
        (await _registry.UpsertConnectionAsync("ref-g", "org-g", "proj-g", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.WriteProfileCacheAsync("ref-g", "prof-id", "v1", "{\"types\":[]}")).IsSuccess.ShouldBeTrue();

        var read = await _registry.ReadProfileCacheAsync("ref-g");
        read.Value.ShouldNotBeNull();
        read.Value!.ProfileIdentity.ShouldBe("prof-id");
    }

    // ── layout_meta exact-version pre-check ─────────────────────────────

    [Fact]
    public async Task Reopen_with_bumped_schema_version_fails_before_running_ddl()
    {
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
    public async Task Reopen_of_valid_existing_db_does_not_reinitialize_schema()
    {
        var dbPath = Path.Combine(_dir, "system.db");
        (await _registry.UpsertConnectionAsync("ref-i", "org-i", "proj-i", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-i", "ref-i", "/wt")).IsSuccess.ShouldBeTrue();
        _registry.Dispose();

        using var reopened = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        var find = await reopened.FindWorktreeAsync("fp-i");
        find.Value.ShouldNotBeNull();
        find.Value!.ConnectionRef.ShouldBe("ref-i");
    }

    [Fact]
    public async Task Existing_db_missing_layout_meta_fails_closed()
    {
        var dbPath = Path.Combine(_dir, "system.db");
        (await _registry.UpsertConnectionAsync("ref-j", "org-j", "proj-j", team: null)).IsSuccess.ShouldBeTrue();
        _registry.Dispose();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE layout_meta;";
            cmd.ExecuteNonQuery();
        }
        using var reopened = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
        var find = await reopened.FindWorktreeAsync("anything");
        find.IsSuccess.ShouldBeFalse();
        find.Error.ShouldBe(AttachmentStorageFailure.SystemStoreSchemaMismatch);
    }

    // ── Concurrent open: a racing initializer succeeds once schema
    //    commits, so a caller that observes a mid-init file does NOT
    //    stick to schema-mismatch (AB#739 §Concurrency). ─────────────

    [Fact]
    public async Task Concurrent_open_of_a_new_db_does_not_stick_to_schema_mismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twig-sysreg-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "system.db");
        try
        {
            var registries = new List<SqliteSystemWorktreeRegistry>();
            try
            {
                var barrier = new System.Threading.Barrier(4);
                var tasks = new List<Task<bool>>();
                for (var i = 0; i < 4; i++)
                {
                    var reg = new SqliteSystemWorktreeRegistry(dbPath, TimeProvider.System);
                    registries.Add(reg);
                    tasks.Add(Task.Run(async () =>
                    {
                        barrier.SignalAndWait();
                        var upsert = await reg.UpsertConnectionAsync("ref-conc", "org-conc", "proj-conc", team: null);
                        return upsert.IsSuccess;
                    }));
                }
                var results = await Task.WhenAll(tasks);
                results.ShouldAllBe(r => r);
            }
            finally
            {
                foreach (var reg in registries) reg.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
